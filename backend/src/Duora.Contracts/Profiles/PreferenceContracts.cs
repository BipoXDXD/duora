namespace Duora.Contracts.Profiles;

public sealed record OwnPreferencesResponse(string[] CommunicationLanguages, bool AllowDifferentDisplayLocales, bool DeclarationRequired, string Version);

public sealed record UpdatePreferencesRequest(string[]? CommunicationLanguages, bool? AllowDifferentDisplayLocales);

public sealed record OwnLocaleResponse(string Locale, string Version);

public sealed record UpdateLocaleRequest(string? Locale);

public sealed record OwnTimeZoneResponse(string? TimeZone, string Version);

public sealed record UpdateTimeZoneRequest([property: System.Text.Json.Serialization.JsonRequired] string? TimeZone);

public sealed record TimeZoneOptionsResponse(IReadOnlyList<string> TimeZones);
