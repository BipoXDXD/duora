namespace Duora.Core.Safety;

/// <summary>An opaque incident reference and private human rationale; identities come from the delivery and session.</summary>
public sealed record SelfieRecoveryRequest
{
    public SelfieRecoveryRequest(Guid deliveryId, string incidentReference, string internalReason, string idempotencyKey)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(deliveryId, Guid.Empty);
        if (string.IsNullOrEmpty(incidentReference) || incidentReference.Length > 64
            || !incidentReference.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Use an opaque incident reference.", nameof(incidentReference));
        }
        // Reuse the domain validation, including Unicode length and control characters.
        InternalReason = SafetyText.RequireText(internalReason, SuspensionOrder.InternalReasonMaxLength, nameof(internalReason));
        ModerationCommandKey.ValidateModeratorKey(idempotencyKey);
        DeliveryId = deliveryId;
        IncidentReference = incidentReference;
        IdempotencyKey = idempotencyKey;
    }

    public Guid DeliveryId { get; }
    public string IncidentReference { get; }
    public string InternalReason { get; }
    public string IdempotencyKey { get; }
}
