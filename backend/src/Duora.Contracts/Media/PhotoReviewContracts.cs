using System.Text.Json.Serialization;

namespace Duora.Contracts.Media;

/// <summary>Private review metadata. Identifiers are opaque; no storage locator or image is included.</summary>
public sealed record PhotoReviewCaseResponse(Guid CaseId, string State, [property: JsonPropertyName("eTag")] string ETag,
    DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline, string PolicyVersion);

public sealed record PhotoReviewCasePageResponse(IReadOnlyList<PhotoReviewCaseResponse> Items, string? NextCursor);

public sealed record PhotoReviewCapabilitiesResponse(bool Assign, bool Evidence, bool Decide,
    bool RecentMfaRequiredForDecision);

public sealed record PhotoReviewAssignmentRequest(Guid ReviewerId);

/// <summary>Closed vocabulary: approve/meets_policy or reject/does_not_meet_policy; no free text.</summary>
public sealed record PhotoReviewDecisionRequest(string Decision, string ReasonCode);

public sealed record PhotoReviewMutationResponse(Guid CaseId, [property: JsonPropertyName("eTag")] string ETag);

public sealed record PhotoReviewDecisionResponse(Guid CaseId, string Decision, [property: JsonPropertyName("eTag")] string ETag,
    [property: JsonPropertyName("photoETag")] string PhotoETag, Guid? AssetId);

/// <summary>Owner-visible factual history; identities of staff and private evidence are never returned.</summary>
public sealed record PhotoReviewHistoryResponse(Guid PhotoId, [property: JsonPropertyName("photoETag")] string PhotoETag,
    IReadOnlyList<PhotoReviewHistoryItemResponse> Items);

public sealed record PhotoReviewHistoryItemResponse(Guid DecisionId, string Decision, string ReasonCode,
    DateTimeOffset DecidedAt);

/// <summary>Owner photo discovery only: no image, asset or storage locator is returned.</summary>
public sealed record PhotoReviewPhotoResponse(Guid PhotoId, string Status, string? RejectionCode, DateTimeOffset CreatedAt);

public sealed record PhotoReviewPhotoPageResponse(IReadOnlyList<PhotoReviewPhotoResponse> Items, string? NextCursor);
