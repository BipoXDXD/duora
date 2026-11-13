using System.Text.Json;
using Duora.Core.Identity;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Email;

/// <summary>
/// Consumidor do fato <c>events.waitlisted_registration_promoted</c> v1 (issue #320, ADR-0056). Cada fato é tratado
/// numa transação curta: revalida conta, inscrição e evento sob lock de leitura e grava, juntos, a marca de
/// processamento (entrega terminal deste consumidor, única por fato) e, se ainda fizer sentido, o pedido de e-mail
/// que o <see cref="TransactionalEmailDispatcher"/> envia depois, fora de qualquer transação. Não altera inscrição,
/// capacidade, fila ou auditoria.
/// </summary>
public sealed partial class WaitlistPromotionNoticeScheduler
{
    internal const string Consumer = "waitlist-promotion-notice";

    /// <summary>
    /// Fato mais velho que isto nunca é lido, mesmo depois da marca d'água. Fica abaixo dos 7 dias em que a retenção
    /// apaga as marcas terminais, então um fato nunca volta a parecer novo depois que a marca some.
    /// </summary>
    internal static readonly TimeSpan MaxFactAge = TimeSpan.FromHours(48);

    private const string FactType = "events.waitlisted_registration_promoted";
    private const int FactSchemaVersion = 1;

    private static readonly JsonSerializerOptions FactJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly NpgsqlDataSource _source;
    private readonly TimeProvider _time;
    private readonly WaitlistPromotionNoticeOptions _options;
    private readonly ILogger<WaitlistPromotionNoticeScheduler> _logger;

    internal WaitlistPromotionNoticeScheduler(NpgsqlDataSource source, TimeProvider time,
        WaitlistPromotionNoticeOptions options, ILogger<WaitlistPromotionNoticeScheduler> logger)
    {
        _source = source;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public TimeSpan PollInterval => _options.PollInterval;

    public int BatchSize => _options.BatchSize;

    /// <summary>Trata um lote de fatos ainda sem marca e devolve quantos marcou; zero quando desligado.</summary>
    public async Task<int> ScheduleBatchAsync(CancellationToken ct)
    {
        if (!_options.Enabled || _options.NotBefore is not { } notBefore)
        {
            return 0;
        }

        var processed = 0;
        foreach (var factId in await CandidatesAsync(notBefore, ct))
        {
            try
            {
                if (await ProcessAsync(factId, ct) is { } outcome)
                {
                    processed++;
                    LogProcessed(_logger, factId, outcome);
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                // Conta ou inscrição presa por um caso de uso além do teto: o fato fica sem marca e volta depois,
                // sem travar os vizinhos do lote.
                LogBusy(_logger, factId);
            }
        }
        return processed;
    }

    private async Task<Guid[]> CandidatesAsync(DateTimeOffset notBefore, CancellationToken ct)
    {
        var oldest = _time.GetUtcNow() - MaxFactAge;
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var query = Command(connection, $"""
            select e.id from ops.outbox_events e
            where e.type='{FactType}' and e.occurred_at>=$1 and e.occurred_at>$2
              and not exists(select 1 from ops.outbox_deliveries d where d.outbox_event_id=e.id and d.consumer='{Consumer}')
            order by e.occurred_at,e.id limit $3
            """, notBefore, oldest, _options.BatchSize);
        await using var reader = await query.ExecuteReaderAsync(ct);
        List<Guid> ids = [];
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return [.. ids];
    }

    /// <returns>O desfecho gravado, ou <c>null</c> quando outro worker já tem ou já marcou o fato.</returns>
    private async Task<NoticeOutcome?> ProcessAsync(Guid factId, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, "set local lock_timeout='2s'", ct);
        if (await LockFactAsync(connection, factId, ct) is not { } fact)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var decision = Read(fact) is { } promotion
            ? await DecideAsync(connection, promotion, now, ct)
            : NoticeDecision.Reject("InvalidContract");
        if (!await MarkAsync(connection, factId, decision, now, ct))
        {
            return null;
        }

        await transaction.CommitAsync(ct);
        return decision.Outcome;
    }

    /// <summary>
    /// Trava o fato (o outro worker pula) e confere de novo a falta de marca, agora sob o lock. A ordem é conta e
    /// depois inscrição, como nos casos de uso; nenhum deles espera pela linha do fato, então não há ciclo.
    /// </summary>
    private static async Task<StoredFact?> LockFactAsync(NpgsqlConnection connection, Guid factId, CancellationToken ct)
    {
        await using var query = Command(connection, $"""
            select e.schema_version,e.resource_type,e.resource_id,e.payload::text from ops.outbox_events e
            where e.id=$1
              and not exists(select 1 from ops.outbox_deliveries d where d.outbox_event_id=e.id and d.consumer='{Consumer}')
            for no key update skip locked
            """, factId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new StoredFact(reader.GetInt32(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3))
            : null;
    }

    /// <summary>Só o contrato v1: tipo, schema 1, recurso evento e payload exato com o mesmo evento do envelope.</summary>
    private static Promotion? Read(StoredFact fact)
    {
        if (fact.SchemaVersion != FactSchemaVersion || fact.ResourceType != "event")
        {
            return null;
        }
        try
        {
            var payload = JsonSerializer.Deserialize<Promotion>(fact.Payload, FactJson);
            return payload is { } p && p.EventId == fact.ResourceId && p.UserId != Guid.Empty ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<NoticeDecision> DecideAsync(NpgsqlConnection connection, Promotion promotion,
        DateTimeOffset now, CancellationToken ct)
    {
        string? email;
        await using (var account = Command(connection, """
            select case when status='Active' then email end from identity.users where id=$1 for share
            """, promotion.UserId))
        {
            email = await account.ExecuteScalarAsync(ct) as string;
        }
        if (email is null)
        {
            return NoticeDecision.Discard("AccountNotActive");
        }

        await using var registration = Command(connection, """
            select r.state,e.state,e.starts_at from events.registrations r join events.events e on e.id=r.event_id
            where r.event_id=$1 and r.user_id=$2 for share of r
            """, promotion.EventId, promotion.UserId);
        await using var reader = await registration.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != "Confirmed")
        {
            return NoticeDecision.Discard("RegistrationNotConfirmed");
        }
        if (reader.GetString(1) != "Published")
        {
            return NoticeDecision.Discard("EventNotOpen");
        }
        var startsAt = reader.GetFieldValue<DateTimeOffset>(2);
        if (now >= startsAt)
        {
            return NoticeDecision.Discard("EventStarted");
        }
        return EmailAddress.TryParse(email, out _)
            ? NoticeDecision.Schedule(new PendingNotice(email, promotion.UserId, startsAt))
            : NoticeDecision.Reject("InvalidRecipient");
    }

    /// <summary>
    /// Marca e pedido de e-mail na mesma transação. A chave única da entrega (fato, consumidor, fato) é a
    /// idempotência: um replay ou um segundo worker não grava nada, nem o e-mail.
    /// </summary>
    private static async Task<bool> MarkAsync(NpgsqlConnection connection, Guid factId, NoticeDecision decision,
        DateTimeOffset now, CancellationToken ct)
    {
        var marked = await Execute(connection, """
            insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,attempts,available_at,
              deadline_at,fencing_token,last_error,created_at,completed_at)
            values ($1,$2,$3,$4,$5,0,$6,$6,0,nullif($7,''),$6,$6)
            on conflict do nothing
            """, ct, Guid.CreateVersion7(now), factId, Consumer, factId.ToString("D"), decision.Status.ToString(), now,
            decision.Reason ?? "");
        if (marked != 1)
        {
            return false;
        }
        if (decision.Notice is { } notice)
        {
            await InsertAsync(connection,
                AccountEmailOutbox.CreateWaitlistPromotionNotice(notice.To, notice.UserId, now, notice.EventStartsAt), ct);
        }
        return true;
    }

    private static async Task InsertAsync(NpgsqlConnection connection, OutboxEvent request, CancellationToken ct)
    {
        await Execute(connection, """
            insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,payload,occurred_at)
            values ($1,$2,$3,$4,$5,$6::jsonb,$7)
            """, ct, request.Id, request.Type, request.SchemaVersion, request.ResourceType, request.ResourceId,
            request.Payload, request.OccurredAt);
        var delivery = request.Deliveries.Single();
        await Execute(connection, """
            insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,attempts,available_at,
              deadline_at,fencing_token,created_at)
            values ($1,$2,$3,$4,$5,0,$6,$7,0,$6)
            """, ct, delivery.Id, request.Id, delivery.Consumer, delivery.RecipientKey, delivery.Status.ToString(),
            delivery.AvailableAt, delivery.DeadlineAt);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fato de promoção {FactId} tratado como {Outcome}")]
    private static partial void LogProcessed(ILogger logger, Guid factId, NoticeOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fato de promoção {FactId} adiado: conta ou inscrição em uso")]
    private static partial void LogBusy(ILogger logger, Guid factId);

    private sealed record StoredFact(int SchemaVersion, string ResourceType, Guid ResourceId, string Payload);

    private sealed record Promotion(Guid EventId, Guid UserId);

    private sealed record PendingNotice(string To, Guid UserId, DateTimeOffset EventStartsAt);

    private enum NoticeOutcome
    {
        Scheduled,
        Discarded,
        Rejected,
    }

    /// <summary>
    /// Estado da marca, motivo sanitizado (um código fixo, nunca dado pessoal) e, quando há, o aviso a enfileirar.
    /// Descartar fecha a marca como <c>Expired</c>; contrato ou destinatário inválido, como <c>Failed</c>, para revisão.
    /// </summary>
    private sealed record NoticeDecision(OutboxDeliveryStatus Status, string? Reason, PendingNotice? Notice)
    {
        public NoticeOutcome Outcome => Notice is not null ? NoticeOutcome.Scheduled
            : Status == OutboxDeliveryStatus.Failed ? NoticeOutcome.Rejected : NoticeOutcome.Discarded;

        public static NoticeDecision Schedule(PendingNotice notice) => new(OutboxDeliveryStatus.Delivered, null, notice);

        public static NoticeDecision Discard(string reason) => new(OutboxDeliveryStatus.Expired, reason, null);

        public static NoticeDecision Reject(string reason) => new(OutboxDeliveryStatus.Failed, reason, null);
    }
}
