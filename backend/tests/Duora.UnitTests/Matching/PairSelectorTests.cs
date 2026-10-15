using Duora.Core.Matching;
using static Duora.UnitTests.Matching.RoundBuilder;

namespace Duora.UnitTests.Matching;

public sealed class PairSelectorTests
{
    private const string Intent = "intencao";

    private static readonly Guid[] UuidV7Ids =
    [
        Guid.Parse("019a0e5c-3f21-7a4b-9c3d-5e6f7a8b9c0d"),
        Guid.Parse("019a0e5c-3f22-7b1e-8d2a-1f3e5a7c9b2e"),
        Guid.Parse("019a0e5d-a401-7c9f-b3e4-6a7b8c9d0e1f"),
        Guid.Parse("019a0e61-07be-7d02-a5f6-2b4c6d8e0a13"),
        Guid.Parse("019a0e6a-c9d4-7e38-94a1-7c0b3e5f2d46"),
        Guid.Parse("019a0e73-5512-7f6d-bb07-d8e2f4a6c193"),
    ];

    [Fact]
    public void EmptyRoundSelectsNothing()
    {
        var selection = PairSelector.Select(new RoundBuilder().Build());

        Assert.Empty(selection.Pairs);
        Assert.Empty(selection.Unpaired);
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 0, CompatiblePartnersTaken: 0), selection.UnpairedTally);
    }

    [Fact]
    public void SingleCandidateStaysUnpairedWithoutCompatiblePartner()
    {
        var selection = PairSelector.Select(new RoundBuilder().WithCandidate(1).Build());

        Assert.Empty(selection.Pairs);
        Assert.Equal([User(1)], selection.Unpaired);
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 1, CompatiblePartnersTaken: 0), selection.UnpairedTally);
    }

    [Fact]
    public void TwoCompatibleCandidatesFormOneNormalizedPair()
    {
        var round = new RoundBuilder().WithCandidate(2).WithCandidate(1).Build();

        var selection = PairSelector.Select(round);

        var pair = Assert.Single(selection.Pairs);
        Assert.Equal(User(1), pair.UserLow);
        Assert.Equal(User(2), pair.UserHigh);
        Assert.Empty(selection.Unpaired);
    }

    [Fact]
    public void OddRoundLeavesTheCandidateWhoWaitedLeastUnpaired()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, waitingMinutes: 10)
            .WithCandidate(2, waitingMinutes: 30)
            .WithCandidate(3, waitingMinutes: 20)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(2), User(3))], selection.Pairs);
        Assert.Equal([User(1)], selection.Unpaired);
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 0, CompatiblePartnersTaken: 1), selection.UnpairedTally);
    }

    [Fact]
    public void RoundsWithoutPairOutrankWaitingTime()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, waitingMinutes: 0, roundsWithoutPair: 1)
            .WithCandidate(2, waitingMinutes: 50)
            .WithCandidate(3, waitingMinutes: 40)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(1), User(2))], selection.Pairs);
        Assert.Equal([User(3)], selection.Unpaired);
    }

    [Fact]
    public void HighestPriorityCandidateIsPairedWithTheHighestPriorityAvailablePartner()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, waitingMinutes: 40)
            .WithCandidate(2, waitingMinutes: 10)
            .WithCandidate(3, waitingMinutes: 30)
            .WithCandidate(4, waitingMinutes: 20)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(1), User(3)), UserPair.Of(User(2), User(4))], selection.Pairs);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void BlockInEitherDirectionRemovesThePair(int blocker, int blocked)
    {
        var round = new RoundBuilder().WithCandidate(1).WithCandidate(2).WithBlock(blocker, blocked).Build();

        var selection = PairSelector.Select(round);

        Assert.Empty(selection.Pairs);
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 2, CompatiblePartnersTaken: 0), selection.UnpairedTally);
    }

    [Fact]
    public void BlockRemovesOnlyItsOwnEdge()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, waitingMinutes: 30)
            .WithCandidate(2, waitingMinutes: 20)
            .WithCandidate(3, waitingMinutes: 10)
            .WithBlock(1, 2)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(1), User(3))], selection.Pairs);
        Assert.Equal([User(2)], selection.Unpaired);
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 0, CompatiblePartnersTaken: 1), selection.UnpairedTally);
    }

    [Fact]
    public void PairAlreadyFormedInTheEventIsNotRepeated()
    {
        var round = new RoundBuilder().WithCandidate(1).WithCandidate(2).WithPriorPairing(2, 1).Build();

        var selection = PairSelector.Select(round);

        Assert.Empty(selection.Pairs);
        Assert.Equal([User(1), User(2)], selection.Unpaired.Order());
    }

    [Fact]
    public void RoundWhereEveryoneIsBlockedSelectsNoPairs()
    {
        var round = new RoundBuilder()
            .WithCandidate(1).WithCandidate(2).WithCandidate(3).WithCandidate(4)
            .WithBlock(1, 2).WithBlock(1, 3).WithBlock(4, 1).WithBlock(2, 3).WithBlock(2, 4).WithBlock(3, 4)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Empty(selection.Pairs);
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 4, CompatiblePartnersTaken: 0), selection.UnpairedTally);
    }

    [Fact]
    public void RoundWhereEveryoneAlreadyMetSelectsNoPairs()
    {
        var round = new RoundBuilder()
            .WithCandidate(1).WithCandidate(2).WithCandidate(3)
            .WithPriorPairing(1, 2).WithPriorPairing(1, 3).WithPriorPairing(2, 3)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Empty(selection.Pairs);
        Assert.Equal(3, selection.Unpaired.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneSidedAcceptanceIsNotAnEdge(bool firstCandidateRestricts)
    {
        var restrictive = PreferencesOf(own: [(Intent, "amizade")], accepts: [(Intent, ["amizade"])]);
        var other = PreferencesOf(own: [(Intent, "romance")], accepts: []);
        var round = new RoundBuilder()
            .WithCandidate(1, preferences: firstCandidateRestricts ? restrictive : other)
            .WithCandidate(2, preferences: firstCandidateRestricts ? other : restrictive)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Empty(selection.Pairs);
    }

    [Fact]
    public void BilateralAcceptanceFormsThePair()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, preferences: PreferencesOf(own: [(Intent, "amizade")], accepts: [(Intent, ["amizade", "romance"])]))
            .WithCandidate(2, preferences: PreferencesOf(own: [(Intent, "romance")], accepts: [(Intent, ["amizade"])]))
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(1), User(2))], selection.Pairs);
    }

    [Fact]
    public void RequirementOnAnUndeclaredCriterionIsNotInferred()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, preferences: PreferencesOf(own: [], accepts: [(Intent, ["amizade"])]))
            .WithCandidate(2, preferences: PreferencesOf(own: [], accepts: []))
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Empty(selection.Pairs);
    }

    /// <summary>
    /// Documenta o limite aceito na spec 5.1 e no ADR-0012: arestas 1–2, 2–3 e 3–4. O máximo seria {1–2, 3–4},
    /// mas a prioridade de 2 e 3 os une e deixa 1 e 4 sem par.
    /// </summary>
    [Fact]
    public void GreedySelectionCanFormFewerPairsThanTheMaximum()
    {
        var round = new RoundBuilder()
            .WithCandidate(1, waitingMinutes: 20)
            .WithCandidate(2, waitingMinutes: 40)
            .WithCandidate(3, waitingMinutes: 30)
            .WithCandidate(4, waitingMinutes: 10)
            .WithPriorPairing(1, 3).WithPriorPairing(1, 4).WithPriorPairing(2, 4)
            .Build();

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(2), User(3))], selection.Pairs);
        Assert.Equal([User(1), User(4)], selection.Unpaired.Order());
        Assert.Equal(new UnpairedTally(WithoutCompatiblePartner: 0, CompatiblePartnersTaken: 2), selection.UnpairedTally);
    }

    /// <summary>
    /// Golden master da reprodutibilidade: a semente persistida precisa refazer a mesma seleção em outra versão do
    /// .NET ou noutra máquina. Se este teste mudar, a versão do algoritmo também muda. O valor esperado foi conferido
    /// com uma implementação independente do SplitMix64: com a semente 42, a ordem de desempate é 4, 2, 1, 3.
    /// </summary>
    [Fact]
    public void SameSeedReproducesThePinnedTieBreak()
    {
        var round = TiedRound(seed: 42);

        var selection = PairSelector.Select(round);

        Assert.Equal([UserPair.Of(User(2), User(4)), UserPair.Of(User(1), User(3))], selection.Pairs);
    }

    /// <summary>
    /// Golden master com UUIDv7 reais, que variam nos 16 bytes: os ids de <c>User(n)</c> zeram os 8 bytes iniciais e
    /// não pegariam uma troca de endianness nem a perda da metade alta na chave de desempate. Com a semente 20261015, a
    /// implementação independente em Python dá a ordem de desempate 2, 5, 3, 0, 1, 4 (índices de <see cref="UuidV7Ids"/>).
    /// </summary>
    [Fact]
    public void SameSeedReproducesThePinnedTieBreakForFullWidthUuids()
    {
        var builder = new RoundBuilder().WithSeed(20261015);
        foreach (var userId in UuidV7Ids)
        {
            builder.WithCandidate(userId);
        }

        var selection = PairSelector.Select(builder.Build());

        Assert.Equal(
            [
                UserPair.Of(UuidV7Ids[2], UuidV7Ids[5]),
                UserPair.Of(UuidV7Ids[0], UuidV7Ids[3]),
                UserPair.Of(UuidV7Ids[1], UuidV7Ids[4]),
            ],
            selection.Pairs);
    }

    [Fact]
    public void DifferentSeedsBreakTiesDifferently()
    {
        var outcomes = Enumerable.Range(1, 32)
            .Select(seed => PairSelector.Select(TiedRound(seed)))
            .Select(selection => string.Join(";", selection.Pairs))
            .Distinct();

        Assert.True(outcomes.Count() > 1, "A semente não alterou nenhum desempate em 32 rodadas empatadas.");
    }

    [Fact]
    public void SelectionReportsTheAlgorithmVersionUsed()
    {
        var selection = PairSelector.Select(new RoundBuilder().WithCandidate(1).Build());

        Assert.Equal(PairingAlgorithmVersion.GreedyByPriorityV1, selection.AlgorithmVersion);
    }

    private static RoundSnapshot TiedRound(long seed) =>
        new RoundBuilder().WithCandidate(1).WithCandidate(2).WithCandidate(3).WithCandidate(4).WithSeed(seed).Build();

    private static Preferences PreferencesOf(
        (string Criterion, string Value)[] own,
        (string Criterion, string[] Values)[] accepts) =>
        new(
            own.ToDictionary(answer => answer.Criterion, answer => answer.Value),
            accepts.ToDictionary(
                requirement => requirement.Criterion,
                requirement => (IReadOnlySet<string>)requirement.Values.ToHashSet()));
}
