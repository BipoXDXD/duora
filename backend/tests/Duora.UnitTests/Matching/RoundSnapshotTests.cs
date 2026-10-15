using Duora.Core.Matching;
using static Duora.UnitTests.Matching.RoundBuilder;

namespace Duora.UnitTests.Matching;

/// <summary>O snapshot nasce válido: o seletor nunca recebe participante duplicado nem versão desconhecida.</summary>
public sealed class RoundSnapshotTests
{
    private static readonly DateTimeOffset WaitingSince = new(2026, 10, 15, 20, 50, 0, TimeSpan.Zero);

    [Fact]
    public void RejectsDuplicateCandidate()
    {
        var builder = new RoundBuilder().WithCandidate(1).WithCandidate(1);

        Assert.Throws<ArgumentException>(builder.Build);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RejectsUnknownAlgorithmVersion(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RoundSnapshot([], [], [], new RoundSeed(1), (PairingAlgorithmVersion)version));
    }

    [Fact]
    public void KeepsItsOwnCopyOfTheCandidates()
    {
        var candidates = new List<Candidate> { NewCandidate(User(1)) };
        var round = new RoundSnapshot(candidates, [], [], new RoundSeed(1), PairingAlgorithmVersion.GreedyByPriorityV1);

        candidates.Add(NewCandidate(User(2)));

        Assert.Single(round.Candidates);
    }

    [Fact]
    public void CandidateRejectsEmptyUserId()
    {
        Assert.Throws<ArgumentException>(() => NewCandidate(Guid.Empty));
    }

    [Fact]
    public void CandidateRejectsNegativeRoundsWithoutPair()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Candidate(User(1), WaitingSince, roundsWithoutPair: -1, Preferences.Unrestricted));
    }

    [Fact]
    public void CandidateAcceptsZeroRoundsWithoutPair()
    {
        var candidate = new Candidate(User(1), WaitingSince, roundsWithoutPair: 0, Preferences.Unrestricted);

        Assert.Equal(0, candidate.RoundsWithoutPair);
    }

    private static Candidate NewCandidate(Guid userId) => new(userId, WaitingSince, 0, Preferences.Unrestricted);
}
