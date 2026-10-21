namespace Duora.Contracts.Profiles;

public sealed record RegionDetectionCapabilityResponse(bool Enabled);

public sealed record RegionDetectionResponse(string Status, string? CountryCode, DateTimeOffset ObservedAt);
