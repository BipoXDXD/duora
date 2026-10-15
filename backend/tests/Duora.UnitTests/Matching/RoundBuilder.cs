using Duora.Core.Matching;

namespace Duora.UnitTests.Matching;

/// <summary>Monta snapshots de rodada legíveis: só o que importa para cada caso aparece no teste.</summary>
internal sealed class RoundBuilder
{
    private static readonly DateTimeOffset RoundStart = new(2026, 10, 15, 21, 0, 0, TimeSpan.Zero);

    private readonly List<Candidate> _candidates = [];
    private readonly List<UserPair> _blockedPairs = [];
    private readonly List<UserPair> _priorEncounters = [];
    private long _seed = 1;

    /// <summary>Conta sintética com UUID previsível; a ordem numérica de <paramref name="number"/> é a ordem do UUID.</summary>
    public static Guid User(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:D12}");

    public RoundBuilder WithCandidate(
        int user,
        int waitingMinutes = 0,
        int roundsWithoutPair = 0,
        Preferences? preferences = null)
    {
        _candidates.Add(new Candidate(
            User(user),
            RoundStart.AddMinutes(-waitingMinutes),
            roundsWithoutPair,
            preferences ?? Preferences.Unrestricted));
        return this;
    }

    public RoundBuilder WithBlock(int blocker, int blocked)
    {
        _blockedPairs.Add(UserPair.Of(User(blocker), User(blocked)));
        return this;
    }

    public RoundBuilder WithPriorEncounter(int first, int second)
    {
        _priorEncounters.Add(UserPair.Of(User(first), User(second)));
        return this;
    }

    public RoundBuilder WithSeed(long seed)
    {
        _seed = seed;
        return this;
    }

    public RoundSnapshot Build() =>
        new(_candidates, _blockedPairs, _priorEncounters, new RoundSeed(_seed), PairingAlgorithmVersion.GreedyByPriorityV1);
}
