namespace Duora.Core.Geolocation;

/// <summary>One daily cycle at 06:00 Sao Paulo; retries are anchored to its first failure.</summary>
public static class CountryDatabaseSchedule
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static DateTimeOffset LatestDue(DateTimeOffset now)
    {
        var day = TimeZoneInfo.ConvertTime(now, Zone).Date;
        var due = AtSix(day);
        return due <= now ? due : AtSix(day.AddDays(-1));
    }

    public static DateTimeOffset NextDue(DateTimeOffset now) => AtSix(TimeZoneInfo.ConvertTime(LatestDue(now), Zone).Date.AddDays(1));

    public static DateTimeOffset? RetryAt(int attempt, DateTimeOffset firstFailure, DateTimeOffset now, DateTimeOffset? retryAfter)
    {
        var delay = attempt switch { 1 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromHours(1), 3 => TimeSpan.FromHours(4), _ => (TimeSpan?)null };
        if (delay is null)
        {
            return null;
        }
        var due = firstFailure.Add(delay.Value);
        if (due < now)
        {
            due = now;
        }
        return retryAfter > due ? retryAfter : due;
    }

    private static DateTimeOffset AtSix(DateTime day) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddHours(6), DateTimeKind.Unspecified), Zone));
}
