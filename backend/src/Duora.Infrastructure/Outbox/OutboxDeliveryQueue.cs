using System.Data;
using Npgsql;

namespace Duora.Infrastructure.Outbox;

/// <summary>
/// Entrega tomada por um worker: só vale enquanto o token de fencing for o atual. <paramref name="OccurredAt"/> é o
/// instante do evento (coluna da outbox, não campo do payload), para o consumidor citá-lo sem mudar o schema do
/// payload.
/// </summary>
internal sealed record ClaimedDelivery(
    Guid Id,
    long FencingToken,
    int Attempts,
    DateTimeOffset DeadlineAt,
    string EventType,
    string Payload,
    DateTimeOffset OccurredAt);

/// <summary>Entrega que o claim encerrou por ter esgotado as tentativas, sem payload: não há o que enviar.</summary>
internal sealed record ExhaustedDelivery(Guid Id, int Attempts);

/// <summary>Resultado do claim com teto: o trabalho tomado e as entregas encerradas no mesmo comando.</summary>
internal sealed record ClaimResult(IReadOnlyList<ClaimedDelivery> Claimed, IReadOnlyList<ExhaustedDelivery> Exhausted);

/// <summary>
/// Fila de entregas sobre o PostgreSQL (spec 10.6). Cada operação é uma transação curta: a posse sai com
/// <c>FOR UPDATE SKIP LOCKED</c> e lease, a transação termina antes do envio, e toda conclusão confere o token
/// de fencing e a validade do lease. Os instantes vêm do <see cref="TimeProvider"/> da aplicação, nunca do
/// <c>now()</c> do banco, para o relógio ser um só e controlável em teste.
/// </summary>
internal sealed class OutboxDeliveryQueue(NpgsqlDataSource dataSource)
{
    private const string Pending = nameof(OutboxDeliveryStatus.Pending);
    private const string InFlight = nameof(OutboxDeliveryStatus.InFlight);

    // Uma entrega em voo cujo lease venceu volta a ser elegível: o worker que a tinha pode ter caído. Retomar
    // conta como nova tentativa e troca o token, o que invalida qualquer conclusão do dono anterior.
    private const string ClaimSql =
        """
        with candidates as materialized (
            select id
            from ops.outbox_deliveries
            where consumer = @consumer
              and ((status = @pending and available_at <= @now)
                or (status = @in_flight and lease_expires_at <= @now))
            order by available_at, id
            limit @batch_size
            for update skip locked
        ),
        exhausted as (
            update ops.outbox_deliveries d
            set status = 'Failed', completed_at = @now, lease_expires_at = null,
                last_error = 'MaxAttemptsExceeded', fencing_token = d.fencing_token + 1
            from candidates c
            where d.id = c.id and d.attempts >= @max_attempts
            returning d.id, d.fencing_token, d.attempts, d.deadline_at, d.outbox_event_id
        ),
        claimed as (
            update ops.outbox_deliveries d
            set status = @in_flight,
                attempts = d.attempts + 1,
                fencing_token = d.fencing_token + 1,
                lease_expires_at = @lease_until
            from candidates c
            where d.id = c.id and d.attempts < @max_attempts
            returning d.id, d.fencing_token, d.attempts, d.deadline_at, d.outbox_event_id
        )
        select c.id, c.fencing_token, c.attempts, c.deadline_at, e.type, e.payload::text, e.occurred_at,
               c.outbox_event_id, true as is_claimed
        from claimed c join ops.outbox_events e on e.id = c.outbox_event_id
        union all
        select x.id, x.fencing_token, x.attempts, x.deadline_at, null, null, null, x.outbox_event_id, false
        from exhausted x
        """;

    private const string SettleSql =
        """
        update ops.outbox_deliveries
        set status = @status, completed_at = @now, lease_expires_at = null, last_error = @error
        where id = @id and fencing_token = @fencing_token and status = @in_flight and lease_expires_at > @now
        returning outbox_event_id
        """;

    private const string RescheduleSql =
        """
        update ops.outbox_deliveries
        set status = @pending, available_at = @available_at, lease_expires_at = null, last_error = @error
        where id = @id and fencing_token = @fencing_token and status = @in_flight and lease_expires_at > @now
        """;

    // O teto é opt-in por consumidor: o e-mail o fornece; os consumidores sem teto (verificação etária, mídia)
    // mantêm seu contrato e compensam no domínio. Sem teto, nenhuma entrega é encerrada no claim.
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        string consumer,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken) =>
        (await ClaimWithAttemptLimitAsync(consumer, batchSize, now, leaseUntil, int.MaxValue, cancellationToken)).Claimed;

    /// <summary>
    /// Toma o lote e, no mesmo comando, encerra como <c>Failed</c> os candidatos que já esgotaram
    /// <paramref name="maxAttempts"/>. Esgotadas ocupam espaço no lote, mas nunca voltam como trabalho a enviar;
    /// voltam em <see cref="ClaimResult.Exhausted"/> para o consumidor registrá-las.
    /// </summary>
    public async Task<ClaimResult> ClaimWithAttemptLimitAsync(
        string consumer,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(ClaimSql, connection, transaction);
        command.Parameters.AddWithValue("max_attempts", maxAttempts);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease_until", leaseUntil);
        command.Parameters.AddWithValue("pending", Pending);
        command.Parameters.AddWithValue("in_flight", InFlight);

        var claimed = new List<ClaimedDelivery>();
        var exhausted = new List<ExhaustedDelivery>();
        var exhaustedEvents = new HashSet<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(8))
                {
                    exhausted.Add(new ExhaustedDelivery(reader.GetGuid(0), reader.GetInt32(2)));
                    exhaustedEvents.Add(reader.GetGuid(7));
                    continue;
                }

                claimed.Add(new ClaimedDelivery(
                    reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        await ScrubCompletedEventsAsync(connection, transaction, exhaustedEvents.ToArray(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ClaimResult(claimed, exhausted);
    }

    /// <summary>Confere a posse e o orçamento restante antes de iniciar um efeito externo.</summary>
    public async Task<bool> CanSendAsync(
        ClaimedDelivery delivery,
        TimeSpan sendTimeout,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            select lease_expires_at from ops.outbox_deliveries
            where id = @id and fencing_token = @fencing_token and status = @in_flight
            """);
        command.Parameters.AddWithValue("id", delivery.Id);
        command.Parameters.AddWithValue("fencing_token", delivery.FencingToken);
        command.Parameters.AddWithValue("in_flight", InFlight);
        // A espera pelo pool/banco também consome o lease. Ler o relógio só depois da consulta,
        // não comparar com um instante capturado antes de um await potencialmente longo.
        var lease = await command.ExecuteScalarAsync(cancellationToken);
        return lease is DateTime expiresAt && expiresAt - time.GetUtcNow().UtcDateTime > sendTimeout;
    }

    /// <summary>Encerra a entrega; falso quando o lease venceu ou outro worker a assumiu.</summary>
    public async Task<bool> SettleAsync(
        ClaimedDelivery delivery,
        OutboxDeliveryStatus finalStatus,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (finalStatus is OutboxDeliveryStatus.Pending or OutboxDeliveryStatus.InFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(finalStatus), finalStatus, "Estado não é final.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(SettleSql, connection, transaction);
        AddOwnership(command, delivery, now);
        command.Parameters.AddWithValue("status", finalStatus.ToString());
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);

        var settled = await command.ExecuteScalarAsync(cancellationToken);
        if (settled is Guid eventId)
        {
            await ScrubCompletedEventsAsync(connection, transaction, [eventId], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return settled is Guid;
    }

    /// <summary>Devolve a entrega à fila para nova tentativa; falso quando o worker já não a detém.</summary>
    public async Task<bool> RescheduleAsync(
        ClaimedDelivery delivery,
        DateTimeOffset availableAt,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(RescheduleSql);
        AddOwnership(command, delivery, now);
        command.Parameters.AddWithValue("available_at", availableAt);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("pending", Pending);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // Um único CTE vê o snapshot anterior: dois consumidores encerrando entregas irmãs podem
    // enxergar um ao outro como aberto e ambos deixar o payload para sempre. Serializar a limpeza
    // pelo evento e só então consultar em NOVO comando (novo snapshot READ COMMITTED) evita isso.
    // As entregas são alteradas antes; após o lock do evento não se espera por locks de entregas.
    private static async Task ScrubCompletedEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] eventIds,
        CancellationToken cancellationToken)
    {
        if (eventIds.Length == 0)
        {
            return;
        }

        await using (var takeLocks = new NpgsqlCommand(
            "select id from ops.outbox_events where id = any(@ids) order by id for no key update",
            connection, transaction))
        {
            takeLocks.Parameters.AddWithValue("ids", eventIds);
            await takeLocks.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var scrub = new NpgsqlCommand(
            """
            update ops.outbox_events e set payload = '{}'::jsonb
            where e.id = any(@ids)
              and not exists (
                  select 1 from ops.outbox_deliveries d
                  where d.outbox_event_id = e.id and d.status in (@pending, @in_flight))
            """, connection, transaction);
        scrub.Parameters.AddWithValue("ids", eventIds);
        scrub.Parameters.AddWithValue("pending", Pending);
        scrub.Parameters.AddWithValue("in_flight", InFlight);
        await scrub.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddOwnership(NpgsqlCommand command, ClaimedDelivery delivery, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("id", delivery.Id);
        command.Parameters.AddWithValue("fencing_token", delivery.FencingToken);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("in_flight", InFlight);
    }
}
