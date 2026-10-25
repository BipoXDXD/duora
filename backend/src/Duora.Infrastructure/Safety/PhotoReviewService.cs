using System.Data;
using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

public enum ReviewRefusal
{
    Forbidden,
    NotFound,
    PreconditionFailed,
    KeyReused,
    StateConflict,
    Unavailable,
    ReviewerUnavailable,
}
public sealed record ReviewReply<T>(T? Value, ReviewRefusal? Refusal) where T : class;
public sealed record ReviewCase(Guid CaseId, string State, long Version, DateTimeOffset AwaitingReviewAt,
    DateTimeOffset ReviewDeadline, string PolicyVersion);
public sealed record ReviewCasePage(IReadOnlyList<ReviewCase> Items, string? NextCursor);
public sealed record ReviewCapabilities(bool Assign, bool Evidence, bool Decide, bool Appeal, bool RecentMfaRequiredForDecision);
public sealed record ReviewMutation(Guid CaseId, Guid PhotoId, Guid? AppealId, long Version, long PhotoVersion,
    string? Decision = null, Guid? AssetId = null, DateTimeOffset? RequestedAt = null,
    DateTimeOffset? ResponseTargetAt = null, DateTimeOffset? MaximumRetentionUntil = null);
public sealed record ReviewAppeal(Guid AppealId, Guid PhotoId, string State, long Version, DateTimeOffset RequestedAt,
    DateTimeOffset ResponseTargetAt, DateTimeOffset MaximumRetentionUntil, DateTimeOffset? ClosedAt);
public sealed record ReviewAppealPage(IReadOnlyList<ReviewAppeal> Items, string? NextCursor);
public sealed record ReviewHistoryItem(Guid DecisionId, string Decision, string ReasonCode, DateTimeOffset DecidedAt, bool IsAppeal);
public sealed record ReviewHistory(Guid PhotoId, long PhotoVersion, IReadOnlyList<ReviewHistoryItem> Items,
    ReviewAppeal? Appeal, bool CanRequestAppeal);

/// <summary>Typed application boundary. Every command delegates to a live, guarded internal coordinator.</summary>
public sealed partial class PhotoReviewService
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal PhotoReviewService(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        _contexts = contexts;
        _store = store;
        _time = time;
    }

    public async Task<ReviewReply<ReviewMutation>> AssignAsync(Guid user, Guid session, Guid id, Guid reviewer,
        long version, IdempotencyKey key, bool appeal, CancellationToken ct)
    {
        var actor = new PhotoReviewActor(user, session);
        if (appeal)
        {
            var result = await new PhotoReviewAppealAssignments(_contexts, _time).AssignAsync(actor, id, reviewer, version, key, ct);
            return result is PhotoReviewAppealAssignmentResult.Assigned accepted
                ? new(new(Guid.Empty, Guid.Empty, accepted.AppealId, accepted.Version, 0), null)
                : new(null, Refusal(result));
        }
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var initial = await new PhotoReviewAssignments(db, _time).AssignAsync(actor, id, reviewer, version, key, ct);
        return initial is PhotoReviewAssignmentResult.Assigned assigned
            ? new(new(assigned.CaseId, Guid.Empty, null, assigned.Version, 0), null)
            : new(null, Refusal(initial));
    }

    public async Task<ReviewReply<ReviewMutation>> RequestAppealAsync(Guid user, Guid session, Guid photo,
        long version, IdempotencyKey key, CancellationToken ct)
    {
        var result = await new PhotoReviewAppeals(_contexts, _store, _time).RequestAsync(new(user, session), photo, version, key, ct);
        return result is PhotoReviewAppealRequestResult.Accepted accepted
            ? new(new(Guid.Empty, accepted.PhotoId, accepted.AppealId, 1, accepted.PhotoVersion,
                RequestedAt: accepted.RequestedAt, ResponseTargetAt: accepted.ResponseTargetAt,
                MaximumRetentionUntil: accepted.MaximumRetentionUntil), null)
            : new(null, Refusal(result));
    }

    public async Task<ReviewReply<ReviewMutation>> DecideAsync(Guid user, Guid session, Guid id,
        long version, IdempotencyKey key, bool appeal, bool approve, CancellationToken ct)
    {
        var actor = new PhotoReviewActor(user, session);
        if (appeal)
        {
            var result = await new PhotoReviewAppealDecisions(_contexts, _store, _time).DecideAsync(actor, id, version, approve, key, ct);
            return result is PhotoReviewAppealDecisionResult.Decided decided
                ? new(new(decided.CaseId, await PhotoIdAsync(decided.CaseId, ct), decided.AppealId, decided.AppealVersion, decided.PhotoVersion,
                    decided.Decision, decided.AssetId), null)
                : new(null, Refusal(result));
        }
        if (approve)
        {
            var result = await new PhotoReviewApprovals(_contexts, _store, _time).ApproveAsync(actor, id, version,
                PhotoReviewApprovalReason.MeetsPolicy, key, ct);
            return result is PhotoReviewApprovalResult.Approved accepted
                ? new(new(accepted.CaseId, await PhotoIdAsync(accepted.CaseId, ct), null, accepted.Version, accepted.PhotoVersion, "approve", accepted.AssetId), null)
                : new(null, Refusal(result));
        }
        var rejection = await new PhotoReviewRejections(_contexts, _store, _time).RejectAsync(actor, id, version,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, key, ct);
        return rejection is PhotoReviewRejectionResult.Rejected rejected
            ? new(new(rejected.CaseId, await PhotoIdAsync(rejected.CaseId, ct), null, rejected.Version, rejected.PhotoVersion, "reject"), null)
            : new(null, Refusal(rejection));
    }

    public async Task<ReviewReply<ReviewCapabilities>> CapabilitiesAsync(Guid user, Guid session, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var actor = new PhotoReviewActor(user, session);
        var access = new PhotoReviewAccess(db, _time);
        // Capability discovery requires at least one live explicit permission. No role grants are invented.
        var assign = await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, ct);
        var evidence = await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, ct);
        var recentDecide = await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, ct);
        var recentAppeal = await access.AuthorizeAsync(actor, PhotoReviewCapability.Appeal, ct);
        if (!assign && !evidence && !recentDecide && !recentAppeal)
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        var decide = evidence && await HasGrantAsync(db, user, "decide", ct);
        var appeal = evidence && await HasGrantAsync(db, user, "appeal", ct);
        return new(new(assign, evidence, decide, appeal, (decide && !recentDecide) || (appeal && !recentAppeal)), null);
    }

    public async Task<ReviewReply<ReviewCasePage>> CasesAsync(Guid user, Guid session, string? cursor, int limit, CancellationToken ct)
    {
        var parsed = ParseCursor(cursor);
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var result = await new PhotoReviewAssignments(db, _time).ListAssignedAsync(new(user, session),
            parsed is { } page ? new(page.At, page.Id) : null, limit, ct);
        return result is PhotoReviewListResult.Listed listed
            ? new(new(listed.Page.Cases.Select(c => new ReviewCase(c.CaseId, c.State, c.Version,
                    c.AwaitingReviewAt, c.ReviewDeadline, c.PolicyVersion)).ToArray(),
                listed.Page.NextCursor is { } next ? Cursor(next.AwaitingReviewAt, next.CaseId) : null), null)
            : new(null, ReviewRefusal.Forbidden);
    }

    public async Task<ReviewReply<ReviewCase>> CaseAsync(Guid user, Guid session, Guid id, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var result = await new PhotoReviewAssignments(db, _time).FindAssignedAsync(new(user, session), id, ct);
        if (result is not PhotoReviewDetailsResult.Found found)
        {
            return new(null, result is PhotoReviewDetailsResult.Forbidden ? ReviewRefusal.Forbidden : ReviewRefusal.NotFound);
        }
        var item = found.Details.Case;
        return new(new(item.CaseId, item.State, item.Version, item.AwaitingReviewAt,
            item.ReviewDeadline, found.Details.PolicyVersion), null);
    }

    private static Task<bool> HasGrantAsync(DuoraDbContext db, Guid user, string capability, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            select coalesce((select enabled from safety.photo_review_grants where user_id={user} and capability={capability}
              order by version desc limit 1),false) as "Value"
            """).SingleAsync(ct);

    private static string Cursor(DateTimeOffset at, Guid id) =>
        $"{at.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{id:N}";

    private static (DateTimeOffset At, Guid Id)? ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }
        var parts = cursor.Split('.');
        if (cursor.Length > 64 || parts.Length != 2 || !long.TryParse(parts[0],
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            || ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id)
            || id == Guid.Empty || Cursor(new(ticks, TimeSpan.Zero), id) != cursor)
        {
            throw new ArgumentException("Invalid review cursor.", nameof(cursor));
        }
        return (new(ticks, TimeSpan.Zero), id);
    }

    // Closed mapping: never use exception text, database errors or CLR type names as public problem details.
    private static ReviewRefusal Refusal(object result) => result switch
    {
        PhotoReviewAssignmentResult.Forbidden or PhotoReviewAppealAssignmentResult.Forbidden
            or PhotoReviewAppealRequestResult.Forbidden or PhotoReviewApprovalResult.Forbidden
            or PhotoReviewRejectionResult.Forbidden or PhotoReviewAppealDecisionResult.Forbidden => ReviewRefusal.Forbidden,
        PhotoReviewAssignmentResult.NotFound or PhotoReviewAppealAssignmentResult.NotFound
            or PhotoReviewAppealRequestResult.NotFound or PhotoReviewApprovalResult.NotFound
            or PhotoReviewRejectionResult.NotFound or PhotoReviewAppealDecisionResult.NotFound => ReviewRefusal.NotFound,
        PhotoReviewAssignmentResult.PreconditionFailed or PhotoReviewAppealAssignmentResult.PreconditionFailed
            or PhotoReviewAppealRequestResult.PreconditionFailed or PhotoReviewApprovalResult.PreconditionFailed
            or PhotoReviewRejectionResult.PreconditionFailed or PhotoReviewAppealDecisionResult.PreconditionFailed => ReviewRefusal.PreconditionFailed,
        PhotoReviewAssignmentResult.KeyReused or PhotoReviewAppealAssignmentResult.KeyReused
            or PhotoReviewAppealRequestResult.KeyReused or PhotoReviewApprovalResult.KeyReused
            or PhotoReviewRejectionResult.KeyReused or PhotoReviewAppealDecisionResult.KeyReused => ReviewRefusal.KeyReused,
        PhotoReviewAssignmentResult.ReviewerUnavailable or PhotoReviewAppealAssignmentResult.ReviewerUnavailable => ReviewRefusal.ReviewerUnavailable,
        PhotoReviewAppealRequestResult.Unavailable or PhotoReviewApprovalResult.Unavailable
            or PhotoReviewRejectionResult.Unavailable or PhotoReviewAppealDecisionResult.Unavailable => ReviewRefusal.Unavailable,
        PhotoReviewAssignmentResult.StateConflict or PhotoReviewAppealAssignmentResult.StateConflict
            or PhotoReviewAppealRequestResult.StateConflict or PhotoReviewApprovalResult.StateConflict
            or PhotoReviewRejectionResult.StateConflict or PhotoReviewAppealDecisionResult.StateConflict => ReviewRefusal.StateConflict,
        _ => throw new InvalidOperationException("Unknown private review result."),
    };
}
