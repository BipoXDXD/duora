using System.Buffers.Binary;
using CsCheck;
using Duora.Core.Matching;

namespace Duora.UnitTests.Matching;

/// <summary>
/// Invariantes do seletor sobre rodadas aleatórias de até 100 candidatos (spec 4.3). Cada execução sorteia novas
/// rodadas; quando uma propriedade falha, o CsCheck reduz o caso e imprime na mensagem a semente que o refaz
/// (<c>seed:</c> no <c>Sample</c> ou a variável de ambiente <c>CsCheck_Seed</c>), inclusive no log do CI.
/// </summary>
public sealed class PairSelectorPropertyTests
{
    private const int MaxCandidates = 100;
    private const int MaxMinutesWaiting = 5;
    private const int MaxRoundsWithoutPair = 2;
    private const int RoundsPerEvent = 3;
    private const long Iterations = 200;

    private static readonly DateTimeOffset RoundStart = new(2026, 10, 15, 21, 0, 0, TimeSpan.Zero);
    private static readonly string[] Criteria = ["c1", "c2"];
    private static readonly string[] Values = ["a", "b", "c"];

    /// <summary>Faixas pequenas de espera e de rodadas sem par forçam empates, que só a semente desfaz.</summary>
    private static readonly Gen<RoundCase> AnyRound =
        from count in Gen.Int[0, MaxCandidates]
        from idPrefix in Gen.Long
        from profiles in CandidateProfile.Array[count]
        from blocked in IndexPairs(count, maxCount: count * (count - 1) / 2)
        from met in IndexPairs(count, maxCount: RoundsPerEvent * count)
        from seed in Gen.Long
        select RoundCase.Create(idPrefix, profiles, blocked, met, seed);

    private static Gen<(int Minutes, int Rounds, Preferences Preferences)> CandidateProfile =>
        Gen.Select(Gen.Int[0, MaxMinutesWaiting], Gen.Int[0, MaxRoundsWithoutPair], AnyPreferences);

    /// <summary>Cada critério fica sem resposta em 1/4 dos casos e é exigido em 3/10, para haver incompatibilidade.</summary>
    private static Gen<Preferences> AnyPreferences =>
        Gen.Select(
            Gen.Int[0, Values.Length].Array[Criteria.Length],
            Gen.Select(Gen.Int[0, 9], Gen.Int[1, (1 << Values.Length) - 1]).Array[Criteria.Length],
            BuildPreferences);

    [Fact]
    public void NobodyAppearsInTwoPairs()
    {
        AnyRound.Sample(round =>
        {
            var members = Select(round).Pairs.SelectMany(pair => new[] { pair.UserLow, pair.UserHigh }).ToList();

            Assert.Equal(members.Count, members.Distinct().Count());
        }, iter: Iterations);
    }

    [Fact]
    public void NoPairIsBlocked()
    {
        AnyRound.Sample(round =>
        {
            Assert.DoesNotContain(Select(round).Pairs, round.BlockedPairs.Contains);
        }, iter: Iterations);
    }

    [Fact]
    public void NoPairRepeatsOneAlreadyFormedInTheEvent()
    {
        AnyRound.Sample(round =>
        {
            Assert.DoesNotContain(Select(round).Pairs, round.PriorPairings.Contains);
        }, iter: Iterations);
    }

    [Fact]
    public void EveryPairIsBilaterallyEligible()
    {
        AnyRound.Sample(round =>
        {
            Assert.All(Select(round).Pairs, pair =>
            {
                var low = round.CandidateOf(pair.UserLow).Preferences;
                var high = round.CandidateOf(pair.UserHigh).Preferences;
                Assert.True(low.Accepts(high) && high.Accepts(low));
            });
        }, iter: Iterations);
    }

    [Fact]
    public void PairedAndUnpairedPartitionTheCandidates()
    {
        AnyRound.Sample(round =>
        {
            var selection = Select(round);
            var paired = selection.Pairs.SelectMany(pair => new[] { pair.UserLow, pair.UserHigh });

            var everyone = paired.Concat(selection.Unpaired).ToList();

            Assert.Equal(round.Candidates.Length, everyone.Count);
            Assert.Equal(round.Candidates.Select(c => c.UserId).ToHashSet(), everyone.ToHashSet());
        }, iter: Iterations);
    }

    [Fact]
    public void SameInputAndSeedGiveTheSameSelectionWhateverTheInputOrder()
    {
        Gen.Select(AnyRound, Gen.Int).Sample((round, shuffleSeed) =>
        {
            var first = Select(round);
            var second = PairSelector.Select(round.Shuffled(shuffleSeed).ToSnapshot());

            Assert.Equal(first.Pairs, second.Pairs);
            Assert.Equal(first.Unpaired, second.Unpaired);
            Assert.Equal(first.UnpairedTally, second.UnpairedTally);
        }, iter: Iterations);
    }

    /// <summary>A gulosa devolve um emparelhamento maximal: sobra no máximo quem não tem aresta com outro que sobrou.</summary>
    [Fact]
    public void NoTwoUnpairedCandidatesCouldHaveBeenPaired()
    {
        AnyRound.Sample(round =>
        {
            var unpaired = Select(round).Unpaired;
            var options = round.ViableOptions();

            var missed = unpaired.SelectMany(user => unpaired.Where(options[user].Contains));

            Assert.Empty(missed);
        }, iter: Iterations);
    }

    [Fact]
    public void TallyCountsWhyCandidatesWereLeftUnpaired()
    {
        AnyRound.Sample(round =>
        {
            var selection = Select(round);
            var options = round.ViableOptions();

            var withoutPartner = selection.Unpaired.Count(user => options[user].Count == 0);

            Assert.Equal(
                new UnpairedTally(withoutPartner, selection.Unpaired.Count - withoutPartner),
                selection.UnpairedTally);
        }, iter: Iterations);
    }

    /// <summary>
    /// Prioridade, definida com precisão: se X tem prioridade estritamente maior que Y (mais rodadas sem par, ou o mesmo
    /// número e espera mais longa) e toda opção viável de Y, fora o próprio X, também é opção de X, então X nunca fica
    /// sem par enquanto Y é pareado. "Mesmo conjunto de arestas" é o caso particular da igualdade. Empates de
    /// prioridade ficam de fora: a semente os desfaz.
    /// </summary>
    [Fact]
    public void HigherPriorityIsNeverLeftOutForSomeoneWithNoMoreOptions()
    {
        AnyRound.Sample(round =>
        {
            var selection = Select(round);
            var options = round.ViableOptions();
            var unpaired = selection.Unpaired.Select(round.CandidateOf).ToList();
            var paired = round.Candidates.Except(unpaired).ToList();

            var overtaken = unpaired.SelectMany(x => paired
                .Where(y => HasHigherPriority(x, y) && HasNoMoreOptions(options, y, than: x))
                .Select(y => (LeftOut: x.UserId, Paired: y.UserId)));

            Assert.Empty(overtaken);
        }, iter: Iterations);
    }


    private static PairSelection Select(RoundCase round) => PairSelector.Select(round.ToSnapshot());

    private static bool HasHigherPriority(Candidate x, Candidate y) =>
        x.RoundsWithoutPair > y.RoundsWithoutPair
        || (x.RoundsWithoutPair == y.RoundsWithoutPair && x.WaitingSince < y.WaitingSince);

    private static bool HasNoMoreOptions(Dictionary<Guid, HashSet<Guid>> options, Candidate y, Candidate than) =>
        options[y.UserId].Where(user => user != than.UserId).All(options[than.UserId].Contains);

    private static Gen<(int, int)[]> IndexPairs(int count, int maxCount) =>
        count < 2
            ? Gen.Const(Array.Empty<(int, int)>())
            : Gen.Select(Gen.Int[0, count - 1], Gen.Int[0, count - 1])
                .Where(indexes => indexes.Item1 != indexes.Item2)
                .Array[0, maxCount];

    private static Preferences BuildPreferences(int[] ownValueIndexes, (int Roll, int Mask)[] requirements)
    {
        const int RequirementThreshold = 7;
        var own = new Dictionary<string, string>();
        var accepted = new Dictionary<string, IReadOnlySet<string>>();
        for (var criterion = 0; criterion < Criteria.Length; criterion++)
        {
            if (ownValueIndexes[criterion] < Values.Length)
            {
                own[Criteria[criterion]] = Values[ownValueIndexes[criterion]];
            }

            if (requirements[criterion].Roll >= RequirementThreshold)
            {
                var mask = requirements[criterion].Mask;
                accepted[Criteria[criterion]] = Values.Where((_, bit) => (mask & (1 << bit)) != 0).ToHashSet();
            }
        }

        return new Preferences(own, accepted);
    }

    /// <summary>Rodada gerada, com o oráculo das regras de aresta da spec 5.1 escrito de forma independente do seletor.</summary>
    private sealed record RoundCase(Candidate[] Candidates, UserPair[] BlockedPairs, UserPair[] PriorPairings, long Seed)
    {
        public static RoundCase Create(
            long idPrefix,
            (int Minutes, int Rounds, Preferences Preferences)[] profiles,
            (int, int)[] blocked,
            (int, int)[] met,
            long seed)
        {
            var candidates = profiles
                .Select((profile, index) => new Candidate(
                    UniqueUserId(idPrefix, index),
                    RoundStart.AddMinutes(-profile.Minutes),
                    profile.Rounds,
                    profile.Preferences))
                .ToArray();
            UserPair PairOf((int First, int Second) indexes) =>
                UserPair.Of(candidates[indexes.First].UserId, candidates[indexes.Second].UserId);

            return new RoundCase(candidates, [.. blocked.Select(PairOf)], [.. met.Select(PairOf)], seed);
        }

        public RoundSnapshot ToSnapshot() =>
            new(Candidates, BlockedPairs, PriorPairings, new RoundSeed(Seed), PairingAlgorithmVersion.GreedyByPriorityV1);

        public RoundCase Shuffled(int shuffleSeed)
        {
            var random = new Random(shuffleSeed);
            return this with
            {
                Candidates = Reordered(Candidates, random),
                BlockedPairs = Reordered(BlockedPairs, random),
                PriorPairings = Reordered(PriorPairings, random),
            };
        }

        public Candidate CandidateOf(Guid userId) => Candidates.Single(candidate => candidate.UserId == userId);

        /// <summary>Opções viáveis de cada candidato pelas regras de aresta, calculadas uma vez por rodada.</summary>
        public Dictionary<Guid, HashSet<Guid>> ViableOptions()
        {
            var excluded = BlockedPairs.Concat(PriorPairings).ToHashSet();
            bool IsViable(Candidate a, Candidate b) =>
                a.UserId != b.UserId
                && !excluded.Contains(UserPair.Of(a.UserId, b.UserId))
                && a.Preferences.Accepts(b.Preferences)
                && b.Preferences.Accepts(a.Preferences);

            return Candidates.ToDictionary(
                candidate => candidate.UserId,
                candidate => Candidates.Where(other => IsViable(candidate, other)).Select(other => other.UserId).ToHashSet());
        }

        public override string ToString() =>
            $"{Candidates.Length} candidatos, {BlockedPairs.Length} bloqueios, {PriorPairings.Length} pares anteriores, semente {Seed}";

        /// <summary>O índice ocupa os 8 bytes finais: ids distintos por construção, mesmo quando o CsCheck reduz o prefixo.</summary>
        private static Guid UniqueUserId(long prefix, int index)
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteInt64BigEndian(bytes, prefix);
            BinaryPrimitives.WriteInt64BigEndian(bytes[8..], index + 1L);
            return new Guid(bytes, bigEndian: true);
        }

        private static T[] Reordered<T>(T[] items, Random random)
        {
            var copy = items.ToArray();
            random.Shuffle(copy);
            return copy;
        }
    }
}
