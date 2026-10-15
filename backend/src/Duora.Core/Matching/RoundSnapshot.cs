using System.Collections.Frozen;

namespace Duora.Core.Matching;

/// <summary>
/// Entrada imutável da seleção de pares de uma rodada. Nasce válida: sem candidato repetido e com uma
/// versão de algoritmo conhecida, para que o seletor não precise se defender.
/// </summary>
public sealed class RoundSnapshot
{
    public RoundSnapshot(
        IEnumerable<Candidate> candidates,
        IEnumerable<UserPair> blockedPairs,
        IEnumerable<UserPair> priorPairings,
        RoundSeed seed,
        PairingAlgorithmVersion algorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(blockedPairs);
        ArgumentNullException.ThrowIfNull(priorPairings);
        if (!Enum.IsDefined(algorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion), algorithmVersion, "Versão de algoritmo desconhecida.");
        }

        Candidates = RequireDistinct(candidates.ToArray()).AsReadOnly();
        BlockedPairs = blockedPairs.ToFrozenSet();
        PriorPairings = priorPairings.ToFrozenSet();
        Seed = seed;
        AlgorithmVersion = algorithmVersion;
    }

    public IReadOnlyList<Candidate> Candidates { get; }

    /// <summary>Pares com bloqueio em qualquer direção; pode citar contas fora da rodada.</summary>
    public IReadOnlySet<UserPair> BlockedPairs { get; }

    /// <summary>Pares já formados em rodadas anteriores deste evento (spec 4.3: não repetir o par no evento).</summary>
    public IReadOnlySet<UserPair> PriorPairings { get; }

    public RoundSeed Seed { get; }

    public PairingAlgorithmVersion AlgorithmVersion { get; }

    private static Candidate[] RequireDistinct(Candidate[] candidates)
    {
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate, nameof(candidates));
        }

        if (candidates.DistinctBy(candidate => candidate.UserId).Count() != candidates.Length)
        {
            throw new ArgumentException("A mesma conta aparece mais de uma vez na rodada.", nameof(candidates));
        }

        return candidates;
    }
}
