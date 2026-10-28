using System.Text.Json;
using Duora.Core.Identity;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

/// <summary>Outbox com teto, renovação e fencing; chamadas externas sempre fora de transações.</summary>
public sealed class SelfieVerificationDispatcher
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransientDataRetention = TimeSpan.FromHours(24);

    /// <summary>Decisão do dono em 28/10/2026 (issue #93): resultado mínimo, inclusive Passed, por 30 dias.</summary>
    private static readonly TimeSpan ResultRetention = TimeSpan.FromDays(30);
    private readonly DuoraDbContext _db;
    private readonly OutboxDeliveryQueue _queue;
    private readonly ISelfieProvider _provider;
    private readonly SelfieProviderOptions _options;
    private readonly TimeProvider _time;

    internal SelfieVerificationDispatcher(DuoraDbContext db, OutboxDeliveryQueue queue, ISelfieProvider provider,
        IOptions<SelfieProviderOptions> options, TimeProvider time)
    {
        _db = db;
        _queue = queue;
        _provider = provider;
        _options = options.Value;
        _time = time;
    }

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await PurgeExpiredDataAsync(now, cancellationToken);

        if (!_options.Enabled)
        {
            return 0;
        }

        var claims = await _queue.ClaimWithAttemptLimitAsync(SelfieVerificationOutbox.Consumer, 1, now, now + Lease, 40, cancellationToken);
        foreach (var exhausted in claims.Exhausted)
        {
            if (Guid.TryParse(exhausted.RecipientKey, out var id))
            {
                await _db.Set<SelfieVerificationRecord>().Where(v => v.Id == id
                    && (v.State == SelfieState.Pending || v.State == SelfieState.Capturing || v.State == SelfieState.Processing))
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.State, SelfieState.Expired), cancellationToken);
            }
        }

        foreach (var delivery in claims.Claimed)
        {
            await ProcessAsync(delivery, cancellationToken);
        }

        return claims.Claimed.Count;
    }

    /// <summary>
    /// Roda mesmo com a integração desligada. Não há imagens no banco: a referência da sessão e o feedback saem 24h
    /// após a expiração; a linha inteira sai 30 dias após a conclusão (ou a expiração, se nunca concluiu).
    /// </summary>
    private async Task PurgeExpiredDataAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transientCutoff = now - TransientDataRetention;
        await _db.Set<SelfieVerificationRecord>().Where(v => v.ExpiresAt < transientCutoff
                && (v.ProviderReference != null || v.FeedbackCode != null))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProviderReference, (string?)null)
                .SetProperty(v => v.FeedbackCode, (string?)null), cancellationToken);

        var resultCutoff = now - ResultRetention;
        await _db.Set<SelfieVerificationRecord>().Where(v => (v.CompletedAt ?? v.ExpiresAt) <= resultCutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task ProcessAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        Guid id;
        try
        {
            id = JsonSerializer.Deserialize<Guid>(delivery.Payload);
        }
        catch (JsonException)
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Failed, "InvalidPayload", _time.GetUtcNow(), cancellationToken);
            return;
        }

        var row = await _db.Set<SelfieVerificationRecord>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (delivery.EventType != SelfieVerificationOutbox.EventType || row is null
            || row.State is SelfieState.Passed or SelfieState.RecaptureRequired or SelfieState.Inconclusive or SelfieState.Expired)
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, _time.GetUtcNow(), cancellationToken);
            return;
        }

        if (row.ExpiresAt <= _time.GetUtcNow())
        {
            await ApplyAsync(row, delivery, null, new(SelfieState.Expired, null, row.PolicyVersion), cancellationToken);
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Expired, null, _time.GetUtcNow(), cancellationToken);
            return;
        }

        if (!(await _queue.RenewLeasesAsync([delivery], Lease, _time, cancellationToken)).Contains(delivery.Id))
        {
            return;
        }

        SelfieSession? session = null;
        SelfieResult? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (row.ProviderReference is null)
            {
                session = await _provider.StartAsync(row.Id, timeout.Token);
            }
            else
            {
                result = await _provider.ReadAsync(row.ProviderReference, timeout.Token);
            }
        }
        catch (Exception exception) when (exception is Amazon.Runtime.AmazonServiceException or HttpRequestException or InvalidDataException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _queue.RescheduleAsync(delivery, _time.GetUtcNow().AddSeconds(5), "ProviderUnavailable", _time.GetUtcNow(), cancellationToken);
            return;
        }

        var applied = await ApplyAsync(row, delivery, session, result, cancellationToken);
        if (!applied)
        {
            return;
        }

        if (result is { State: not (SelfieState.Processing or SelfieState.Capturing or SelfieState.Pending) })
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, _time.GetUtcNow(), cancellationToken);
        }
        else
        {
            await _queue.RescheduleAsync(delivery, _time.GetUtcNow().AddSeconds(5), "AwaitingCapture", _time.GetUtcNow(), cancellationToken);
        }
    }

    private async Task<bool> ApplyAsync(SelfieVerificationRecord previous, ClaimedDelivery delivery,
        SelfieSession? session, SelfieResult? result, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var owned = await _db.Database.SqlQuery<int>($"""
            select 1 as "Value" from ops.outbox_deliveries
            where id = {delivery.Id} and fencing_token = {delivery.FencingToken}
                and status = 'InFlight' and lease_expires_at > {now} for update
            """).ToListAsync(cancellationToken);
        if (owned.Count == 0)
        {
            return false;
        }

        var accounts = await _db.Database.SqlQuery<string>($"""
            select status as "Value" from identity.users where id = {previous.UserId} and email_confirmed for update
            """).ToListAsync(cancellationToken);
        var allowed = accounts.Count == 1 && accounts[0] is "Active" or "PendingEligibility";
        var record = await _db.Set<SelfieVerificationRecord>().SingleAsync(v => v.Id == previous.Id, cancellationToken);
        if (record.Version != previous.Version || record.State is SelfieState.Passed or SelfieState.Expired
            or SelfieState.Inconclusive or SelfieState.RecaptureRequired)
        {
            return false;
        }

        if (!allowed || record.ExpiresAt <= now)
        {
            record.State = SelfieState.Expired;
        }
        else if (session is not null)
        {
            record.ProviderReference = session.Reference;
            record.ExpiresAt = session.ExpiresAt < record.ExpiresAt ? session.ExpiresAt : record.ExpiresAt;
            record.State = SelfieState.Capturing;
        }
        else if (result is not null)
        {
            record.State = result.PolicyVersion != record.PolicyVersion ? SelfieState.Inconclusive
                : result.State == SelfieState.Processing ? SelfieState.Capturing : result.State;
            record.FeedbackCode = result.FeedbackCode;
            if (record.State == SelfieState.Passed)
            {
                await _db.Database.ExecuteSqlAsync($"""
                    update identity.users set status = 'Active', concurrency_stamp = {Guid.NewGuid().ToString()}
                    where id = {record.UserId} and status = 'PendingEligibility' and email_confirmed
                    """, cancellationToken);
            }
        }

        record.Version++;
        if (record.State is SelfieState.Passed or SelfieState.RecaptureRequired or SelfieState.Inconclusive or SelfieState.Expired)
        {
            record.CompletedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return true;
    }
}
