using System.Globalization;
using Duora.Core.Geolocation;

namespace Duora.UnitTests.Geolocation;

public sealed class CountryDatabaseScheduleTests
{
    [Theory]
    [InlineData("2026-10-21T08:59:59Z", "2026-10-20T09:00:00Z", "2026-10-21T09:00:00Z")]
    [InlineData("2026-10-21T09:00:00Z", "2026-10-21T09:00:00Z", "2026-10-22T09:00:00Z")]
    [InlineData("2026-10-30T14:00:00Z", "2026-10-30T09:00:00Z", "2026-10-31T09:00:00Z")]
    public void UsesSixInSaoPauloWithoutReplayingMissedDays(string now, string latest, string next)
    {
        Assert.Equal(DateTimeOffset.Parse(latest, CultureInfo.InvariantCulture), CountryDatabaseSchedule.LatestDue(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture)));
        Assert.Equal(DateTimeOffset.Parse(next, CultureInfo.InvariantCulture), CountryDatabaseSchedule.NextDue(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 60)]
    [InlineData(3, 240)]
    public void RetriesAreRelativeToFirstFailure(int attempt, int minutes)
    {
        var first = DateTimeOffset.Parse("2026-10-21T09:03:00Z", CultureInfo.InvariantCulture);
        Assert.Equal(first.AddMinutes(minutes), CountryDatabaseSchedule.RetryAt(attempt, first, first, null));
    }

    [Fact]
    public void RetryAfterAndElapsedTimeAreNeverAnticipated()
    {
        var first = DateTimeOffset.Parse("2026-10-21T09:03:00Z", CultureInfo.InvariantCulture);
        Assert.Equal(first.AddHours(9), CountryDatabaseSchedule.RetryAt(1, first, first.AddHours(2), first.AddHours(9)));
        Assert.Equal(first.AddHours(2), CountryDatabaseSchedule.RetryAt(1, first, first.AddHours(2), null));
        Assert.Null(CountryDatabaseSchedule.RetryAt(4, first, first, null));
    }
}
