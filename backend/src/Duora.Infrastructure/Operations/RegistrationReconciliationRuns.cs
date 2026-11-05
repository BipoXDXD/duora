using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Safety;
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
    IReadOnlyList<ReconciliationPreviewItem> Items);

/// <summary>Run de reconciliação com suas unidades, sem recibo nem nada que identifique além dos ids.</summary>
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
/// <c>Pending</c> e a auditoria: não toca Events nem Safety. Aplicação e encerramento são outra etapa.
/// </summary>
public sealed class RegistrationReconciliationRuns(
    NpgsqlDataSource source,
    TimeProvider clock,
    LegacyRegistrationReconciliationPreviews previews)
{
    private const string OpenRunIndex = "ux_registration_reconciliation_runs_open";
    private const string CommandConstraint = "ux_registration_reconciliation_runs_command";

    /// <summary>O perdedor de uma corrida repete uma vez, num snapshot novo que já enxerga o vencedor.</summary>
    private const int MaxAttempts = 2;

    private const string LockTimeout = "2s";

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

    /// <summary>Lê o run num único snapshot, depois de revalidar a moderação dentro dele.</summary>
    public async Task<ReconciliationRunReadOutcome> ReadAsModeratorAsync(ModeratorSession moderator, Guid runId, CancellationToken ct)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await BeginAsync(connection, ct);
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
        await using var transaction = await BeginAsync(connection, ct);
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

        await AuditAsync(connection, moderator.UserId, run, now, ct);
        var view = await ReadViewAsync(connection, run, ct)
            ?? throw new InvalidOperationException("O run gravado nesta transação tem de ser legível.");
        await transaction.CommitAsync(ct);
        return new ReconciliationRunOutcome.Created(view);
    }

    private async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Execute(connection, "select set_config('lock_timeout',$1,true),set_config('statement_timeout',$2,true)", ct,
            LockTimeout,
            FormattableString.Invariant($"{(long)previews.QueryTimeout.TotalMilliseconds}ms"));
        return transaction;
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

    private static Task<int> AuditAsync(NpgsqlConnection connection, Guid actor, Guid run, DateTimeOffset now, CancellationToken ct)
    {
        var record = new AuditRecord(AuditActorKind.Moderator, actor, AuditAction.RegistrationReconciliationRunCreated,
            AuditTargetTypes.RegistrationReconciliationRun, run, targetVersion: 1);
        return Execute(connection, """
            insert into ops.audit_events(occurred_at,actor_kind,actor_id,action,target_type,target_id,target_version,correlation_id)
            values($1,$2,$3,$4,$5,$6,$7,nullif($8,''))
            """, ct, now, record.ActorKind.ToString(), actor, record.Action.Code, record.TargetType, record.TargetId, 1L,
            Activity.Current?.TraceId.ToHexString() ?? string.Empty);
    }

    private static async Task<ReconciliationRunView?> ReadViewAsync(NpgsqlConnection connection, Guid run, CancellationToken ct)
    {
        List<ReconciliationRunUnitView> units = [];
        await using (var query = Command(connection, """
            select id,subject_user_id,original_measure_id,measure_version,state,items::text
            from ops.registration_reconciliation_units where run_id=$1 order by subject_user_id,original_measure_id
            """, run))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                units.Add(new ReconciliationRunUnitView(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt64(3),
                    reader.GetString(4), ReadItems(reader.GetString(5))));
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

    private static ReconciliationPreviewItem[] ReadItems(string json) =>
        JsonSerializer.Deserialize<ReconciliationPreviewItem[]>(json, ItemFormat)
        ?? throw new InvalidOperationException("Unidade gravada sem itens.");

    private static bool IsLostRace(PostgresException error) =>
        error is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenRunIndex or CommandConstraint };

    /// <summary>Lock ocupado, deadlock ou conflito de serialização do snapshot: a transação voltou atrás inteira.</summary>
    private static bool IsContention(PostgresException error) =>
        error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure;

    /// <summary>57014 sem cancelamento do pedido é o <c>statement_timeout</c>; o do cliente continua propagando.</summary>
    private static bool IsTimeout(PostgresException error, CancellationToken ct) =>
        error.SqlState == PostgresErrorCodes.QueryCanceled && !ct.IsCancellationRequested;
}
