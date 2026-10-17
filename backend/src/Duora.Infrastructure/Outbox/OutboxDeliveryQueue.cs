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

/// <summary>
/// Fila de entregas sobre o PostgreSQL (spec 10.6). Cada operação é um único comando: a posse sai com
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
            returning d.id, d.outbox_event_id
        ),
        scrubbed as (
            update ops.outbox_events e
            set payload = '{}'::jsonb
            where exists (select 1 from exhausted x where x.outbox_event_id = e.id)
              and not exists (
                  select 1 from ops.outbox_deliveries other
                  where other.outbox_event_id = e.id
                    and other.status in (@pending, @in_flight)
                    and not exists (select 1 from exhausted x where x.id = other.id))
            returning e.id
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
        select d.id, d.fencing_token, d.attempts, d.deadline_at, e.type, e.payload::text, e.occurred_at
        from claimed d join ops.outbox_events e on e.id = d.outbox_event_id
        """;

    // Estado final e, se era a última entrega aberta do evento, payload esvaziado no mesmo comando. O CTE de
    // escrita vê o snapshot anterior, por isso a entrega atual é excluída da verificação de "ainda abertas".
    private const string SettleSql =
        """
        with settled as (
            update ops.outbox_deliveries
            set status = @status, completed_at = @now, lease_expires_at = null, last_error = @error
            where id = @id and fencing_token = @fencing_token and status = @in_flight and lease_expires_at > @now
            returning outbox_event_id
        ),
        scrubbed as (
            update ops.outbox_events e
            set payload = '{}'::jsonb
            from settled
            where e.id = settled.outbox_event_id
              and not exists (
                  select 1 from ops.outbox_deliveries other
                  where other.outbox_event_id = e.id
                    and other.id <> @id
                    and other.status in (@pending, @in_flight))
            returning e.id
        )
        select count(*)::int from settled
        """;

    private const string RescheduleSql =
        """
        update ops.outbox_deliveries
        set status = @pending, available_at = @available_at, lease_expires_at = null, last_error = @error
        where id = @id and fencing_token = @fencing_token and status = @in_flight and lease_expires_at > @now
        """;

    // O teto é opt-in por consumidor: o e-mail o fornece; os consumidores existentes mantêm seu contrato.
    // Esgotadas consomem espaço no lote de manutenção, mas nunca voltam como trabalho a enviar.
    public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        string consumer,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken) =>
        ClaimAsync(consumer, batchSize, now, leaseUntil, int.MaxValue, cancellationToken);

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        string consumer,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        await using var command = dataSource.CreateCommand(ClaimSql);
        command.Parameters.AddWithValue("max_attempts", maxAttempts);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease_until", leaseUntil);
        command.Parameters.AddWithValue("pending", Pending);
        command.Parameters.AddWithValue("in_flight", InFlight);

        var claimed = new List<ClaimedDelivery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedDelivery(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return claimed;
    }

    /// <summary>Confere a posse e o orçamento restante antes de iniciar um efeito externo.</summary>
    public async Task<bool> CanSendAsync(
        ClaimedDelivery delivery,
        DateTimeOffset requiredUntil,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            select exists (
                select 1 from ops.outbox_deliveries
                where id = @id and fencing_token = @fencing_token and status = @in_flight
                  and lease_expires_at > @now)
            """);
        AddOwnership(command, delivery, requiredUntil);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
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

        await using var command = dataSource.CreateCommand(SettleSql);
        AddOwnership(command, delivery, now);
        command.Parameters.AddWithValue("status", finalStatus.ToString());
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("pending", Pending);

        var settled = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        return settled == 1;
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

    private static void AddOwnership(NpgsqlCommand command, ClaimedDelivery delivery, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("id", delivery.Id);
        command.Parameters.AddWithValue("fencing_token", delivery.FencingToken);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("in_flight", InFlight);
    }
}
