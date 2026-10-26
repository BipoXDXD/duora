using Duora.Core.Media;

namespace Duora.Infrastructure.Safety;

/// <summary>Single-use bounded delivery. No database context or storage locator escapes to the HTTP boundary.</summary>
public sealed class ReviewEvidenceDelivery : IAsyncDisposable
{
    private readonly PhotoReviewEvidenceLease _lease;
    internal ReviewEvidenceDelivery(PhotoReviewEvidenceLease lease) => _lease = lease;
    public Task<int> ReadAsync(Memory<byte> destination, CancellationToken ct) => _lease.ReadAsync(destination, ct);
    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}

public sealed partial class PhotoReviewService
{
    public async Task<ReviewReply<ReviewEvidenceDelivery>> EvidenceAsync(Guid user, Guid session, Guid id,
        PhotoVariant variant, bool appeal, CancellationToken ct)
    {
        var actor = new PhotoReviewActor(user, session);
        var result = appeal
            ? await new PhotoReviewAppealEvidence(_contexts, _store, _time).OpenAsync(actor, id, variant, ct)
            : await new PhotoReviewEvidence(_contexts, _store, _time).OpenAsync(actor, id, variant, ct);
        if (appeal && result is PhotoReviewEvidenceResult.Unavailable)
        {
            await new PhotoReviewAppealAvailability(_contexts, _store, _time).ReconcileAsync(id, ct);
        }
        return result switch
        {
            PhotoReviewEvidenceResult.Opened opened => new(new(opened.Lease), null),
            PhotoReviewEvidenceResult.Forbidden => new(null, ReviewRefusal.Forbidden),
            PhotoReviewEvidenceResult.NotFound => new(null, ReviewRefusal.NotFound),
            PhotoReviewEvidenceResult.Unavailable => new(null, ReviewRefusal.Unavailable),
            _ => throw new InvalidOperationException("Unknown private evidence result."),
        };
    }
}
