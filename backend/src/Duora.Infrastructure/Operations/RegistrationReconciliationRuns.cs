using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Safety;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Operations;

/// <summary>Unidade do manifesto como está gravada.</summary>
public sealed record ReconciliationRunUnitView(
    Guid Id,
    Guid SubjectUserId,
    Guid OriginalMeasureId,
    long MeasureVersion,
    string State,
    IReadOnlyList<ReconciliationPreviewItem> Items,
    ReconciliationUnitReceipt? Receipt);

/// <summary>Run de reconciliação com suas unidades e recibos, sem nada que identifique além dos ids.</summary>
public sealed record ReconciliationRunView(
    Guid Id,
    int SelectorVersion,
    string EvidenceId,
    DateTimeOffset CutoverLower,
    DateTimeOffset CutoverUpper,
    Guid CreatedByUserId,
    string Justification,
    string AuthorizationReference,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ReconciliationRunUnitView> Units);

/// <summary>Resultado da criação de um run.</summary>
public abstract record ReconciliationRunOutcome
{
    private ReconciliationRunOutcome()
    {
    }

    /// <summary>O run gravado agora, ou o que a mesma chave e o mesmo pedido já tinham criado.</summary>
    public sealed record Created(ReconciliationRunView View) : ReconciliationRunOutcome;

    /// <summary>Algum digest não bate com o recálculo do servidor, ou não há corte; nada foi gravado.</summary>
    public sealed record ManifestChanged : ReconciliationRunOutcome;

    /// <summary>Já existe um run aberto.</summary>
    public sealed record AlreadyOpen : ReconciliationRunOutcome;

    /// <summary>A chave já gravou outro pedido deste ator.</summary>
    public sealed record KeyReused : ReconciliationRunOutcome;

    /// <summary>A sessão não autoriza (ou não autoriza mais) a moderação na transação.</summary>
    public sealed record Forbidden : ReconciliationRunOutcome;

    /// <summary>Contenção curta; nada foi gravado e o pedido pode ser repetido com a mesma chave.</summary>
    public sealed record Busy : ReconciliationRunOutcome;

    /// <summary>A transação passou do <c>statement_timeout</c>; nada foi gravado, e a repetição deve esperar mais.</summary>
    public sealed record TimedOut : ReconciliationRunOutcome;
}

/// <summary>Resultado do encerramento de um run.</summary>
public abstract record ReconciliationRunCloseOutcome
{
    private ReconciliationRunCloseOutcome()
    {
    }

    public sealed record Closed(ReconciliationRunView View) : ReconciliationRunCloseOutcome;

    public sealed record NotFound : ReconciliationRunCloseOutcome;

    /// <summary>A versão esperada não é a atual; nada foi gravado.</summary>
    public sealed record PreconditionFailed : ReconciliationRunCloseOutcome;

    /// <summary>A versão esperada é a atual, mas o run já está fechado; nada foi gravado.</summary>
    public sealed record AlreadyClosed : ReconciliationRunCloseOutcome;

    /// <summary>A sessão não autoriza (ou não autoriza mais) a moderação na transação.</summary>
    public sealed record Forbidden : ReconciliationRunCloseOutcome;

    /// <summary>Unidades em curso ou outro encerramento seguram o run; repetir depois.</summary>
    public sealed record Busy : ReconciliationRunCloseOutcome;

    /// <summary>O encerramento passou do <c>statement_timeout</c>; a repetição deve esperar mais.</summary>
    public sealed record TimedOut : ReconciliationRunCloseOutcome;
}

/// <summary>Resultado da leitura de um run.</summary>
public abstract record ReconciliationRunReadOutcome
{
    private ReconciliationRunReadOutcome()
    {
    }

    public sealed record Found(ReconciliationRunView View) : ReconciliationRunReadOutcome;

    public sealed record NotFound : ReconciliationRunReadOutcome;

    /// <summary>A sessão não autoriza (ou não autoriza mais) a moderação na transação.</summary>
    public sealed record Forbidden : ReconciliationRunReadOutcome;

    /// <summary>Contenção curta; repetir depois.</summary>
    public sealed record Busy : ReconciliationRunReadOutcome;

    /// <summary>A leitura passou do <c>statement_timeout</c>; a repetição deve esperar mais.</summary>
    public sealed record TimedOut : ReconciliationRunReadOutcome;
}

/// <summary>
/// Manifesto da reconciliação de inscrições legadas (issue #321, ADR-0049, D1). Operations é dono das tabelas; a
/// classificação é a do leitor da prévia, no snapshot desta transação. Criar um run só grava o run, as unidades
/// <c>Pending</c> e a auditoria: não toca Events nem Safety. Quem aplica uma unidade é o
/// <see cref="RegistrationReconciliationCoordinator"/>; fechar o run espera as unidades em curso e barra as seguintes.
/// </summary>
public sealed partial class RegistrationReconciliationRuns(
    NpgsqlDataSource source,
    TimeProvider clock,
    LegacyRegistrationReconciliationPreviews previews,
    ILogger<RegistrationReconciliationRuns> logger)
{
    /// <summary>Colunas do recibo, na ordem que <see cref="ReadReceipt"/> espera logo depois do estado.</summary>
    internal const string ReceiptColumns = "receipt_reason,receipt_fact_ids,receipt_executed_by,receipt_executed_at,receipt_correlation_id";

    private const string OpenRunIndex = "ux_registration_reconciliation_runs_open";
    private const string CommandConstraint = "ux_registration_reconciliation_runs_command";

    /// <summary>O perdedor de uma corrida repete uma vez, num snapshot novo que já enxerga o vencedor.</summary>
    private const int MaxAttempts = 2;

    private const string LockTimeout = "2s";
    private const string RunClosed = "Closed";

    private static readonly JsonSerializerOptions ItemFormat = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Numa transação <c>REPEATABLE READ</c>: autoriza, trata a chave de idempotência, recusa um segundo run aberto,
    /// reclassifica as contas pedidas com o corte configurado e, se todos os digests baterem, grava tudo junto.
    /// </summary>
    public async Task<ReconciliationRunOutcome> CreateAsModeratorAsync(ModeratorSession moderator, ReconciliationRunOrder order,
        ReconciliationCutoverEvidence? evidence, IdempotencyKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await CreateOnceAsync(moderator, order, evidence, key, ct);
            }
            catch (PostgresException error) when (IsLostRace(error))
            {
                // Outra criação confirmou primeiro: a tentativa seguinte devolve o replay, o conflito de chave ou o 409.
            }
            catch (PostgresException error) when (IsTimeout(error, ct))
            {
                return new ReconciliationRunOutcome.TimedOut();
            }
            catch (PostgresException error) when (IsContention(error))
            {
                return new ReconciliationRunOutcome.Busy();
            }
        }

        return new ReconciliationRunOutcome.Busy();
    }

    /// <summary>
    /// Fecha o run (ADR-0049, D2). O <c>FOR UPDATE</c> espera as unidades em curso, que trancam o run <c>FOR SHARE</c>, e
    /// as seguintes já o veem <c>Closed</c>; ao estourar o <c>lock_timeout</c>, o runbook manda repetir.
    /// </summary>
    public async Task<ReconciliationRunCloseOutcome> CloseAsModeratorAsync(ModeratorSession moderator, Guid runId, long expectedVersion,
        CancellationToken ct)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await BeginAsync(connection, IsolationLevel.ReadCommitted, ct);
            if (await LockRunStateAsync(connection, runId, ct) is not { } run)
            {
                return new ReconciliationRunCloseOutcome.NotFound();
            }

            var now = clock.GetUtcNow();
            if (!await LegacyRegistrationReconciliationPreviews.IsModeratorSessionAsync(connection, moderator, now, ct))
            {
                return new ReconciliationRunCloseOutcome.Forbidden();
            }

            if (run.Version != expectedVersion)
            {
                return new ReconciliationRunCloseOutcome.PreconditionFailed();
            }

            if (run.State == RunClosed)
            {
                return new ReconciliationRunCloseOutcome.AlreadyClosed();
            }

            await Execute(connection, "update ops.registration_reconciliation_runs set state='Closed',version=version+1 where id=$1", ct, runId);
            await ReconciliationAudit.AppendAsync(connection, new AuditRecord(AuditActorKind.Moderator, moderator.UserId,
                AuditAction.RegistrationReconciliationRunClosed, AuditTargetTypes.RegistrationReconciliationRun, runId, run.Version + 1),
                now, ReconciliationAudit.Correlation(now), ct);
            var view = await ReadViewAsync(connection, runId, ct)
                ?? throw new InvalidOperationException("O run trancado nesta transação tem de ser legível.");
            await transaction.CommitAsync(ct);
            return new ReconciliationRunCloseOutcome.Closed(view);
        }
        catch (PostgresException error) when (IsTimeout(error, ct))
        {
            return new ReconciliationRunCloseOutcome.TimedOut();
        }
        catch (PostgresException error) when (IsContention(error))
        {
            if (error.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                LogCloseDeadlock(logger);
            }

            return new ReconciliationRunCloseOutcome.Busy();
        }
    }

    /// <summary>Lê o run num único snapshot, depois de revalidar a moderação dentro dele.</summary>
    public async Task<ReconciliationRunReadOutcome> ReadAsModeratorAsync(ModeratorSession moderator, Guid runId, CancellationToken ct)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await BeginAsync(connection, IsolationLevel.RepeatableRead, ct);
            if (!await LegacyRegistrationReconciliationPreviews.IsModeratorSessionAsync(connection, moderator, clock.GetUtcNow(), ct))
            {
                return new ReconciliationRunReadOutcome.Forbidden();
            }

            return await ReadViewAsync(connection, runId, ct) is { } view
                ? new ReconciliationRunReadOutcome.Found(view)
                : new ReconciliationRunReadOutcome.NotFound();
        }
        catch (PostgresException error) when (IsTimeout(error, ct))
        {
            return new ReconciliationRunReadOutcome.TimedOut();
        }
        catch (PostgresException error) when (IsContention(error))
        {
            return new ReconciliationRunReadOutcome.Busy();
        }
    }

    private async Task<ReconciliationRunOutcome> CreateOnceAsync(ModeratorSession moderator, ReconciliationRunOrder order,
        ReconciliationCutoverEvidence? evidence, IdempotencyKey key, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await BeginAsync(connection, IsolationLevel.RepeatableRead, ct);
        var now = clock.GetUtcNow();
        if (!await LegacyRegistrationReconciliationPreviews.IsModeratorSessionAsync(connection, moderator, now, ct))
        {
            return new ReconciliationRunOutcome.Forbidden();
        }

        var intent = order.Intent();
        if (await ReplayAsync(connection, moderator.UserId, key, intent, ct) is { } replay)
        {
            return replay;
        }

        if (await HasOpenRunAsync(connection, ct))
        {
            return new ReconciliationRunOutcome.AlreadyOpen();
        }

        if (evidence is null)
        {
            return new ReconciliationRunOutcome.ManifestChanged();
        }

        Guid[] subjects = [.. order.Units.Select(unit => unit.SubjectUserId).Distinct()];
        var eligible = await previews.ReclassifyAsync(connection, evidence, subjects, now, ct);
        if (Manifest(order, eligible) is not { } manifest)
        {
            return new ReconciliationRunOutcome.ManifestChanged();
        }

        var run = Guid.CreateVersion7(now);
        await InsertRunAsync(connection, run, moderator.UserId, order, evidence, key, intent, now, ct);
        foreach (var unit in manifest)
        {
            await InsertUnitAsync(connection, run, unit, now, ct);
        }

        await ReconciliationAudit.AppendAsync(connection, new AuditRecord(AuditActorKind.Moderator, moderator.UserId,
            AuditAction.RegistrationReconciliationRunCreated, AuditTargetTypes.RegistrationReconciliationRun, run, targetVersion: 1),
            now, ReconciliationAudit.Correlation(now), ct);
        var view = await ReadViewAsync(connection, run, ct)
            ?? throw new InvalidOperationException("O run gravado nesta transação tem de ser legível.");
        await transaction.CommitAsync(ct);
        return new ReconciliationRunOutcome.Created(view);
    }

    private Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, IsolationLevel isolation, CancellationToken ct) =>
        BeginAsync(connection, isolation, previews.QueryTimeout, ct);

    /// <summary>Transação com o <c>lock_timeout</c> de 2 s e o teto de cada comando dos runs e das unidades (ADR-0049).</summary>
    internal static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, IsolationLevel isolation,
        TimeSpan statementTimeout, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(isolation, ct);
        await Execute(connection, "select set_config('lock_timeout',$1,true),set_config('statement_timeout',$2,true)", ct,
            LockTimeout,
            FormattableString.Invariant($"{(long)statementTimeout.TotalMilliseconds}ms"));
        return transaction;
    }

    private static async Task<(string State, long Version)?> LockRunStateAsync(NpgsqlConnection connection, Guid run, CancellationToken ct)
    {
        await using var query = Command(connection, "select state,version from ops.registration_reconciliation_runs where id=$1 for update", run);
        await using var reader = await query.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static async Task<ReconciliationRunOutcome?> ReplayAsync(NpgsqlConnection connection, Guid actor, IdempotencyKey key,
        byte[] intent, CancellationToken ct)
    {
        Guid run;
        byte[] recorded;
        await using (var query = Command(connection, """
            select id,intent_hash from ops.registration_reconciliation_runs where created_by_user_id=$1 and idempotency_key=$2
            """, actor, key.Value))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            run = reader.GetGuid(0);
            recorded = reader.GetFieldValue<byte[]>(1);
        }

        if (!CryptographicOperations.FixedTimeEquals(recorded, intent))
        {
            return new ReconciliationRunOutcome.KeyReused();
        }

        var view = await ReadViewAsync(connection, run, ct)
            ?? throw new InvalidOperationException("O run encontrado por chave tem de ser legível.");
        return new ReconciliationRunOutcome.Created(view);
    }

    private static async Task<bool> HasOpenRunAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var query = Command(connection, "select exists(select 1 from ops.registration_reconciliation_runs where state='Open')");
        return (bool)(await query.ExecuteScalarAsync(ct))!;
    }

    /// <summary>A unidade recalculada de cada par pedido, ou null se alguma faltar ou tiver outro digest.</summary>
    private static List<ReconciliationPreviewUnit>? Manifest(ReconciliationRunOrder order, IReadOnlyList<ReconciliationPreviewUnit> eligible)
    {
        List<ReconciliationPreviewUnit> manifest = [];
        foreach (var unit in order.Units)
        {
            if (eligible.FirstOrDefault(candidate => candidate.SubjectUserId == unit.SubjectUserId
                && candidate.OriginalMeasureId == unit.OriginalMeasureId && candidate.UnitDigest == unit.UnitDigest) is not { } match)
            {
                return null;
            }

            manifest.Add(match);
        }

        return manifest;
    }

    private static Task<int> InsertRunAsync(NpgsqlConnection connection, Guid run, Guid actor, ReconciliationRunOrder order,
        ReconciliationCutoverEvidence evidence, IdempotencyKey key, byte[] intent, DateTimeOffset now, CancellationToken ct) =>
        Execute(connection, """
            insert into ops.registration_reconciliation_runs(id,selector_version,cutover_evidence_id,cutover_lower,cutover_upper,
              created_by_user_id,justification,authorization_reference,state,version,created_at,idempotency_key,intent_hash)
            values($1,$2,$3,$4,$5,$6,$7,$8,'Open',1,$9,$10,$11)
            """, ct, run, LegacyRegistrationSelector.Version, evidence.Id, evidence.Cutover.Lower, evidence.Cutover.Upper,
            actor, order.Justification, order.AuthorizationReference, now, key.Value, intent);

    private static Task<int> InsertUnitAsync(NpgsqlConnection connection, Guid run, ReconciliationPreviewUnit unit,
        DateTimeOffset now, CancellationToken ct) =>
        Execute(connection, """
            insert into ops.registration_reconciliation_units(id,run_id,subject_user_id,original_measure_id,measure_version,items)
            values($1,$2,$3,$4,$5,$6::jsonb)
            """, ct, Guid.CreateVersion7(now), run, unit.SubjectUserId, unit.OriginalMeasureId, unit.MeasureVersion,
            JsonSerializer.Serialize(unit.Items, ItemFormat));

    private static async Task<ReconciliationRunView?> ReadViewAsync(NpgsqlConnection connection, Guid run, CancellationToken ct)
    {
        List<ReconciliationRunUnitView> units = [];
        await using (var query = Command(connection, $"""
            select id,subject_user_id,original_measure_id,measure_version,items::text,state,{ReceiptColumns}
            from ops.registration_reconciliation_units where run_id=$1 order by subject_user_id,original_measure_id
            """, run))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                units.Add(new ReconciliationRunUnitView(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt64(3),
                    reader.GetString(5), ReadItems(reader.GetString(4)), ReadReceipt(reader, run, reader.GetGuid(0), stateColumn: 5)));
            }
        }

        await using var header = Command(connection, """
            select id,selector_version,cutover_evidence_id,cutover_lower,cutover_upper,created_by_user_id,justification,
              authorization_reference,state,version,created_at
            from ops.registration_reconciliation_runs where id=$1
            """, run);
        await using var row = await header.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
        {
            return null;
        }

        return new ReconciliationRunView(row.GetGuid(0), row.GetInt32(1), row.GetString(2), row.GetFieldValue<DateTimeOffset>(3),
            row.GetFieldValue<DateTimeOffset>(4), row.GetGuid(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetInt64(9),
            row.GetFieldValue<DateTimeOffset>(10), units);
    }

    /// <summary>O recibo da linha, com <see cref="ReceiptColumns"/> logo depois do estado; nulo em unidade <c>Pending</c>.</summary>
    internal static ReconciliationUnitReceipt? ReadReceipt(NpgsqlDataReader reader, Guid run, Guid unit, int stateColumn)
    {
        var state = Enum.Parse<ReconciliationUnitState>(reader.GetString(stateColumn));
        return state == ReconciliationUnitState.Pending ? null : new ReconciliationUnitReceipt(run, unit, state,
            reader.IsDBNull(stateColumn + 1) ? null : reader.GetString(stateColumn + 1),
            reader.GetFieldValue<Guid[]>(stateColumn + 2), reader.GetGuid(stateColumn + 3),
            reader.GetFieldValue<DateTimeOffset>(stateColumn + 4), reader.GetString(stateColumn + 5));
    }

    internal static ReconciliationPreviewItem[] ReadItems(string json) =>
        JsonSerializer.Deserialize<ReconciliationPreviewItem[]>(json, ItemFormat)
        ?? throw new InvalidOperationException("Unidade gravada sem itens.");

    private static bool IsLostRace(PostgresException error) =>
        error is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenRunIndex or CommandConstraint };

    /// <summary>Lock ocupado, deadlock ou conflito de serialização do snapshot: a transação voltou atrás inteira.</summary>
    internal static bool IsContention(PostgresException error) =>
        error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure;

    /// <summary>57014 sem cancelamento do pedido é o <c>statement_timeout</c>; o do cliente continua propagando.</summary>
    internal static bool IsTimeout(PostgresException error, CancellationToken ct) =>
        error.SqlState == PostgresErrorCodes.QueryCanceled && !ct.IsCancellationRequested;

    [LoggerMessage(Level = LogLevel.Error, Message = "Deadlock ao fechar um run da reconciliação: defeito a investigar, sem retry.")]
    private static partial void LogCloseDeadlock(ILogger logger);
}
