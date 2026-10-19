using Duora.Core.Matching;

namespace Duora.UnitTests.Matching;

public sealed class LanguageMatchingTests
{
    [Fact]
    public void NewAlgorithmRejectsUndeclaredLegacyCandidates()
    {
        var round = new RoundBuilder().WithCandidate(1).WithCandidate(2).Build();
        var v2 = new RoundSnapshot(round.Candidates, [], [], round.Seed, (PairingAlgorithmVersion)2);
        Assert.Empty(PairSelector.Select(v2).Pairs);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void DifferentLocalesRequireBothOptIns(bool first, bool second, bool expected)
    {
        var agreement = CommunicationPolicy.Agree(new("en-US", ["en", "pt"], first), new("pt-BR", ["pt", "en"], second));
        Assert.Equal(expected, agreement is not null);
        if (expected)
        {
            Assert.Equal(new CommunicationAgreement("en", 1), agreement);
        }
    }

    [Fact]
    public void SameLocaleStillRequiresIntersection()
    {
        Assert.Null(CommunicationPolicy.Agree(new("en-US", ["en"], false), new("en-US", ["pt"], false)));
        Assert.Equal("pt", CommunicationPolicy.Agree(new("en-US", ["pt"], false), new("en-US", ["pt"], false))!.Language);
    }

    [Fact]
    public void LongWaitNeverRelaxesLanguageAndBlocksStillPrevail()
    {
        var a = new Candidate(RoundBuilder.User(1), DateTimeOffset.MinValue, 10000, Preferences.Unrestricted, new("en-US", ["en"], true));
        var b = new Candidate(RoundBuilder.User(2), DateTimeOffset.MinValue, 10000, Preferences.Unrestricted, new("pt-BR", ["pt"], true));
        Assert.Empty(PairSelector.Select(new([a, b], [], [], new(42), PairingAlgorithmVersion.GreedyByPriorityV2)).Pairs);
        b = new Candidate(b.UserId, b.WaitingSince, b.RoundsWithoutPair, b.Preferences, new("pt-BR", ["en"], true));
        var paired = PairSelector.Select(new([b, a], [], [], new(42), PairingAlgorithmVersion.GreedyByPriorityV2));
        Assert.Equal(new CommunicationAgreement("en", 1), Assert.Single(paired.CommunicationAgreements).Value);
        Assert.Empty(PairSelector.Select(new([a, b], [UserPair.Of(a.UserId, b.UserId)], [], new(42), PairingAlgorithmVersion.GreedyByPriorityV2)).Pairs);
    }

    [Fact]
    public void RegionalCriterionRemainsIndependentFromLanguageAndDisplayLocale()
    {
        // Opaque synthetic values preserve the pending geographic taxonomy.
        var restricted = new Preferences(new Dictionary<string, string> { ["region"] = "area-a" },
            new Dictionary<string, IReadOnlySet<string>> { ["region"] = new HashSet<string> { "area-a" } });
        var outside = new Preferences(new Dictionary<string, string> { ["region"] = "area-b" },
            new Dictionary<string, IReadOnlySet<string>>());
        var a = new Candidate(RoundBuilder.User(1), DateTimeOffset.MinValue, 10000, restricted, new("en-US", ["en"], true));
        var b = new Candidate(RoundBuilder.User(2), DateTimeOffset.MinValue, 10000, outside, new("pt-BR", ["en"], true));
        Assert.Empty(PairSelector.Select(new([a, b], [], [], new(42), PairingAlgorithmVersion.GreedyByPriorityV2)).Pairs);
        b = new Candidate(b.UserId, b.WaitingSince, b.RoundsWithoutPair, restricted, b.Communication);
        Assert.Single(PairSelector.Select(new([a, b], [], [], new(42), PairingAlgorithmVersion.GreedyByPriorityV2)).Pairs);
        b = new Candidate(b.UserId, b.WaitingSince, b.RoundsWithoutPair, restricted, new("pt-BR", ["pt"], true));
        Assert.Empty(PairSelector.Select(new([a, b], [], [], new(42), PairingAlgorithmVersion.GreedyByPriorityV2)).Pairs);
    }

    [Fact]
    public void DeclarationCopiesAndRejectsInvalidLanguages()
    {
        string[] languages = ["en"];
        var declaration = new CommunicationDeclaration("pt-BR", languages, false);
        languages[0] = "pt";
        Assert.Contains("en", declaration.CommunicationLanguages);
        Assert.Throws<ArgumentException>(() => new CommunicationDeclaration("en-US", [], false));
        Assert.Throws<ArgumentException>(() => new CommunicationDeclaration("en-US", ["en", "en"], false));
        Assert.Throws<ArgumentException>(() => new CommunicationDeclaration("en-US", ["EN"], false));
    }
}
