namespace Duora.Core.Geolocation;

public readonly record struct DatabaseAgeState(bool CanSuggest, bool NeedsAlert);

/// <summary>Publication/build age; download or restart must never renew a database's validity.</summary>
public static class DatabaseAgePolicy
{
    public static DatabaseAgeState Evaluate(DateTimeOffset? publishedAt, DateTimeOffset now)
    {
        if (publishedAt is not { } published || published > now)
        {
            return new(false, true);
        }
        var age = now - published;
        return new(age < TimeSpan.FromDays(7), age >= TimeSpan.FromHours(72));
    }
}
