namespace Duora.Contracts.Profiles;

public sealed record OwnRegionalPreferencesResponse(string Mode, string[] CountryCodes, string Version);

public sealed record UpdateRegionalPreferencesRequest(string? Mode, string[]? CountryCodes);
