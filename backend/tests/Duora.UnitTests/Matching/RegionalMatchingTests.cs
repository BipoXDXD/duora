using CsCheck;
using Duora.Core.Matching;

namespace Duora.UnitTests.Matching;

public sealed class RegionalMatchingTests
{
    [Theory]
    [InlineData("BR", "BR", "BR", "BR", true)]
    [InlineData("BR", "US", "US", "BR", true)]
    [InlineData("BR", "US", "US", "US", false)]
    [InlineData("BR", "BR", "US", "US", false)]
    [InlineData(null, "BR", "BR", "BR", false)]
    [InlineData(null, "BR", "BR", null, true)]
    [InlineData(null, null, null, null, true)]
    public void CountryAcceptanceIsBilateralAndUsesDeclarations(string? firstCountry, string? secondCountry,
        string? firstAccepts, string? secondAccepts, bool paired)
    {
        var first = Person(1, firstCountry, firstAccepts);
        var second = Person(2, secondCountry, secondAccepts);
        var result = Select(first, second);
        Assert.Equal(paired, result.Pairs.Count == 1);
        Assert.Equal((PairingAlgorithmVersion)3, result.AlgorithmVersion);
        Assert.Equal(paired ? 1 : 0, result.CommunicationAgreements.Count);
        if (paired)
        { Assert.Equal(new CommunicationAgreement("en", 1), Assert.Single(result.CommunicationAgreements).Value); }
    }

    [Theory]
    [InlineData("pt-BR", "en", false, true)]
    [InlineData("pt-BR", "en", true, false)]
    [InlineData("en-US", "pt", true, true)]
    public void RegionAndLongWaitNeverReplaceCommonLanguageOrBilateralLocaleConsent(string locale, string language, bool firstOptIn, bool secondOptIn)
    {
        var first = Person(1, "BR", "BR", new("en-US", ["en"], firstOptIn));
        var second = Person(2, "BR", "BR", new(locale, [language], secondOptIn));
        Assert.Empty(Select(first, second).Pairs);
    }

    [Fact]
    public void RegionalRestrictionsPreservePriorityBlocksAndPriorPairs()
    {
        var candidates = new[] { Person(1, "BR", "BR"), Person(2, "US", null), Person(3, "BR", null), Person(4, "BR", null) };
        var blocked = new[] { UserPair.Of(candidates[0].UserId, candidates[2].UserId) };
        var result = PairSelector.Select(new(candidates, blocked, [], new(42), (PairingAlgorithmVersion)3));
        Assert.Contains(UserPair.Of(candidates[0].UserId, candidates[3].UserId), result.Pairs);
        var prior = new[] { UserPair.Of(candidates[0].UserId, candidates[3].UserId) };
        result = PairSelector.Select(new(candidates, blocked, prior, new(42), (PairingAlgorithmVersion)3));
        Assert.DoesNotContain(result.Pairs, p => p.UserLow == candidates[0].UserId || p.UserHigh == candidates[0].UserId);
    }

    [Fact]
    public void RandomizedV3RoundsRemainDeterministicBilateralAndMaximal()
    {
        Gen.Int[0, 5].Array[30].Sample(values =>
        {
            var candidates = values.Select((value, index) => new Candidate(
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"), DateTimeOffset.UnixEpoch.AddMinutes(index), index % 3,
                new Preferences(new Dictionary<string, string> { ["country"] = value % 2 == 0 ? "BR" : "US" },
                    value < 2 ? new Dictionary<string, IReadOnlySet<string>>() :
                        new() { ["country"] = new HashSet<string> { value < 4 ? "BR" : "US" } }),
                new("en-US", [value % 3 == 0 ? "pt" : "en"], false))).ToArray();
            var blocked = new[] { UserPair.Of(candidates[0].UserId, candidates[1].UserId) };
            var prior = new[] { UserPair.Of(candidates[2].UserId, candidates[3].UserId) };
            var result = PairSelector.Select(new(candidates, blocked, prior, new(17), PairingAlgorithmVersion.GreedyByPriorityV3));
            var shuffled = PairSelector.Select(new(candidates.Reverse(), blocked, prior, new(17), PairingAlgorithmVersion.GreedyByPriorityV3));
            Assert.Equal(result.Pairs, shuffled.Pairs);
            var paired = result.Pairs.SelectMany(p => new[] { p.UserLow, p.UserHigh }).ToArray();
            Assert.Equal(paired.Length, paired.Distinct().Count());
            Assert.Equal(candidates.Length, paired.Length + result.Unpaired.Count);
            foreach (var pair in result.Pairs)
            {
                Assert.DoesNotContain(pair, blocked);
                Assert.DoesNotContain(pair, prior);
                var a = candidates.Single(c => c.UserId == pair.UserLow);
                var b = candidates.Single(c => c.UserId == pair.UserHigh);
                Assert.True(a.Preferences.Accepts(b.Preferences) && b.Preferences.Accepts(a.Preferences));
                Assert.Equal(a.Communication!.CommunicationLanguages, b.Communication!.CommunicationLanguages);
                Assert.True(result.CommunicationAgreements.ContainsKey(pair));
            }
            foreach (var a in candidates.Where(c => result.Unpaired.Contains(c.UserId)))
            {
                foreach (var b in candidates.Where(c => result.Unpaired.Contains(c.UserId) && c.UserId != a.UserId))
                {
                    var pair = UserPair.Of(a.UserId, b.UserId);
                    Assert.True(blocked.Contains(pair) || prior.Contains(pair) || !a.Preferences.Accepts(b.Preferences)
                        || !b.Preferences.Accepts(a.Preferences) || !a.Communication!.CommunicationLanguages.Intersect(b.Communication!.CommunicationLanguages).Any());
                }
            }
        }, iter: 200);
    }

    private static PairSelection Select(params Candidate[] candidates) =>
        PairSelector.Select(new(candidates, [], [], new(42), (PairingAlgorithmVersion)3));

    private static Candidate Person(int id, string? country, string? accepts, CommunicationDeclaration? communication = null) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{id:D12}"), DateTimeOffset.UnixEpoch.AddMinutes(id), 5 - id,
            new Preferences(country is null ? new Dictionary<string, string>() : new() { ["country"] = country },
                accepts is null ? new Dictionary<string, IReadOnlySet<string>>() : new() { ["country"] = new HashSet<string> { accepts } }),
            communication ?? new("en-US", ["en"], true));
}
