using Duora.Core.Geolocation;

namespace Duora.UnitTests.Geolocation;

public sealed class DatabaseAgePolicyTests
{
    private static readonly DateTimeOffset Published = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(259199, true, false)]
    [InlineData(259200, true, true)]
    [InlineData(604799, true, true)]
    [InlineData(604800, false, true)]
    [InlineData(-1, false, true)]
    public void HonorsExactBoundaries(int seconds, bool canSuggest, bool needsAlert)
    {
        var state = DatabaseAgePolicy.Evaluate(Published, Published.AddSeconds(seconds));
        Assert.Equal(canSuggest, state.CanSuggest);
        Assert.Equal(needsAlert, state.NeedsAlert);
    }

    [Fact]
    public void MissingPublicationCannotBeReplacedByDownloadTime()
    {
        var state = DatabaseAgePolicy.Evaluate(null, Published);
        Assert.False(state.CanSuggest);
        Assert.True(state.NeedsAlert);
    }
}
