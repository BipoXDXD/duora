namespace Duora.Contracts.Participation;

public sealed record CatalogContentRequest(string Title, string Objective, IReadOnlyList<CatalogCardRequest> Cards);
public sealed record CatalogCardRequest(string CardId, string Prompt, IReadOnlyList<OptionResponse> Options);
public sealed record CatalogAudioVariantRequest(string AssetId, string Transcript, string AccessibleDescription,
    string Provenance, string Sha256, byte[] Bytes);
public sealed record CatalogAudioRequest(string CueId, CatalogAudioVariantRequest EnUS, CatalogAudioVariantRequest PtBR);
public sealed record PublishCatalogRequest(int ContentVersion, CatalogContentRequest EnUS, CatalogContentRequest PtBR,
    IReadOnlyList<CatalogAudioRequest> Audio, bool EditorialApproved);
public sealed record CatalogPublishedResponse(Guid ExperienceVersionId);
public sealed record RoomAudioResponse(string CueId, string AssetId, string Transcript, string AccessibleDescription,
    string Sha256, string Url);
