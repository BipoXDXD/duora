using System.Buffers.Binary;
using System.Collections.Frozen;

namespace Duora.Core.Matching;

/// <summary>
/// Seleção determinística de pares de uma rodada (spec 5.1, ADR-0012), versão <see cref="PairingAlgorithmVersion.GreedyByPriorityV1"/>.
/// Função pura: sem I/O, sem relógio e sem aleatoriedade além da semente da rodada.
/// <list type="number">
/// <item>Aresta <c>A ↔ B</c> só existe sem bloqueio em qualquer direção, sem par já formado no evento e com
/// aceitação nos dois sentidos. Restrições eliminam a aresta antes de qualquer ordenação.</item>
/// <item>Ordena os candidatos por prioridade: mais rodadas sem par, depois espera mais longa, depois uma chave
/// derivada da semente e da conta, depois a própria conta.</item>
/// <item>Percorre nessa ordem; quem ainda está livre forma par com o vizinho livre de maior prioridade.</item>
/// </list>
/// A gulosa devolve um emparelhamento maximal, não máximo: pode formar menos pares que o ótimo global (spec 5.1).
/// Em troca, quem tem prioridade maior nunca fica sem par enquanto alguém de prioridade menor, sem opções além das
/// dele, é pareado.
/// </summary>
public static class PairSelector
{
    private const int Unpaired = -1;

    /// <summary>
    /// Executa a versão pedida pelo snapshot. Cada braço declara a versão que de fato rodou; versão sem implementação
    /// falha alto em vez de devolver um resultado rotulado com uma versão que não executou.
    /// </summary>
    public static PairSelection Select(RoundSnapshot round)
    {
        ArgumentNullException.ThrowIfNull(round);

        return round.AlgorithmVersion switch
        {
            PairingAlgorithmVersion.GreedyByPriorityV1 or PairingAlgorithmVersion.GreedyByPriorityV2 => SelectGreedyByPriority(round),
            _ => throw new InvalidOperationException($"Versão de algoritmo sem implementação: {round.AlgorithmVersion}."),
        };
    }

    private static PairSelection SelectGreedyByPriority(RoundSnapshot round)
    {
        var ranked = RankByPriority(round);
        var options = ViableOptionsByRank(round, ranked);
        var partnerOf = PairGreedily(options);

        var pairs = PairsOf(ranked, partnerOf);
        var candidates = ranked.ToDictionary(candidate => candidate.UserId);
        var agreements = round.AlgorithmVersion == PairingAlgorithmVersion.GreedyByPriorityV2
            ? pairs.ToFrozenDictionary(pair => pair, pair => CommunicationPolicy.Agree(
                candidates[pair.UserLow].Communication, candidates[pair.UserHigh].Communication)!)
            : FrozenDictionary<UserPair, CommunicationAgreement>.Empty;
        return new PairSelection(
            pairs,
            [.. UnpairedRanks(partnerOf).Select(rank => ranked[rank].UserId)],
            TallyUnpaired(options, partnerOf),
            round.AlgorithmVersion, agreements);
    }

    private static Candidate[] RankByPriority(RoundSnapshot round)
    {
        var seed = round.Seed.Value;
        return [.. round.Candidates
            .OrderByDescending(candidate => candidate.RoundsWithoutPair)
            .ThenBy(candidate => candidate.WaitingSince)
            .ThenBy(candidate => TieBreakKey(seed, candidate.UserId))
            .ThenBy(candidate => candidate.UserId)];
    }

    /// <summary>Opções de cada candidato, por posição no ranking e já em ordem de prioridade.</summary>
    private static List<int>[] ViableOptionsByRank(RoundSnapshot round, Candidate[] ranked)
    {
        var options = ranked.Select(_ => new List<int>()).ToArray();
        for (var first = 0; first < ranked.Length; first++)
        {
            for (var second = first + 1; second < ranked.Length; second++)
            {
                if (IsViableEdge(round, ranked[first], ranked[second]))
                {
                    options[first].Add(second);
                    options[second].Add(first);
                }
            }
        }

        return options;
    }

    private static bool IsViableEdge(RoundSnapshot round, Candidate first, Candidate second)
    {
        var pair = UserPair.Of(first.UserId, second.UserId);
        return !round.BlockedPairs.Contains(pair)
            && !round.PriorPairings.Contains(pair)
            && first.Preferences.Accepts(second.Preferences)
            && second.Preferences.Accepts(first.Preferences)
            && (round.AlgorithmVersion == PairingAlgorithmVersion.GreedyByPriorityV1
                || CommunicationPolicy.Agree(first.Communication, second.Communication) is not null);
    }

    /// <summary>Devolve, por posição no ranking, a posição do par formado, ou <see cref="Unpaired"/>.</summary>
    private static int[] PairGreedily(List<int>[] options)
    {
        var partnerOf = Enumerable.Repeat(Unpaired, options.Length).ToArray();
        for (var rank = 0; rank < options.Length; rank++)
        {
            if (partnerOf[rank] != Unpaired)
            {
                continue;
            }

            var partner = options[rank].FirstOrDefault(option => partnerOf[option] == Unpaired, Unpaired);
            if (partner != Unpaired)
            {
                partnerOf[rank] = partner;
                partnerOf[partner] = rank;
            }
        }

        return partnerOf;
    }

    private static UserPair[] PairsOf(Candidate[] ranked, int[] partnerOf) =>
        [.. Enumerable.Range(0, ranked.Length)
            .Where(rank => partnerOf[rank] > rank)
            .Select(rank => UserPair.Of(ranked[rank].UserId, ranked[partnerOf[rank]].UserId))];

    private static IEnumerable<int> UnpairedRanks(int[] partnerOf) =>
        Enumerable.Range(0, partnerOf.Length).Where(rank => partnerOf[rank] == Unpaired);

    private static UnpairedTally TallyUnpaired(List<int>[] options, int[] partnerOf)
    {
        var unpaired = UnpairedRanks(partnerOf).ToList();
        var withoutCompatiblePartner = unpaired.Count(rank => options[rank].Count == 0);
        return new UnpairedTally(withoutCompatiblePartner, unpaired.Count - withoutCompatiblePartner);
    }

    /// <summary>
    /// Chave de desempate estável entre processos, máquinas e versões do .NET, para que a semente persistida refaça a
    /// seleção. Por isso não usa <see cref="HashCode"/> (aleatório por processo) nem <see cref="Random"/> (algoritmo
    /// sem garantia entre versões): aplica o finalizador do SplitMix64 aos bytes do UUID em big-endian, a ordem do
    /// PostgreSQL.
    /// </summary>
    private static ulong TieBreakKey(long seed, Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes, bigEndian: true, out _);
        var high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return Mix(Mix(unchecked((ulong)seed) ^ high) ^ low);
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
            return value ^ (value >> 31);
        }
    }
}
