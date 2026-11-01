using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Participation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.Infrastructure.Safety;

/// <summary>Private administrative diagnosis, never a payload reconstruction.</summary>
public sealed record SelfieRecoveryDiagnosis(Guid DeliveryId, Guid EventId, Guid VerificationId, Guid? SubjectUserId,
    string Standing, DateTimeOffset? ReviewedAt, Guid? SuspensionId, string? SuspensionReason);

public sealed record SelfieRecoveryResult(Guid DeliveryId, string Disposition, Guid? SuspensionId,
    DateTimeOffset RecordedAt, bool Replayed);

/// <summary>Codes are mapped to HTTP by the authenticated boundary; Busy is safe to retry with the same key.</summary>
public sealed record SelfieRecoveryOutcome(string Code, SelfieRecoveryResult? Result = null);

public sealed partial class AccountSuspensions
{
    public async Task<SelfieRecoveryDiagnosis?> DiagnoseSelfieRecoveryAsync(ModeratorSession moderator, Guid deliveryId,
        CancellationToken cancellationToken)
    {
        if (!await IsModeratorAsync(moderator, requireRecentTotp: false, cancellationToken))
        {
            return null;
        }
        var delivery = await RecoveryDeliveryAsync(deliveryId, lockRow: false, cancellationToken);
        if (delivery is null)
        {
            return null;
        }
        var standing = delivery.SubjectUserId is { } user
            ? (await SelfieFailureLedger.StandingAsync(_db, user, _time.GetUtcNow(), cancellationToken)).GetType().Name
            : "LinkMissing";
        var active = delivery.SubjectUserId is { } subject
            ? await ActiveRowsOf(subject).SingleOrDefaultAsync(cancellationToken) : null;
        return new SelfieRecoveryDiagnosis(deliveryId, delivery.EventId, delivery.VerificationId,
            delivery.SubjectUserId, standing, delivery.ReviewedAt, active?.Id, active?.ReasonCode);
    }

    /// <summary>
    /// Human recovery through the suspension port. Effect, immutable receipt, audit and Failed review commit together.
    /// The receipt is checked before the potentially purged ledger, but only after live authorization.
    /// </summary>
    public async Task<SelfieRecoveryOutcome> SuspendAsModeratorAsync(ModeratorSession moderator,
        SelfieRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await RecoverSelfieAsync(moderator, request, cancellationToken);
            }
            catch (RegistrationPlanChangedException)
            {
                // Dispose the transaction before planning the expanded user set again.
            }
            catch (PostgresException error) when (IsContention(error))
            {
                return new("Busy");
            }
        }
        return new("Busy");
    }

    private async Task<SelfieRecoveryOutcome> RecoverSelfieAsync(ModeratorSession moderator,
        SelfieRecoveryRequest request, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"selfie-recovery:{moderator.UserId:D}:{request.IdempotencyKey}")));
        // Length-prefixing avoids ambiguity when the private rationale contains separators.
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.DeliveryId:D}:{request.IncidentReference.Length}:{request.IncidentReference}:{request.InternalReason}"));
        await using var transaction = await BeginAsync(cancellationToken);
        if (!await IsModeratorAsync(moderator, requireRecentTotp: true, cancellationToken))
        {
            return new("Forbidden");
        }
        var discovery = await RecoveryDeliveryAsync(request.DeliveryId, lockRow: false, cancellationToken);
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var plan = discovery?.SubjectUserId is { } user
            ? await RegistrationLockPlan.ForSuspensionAsync(connection, user, moderator.UserId, cancellationToken) : null;
        await GuardUsersAsync(plan?.Users ?? [moderator.UserId], cancellationToken);
        // Freeze the live credentials/role while recording the decision, after the ordered guards.
        await ParticipationSql.Execute(connection,
            "select 1 from identity.users where id=any($1) order by id for share", cancellationToken,
            plan?.Users ?? [moderator.UserId]);
        await _db.Database.ExecuteSqlAsync($"select 1 from identity.login_sessions where id={moderator.SessionId} for share", cancellationToken);
        await _db.Database.ExecuteSqlAsync($"select 1 from identity.user_roles where user_id={moderator.UserId} for share", cancellationToken);
        if (!await IsModeratorAsync(moderator, requireRecentTotp: true, cancellationToken))
        {
            return new("Forbidden");
        }
        var receipts = await _db.Database.SqlQuery<RecoveryReceipt>($"""
            select delivery_id as "DeliveryId", command_key as "CommandKey", command_intent as "Intent",
              disposition as "Disposition", moderation_action_id as "SuspensionId", recorded_at as "RecordedAt"
            from safety.selfie_suspension_recoveries where command_key={key} or delivery_id={request.DeliveryId}
            """).ToListAsync(cancellationToken);
        if (receipts.Count != 0)
        {
            var receipt = receipts.Find(r => r.CommandKey == key);
            if (receipt is null)
            {
                return new("AlreadyReviewed");
            }
            return CryptographicOperations.FixedTimeEquals(receipt.Intent, intent)
                ? new("Resolved", new(receipt.DeliveryId, receipt.Disposition, receipt.SuspensionId, receipt.RecordedAt, true))
                : new("KeyReused");
        }
        if (discovery is null)
        {
            return new("NotFound");
        }
        if (discovery.SubjectUserId is not { } subject || plan is null)
        {
            return new("LinkMissing");
        }
        var standing = await AccountSuspensionStatus.LockAsync(_db, subject, cancellationToken);
        // Recheck the exact retained identity after account/guard locks; never infer it from payload or caller IDs.
        var delivery = await RecoveryDeliveryAsync(request.DeliveryId, lockRow: true, cancellationToken);
        if (delivery is null)
        {
            return new("NotFound");
        }
        if (delivery.SubjectUserId != subject || delivery.EventId != discovery.EventId
            || delivery.VerificationId != discovery.VerificationId)
        {
            return new("LinkMissing");
        }
        if (delivery.ReviewedAt is not null)
        {
            return new("AlreadyReviewed");
        }
        if (standing == SuspensionStanding.NotFound)
        {
            return new("NotFound");
        }
        string disposition;
        Guid? actionId = null;
        var active = await ActiveRowsOf(subject).SingleOrDefaultAsync(cancellationToken);
        if (active is not null)
        {
            if (active.ParseReason() != Core.Safety.SuspensionReason.SelfieVerificationFailed)
            {
                return new("AwaitingOtherMeasure");
            }
            disposition = "ExistingSuspension";
            actionId = active.Id;
        }
        else if (standing == SuspensionStanding.NotSuspendable)
        {
            disposition = "NotSuspendable";
        }
        else if (await SelfieFailureLedger.StandingAsync(_db, subject, _time.GetUtcNow(), cancellationToken)
                 is not SelfieAttemptStanding.Exhausted)
        {
            disposition = "NoLongerNeeded";
        }
        else
        {
            var order = SuspensionOrder.ByModerator(moderator.UserId, subject,
                Core.Safety.SuspensionReason.SelfieVerificationFailed, request.InternalReason);
            var command = ModerationCommandKey.ForModerator(moderator.UserId, request.IdempotencyKey);
            // Recovery has a separate command namespace; delivery identity is part of its persisted intent.
            var effect = await RecordSuspensionEffectAsync(moderator, order, command, plan, key, intent, cancellationToken);
            if (effect is not SuspensionOutcome.Suspended suspended)
            {
                return effect switch
                {
                    SuspensionOutcome.KeyReused => new("KeyReused"),
                    SuspensionOutcome.AlreadySuspended => new("AwaitingOtherMeasure"),
                    SuspensionOutcome.NotFound => new("NotFound"),
                    _ => new("NotSuspendable"),
                };
            }
            disposition = "Suspended";
            actionId = suspended.View.Id;
        }
        var now = _time.GetUtcNow();
        await _db.Database.ExecuteSqlAsync($"""
            insert into safety.selfie_suspension_recoveries
              (delivery_id,command_key,command_intent,moderator_user_id,subject_user_id,event_id,verification_id,
               incident_reference,internal_reason,moderation_action_id,disposition,recorded_at)
            values ({request.DeliveryId},{key},{intent},{moderator.UserId},{subject},{delivery.EventId},{delivery.VerificationId},
              {request.IncidentReference},{request.InternalReason},{actionId},{disposition},{now})
            """, cancellationToken);
        var reviewed = await _db.Database.ExecuteSqlAsync($"""
            update ops.outbox_deliveries set reviewed_at={now}
            where id={request.DeliveryId} and status='Failed' and completed_at is not null and reviewed_at is null
            """, cancellationToken);
        if (reviewed != 1)
        {
            throw new InvalidOperationException("The locked Failed delivery was changed during recovery.");
        }
        await new AuditTrail(_db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, moderator.UserId,
            AuditAction.OutboxFailureResolved, "outbox_delivery", request.DeliveryId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("Resolved", new(request.DeliveryId, disposition, actionId, now, false));
    }

    private async Task<RecoveryDelivery?> RecoveryDeliveryAsync(Guid deliveryId, bool lockRow, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQuery<RecoveryDelivery>($"""
            select d.id as "DeliveryId", e.id as "EventId", e.resource_id as "VerificationId",
              f.user_id as "SubjectUserId", d.reviewed_at as "ReviewedAt"
            from ops.outbox_deliveries d join ops.outbox_events e on e.id=d.outbox_event_id
            left join identity.selfie_failures f on f.verification_id=e.resource_id
            where d.id={deliveryId} and d.consumer='selfie-attempts-suspension' and d.status='Failed'
              and d.completed_at is not null and e.type='identity.selfie-attempts-exhausted'
              and e.schema_version=1 and e.resource_type='selfie_verification'
            """).ToListAsync(cancellationToken);
        if (lockRow && rows.Count != 0)
        {
            // Only the delivery is mutable; ledger changes are serialized by the account lock.
            await _db.Database.ExecuteSqlAsync($"select 1 from ops.outbox_deliveries where id={deliveryId} for update", cancellationToken);
            return await RecoveryDeliveryAsync(deliveryId, lockRow: false, cancellationToken);
        }
        return rows.SingleOrDefault();
    }

    private sealed record RecoveryDelivery(Guid DeliveryId, Guid EventId, Guid VerificationId, Guid? SubjectUserId, DateTimeOffset? ReviewedAt);
    private sealed record RecoveryReceipt(Guid DeliveryId, string CommandKey, byte[] Intent, string Disposition,
        Guid? SuspensionId, DateTimeOffset RecordedAt);
}
