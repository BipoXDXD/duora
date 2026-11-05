using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Operations;

/// <summary>Recibo de uma unidade terminal, sem a chave nem o hash do pedido de aplicação.</summary>
public sealed record ReconciliationUnitReceipt(
    Guid RunId,
    Guid UnitId,
    ReconciliationUnitState State,
    string? Reason,
    IReadOnlyList<Guid> FactIds,
    Guid ExecutedByUserId,
    DateTimeOffset ExecutedAt,
    string CorrelationId);

/// <summary>Resultado de um pedido de aplicação.</summary>
public abstract record ReconciliationApplyOutcome
{
    private ReconciliationApplyOutcome()
    {
    }

    /// <summary>A unidade é terminal: gravou o recibo agora, ou já o tinha.</summary>
    public sealed record Receipt(ReconciliationUnitReceipt Value) : ReconciliationApplyOutcome;

    /// <summary>Run inexistente, ou unidade inexistente ou de outro run.</summary>
    public sealed record NotFound : ReconciliationApplyOutcome;

    /// <summary>A sessão não autoriza (ou não autoriza mais) a moderação; nada foi gravado.</summary>
    public sealed record Forbidden : ReconciliationApplyOutcome;

    /// <summary>Unidade <c>Pending</c> num run fechado.</summary>
    public sealed record RunClosed : ReconciliationApplyOutcome;

    /// <summary>O descritor configurado não é o do run; a unidade segue <c>Pending</c>.</summary>
    public sealed record CutoverChanged : ReconciliationApplyOutcome;

    /// <summary>Esta chave deste moderador já gravou o recibo de outra unidade.</summary>
    public sealed record KeyReused : ReconciliationApplyOutcome;

    /// <summary>Contenção, ou tentativas de reler o plano esgotadas; nada foi gravado e o pedido pode ser repetido.</summary>
    public sealed record Busy : ReconciliationApplyOutcome;

    /// <summary>A transação passou do <c>statement_timeout</c>; nada foi gravado, e a repetição deve esperar mais.</summary>
    public sealed record TimedOut : ReconciliationApplyOutcome;
}

/// <summary>
/// Aplica uma unidade do manifesto (issue #321, ADR-0049, protocolo de uma unidade): uma transação READ COMMITTED, com
/// <c>lock_timeout</c> de 2 s, em que o recibo é o checkpoint. O plano completo tranca e o plano de um evento escreve,
/// sempre pelo <see cref="RegistrationLockPlan"/> e pelo <see cref="RegistrationTransitions"/> do Events; a ordem de locks
/// do ADR-0044 fica intacta, e run e unidade vêm antes dela porque só a reconciliação os toca. Deadlock é defeito a
/// investigar: nunca é repetido aqui.
/// </summary>
public sealed partial class RegistrationReconciliationCoordinator
{
    public const int MaxPlanEvents = 50;
    public const int MaxPlanUsers = 1000;

    private const int MaxAttempts = 3;
    private const string ApplyKeyIndex = "ux_registration_reconciliation_units_apply";
    private const string ClosedRun = "Closed";

    private readonly NpgsqlDataSource _source;
    private readonly TimeProvider _clock;
    private readonly LegacyRegistrationReconciliationPreviews _previews;
    private readonly ILogger<RegistrationReconciliationCoordinator> _logger;
    private readonly int _maxPlanEvents;
    private readonly int _maxPlanUsers;

    public RegistrationReconciliationCoordinator(NpgsqlDataSource source, TimeProvider clock,
        LegacyRegistrationReconciliationPreviews previews, ILogger<RegistrationReconciliationCoordinator> logger)
        : this(source, clock, previews, logger, MaxPlanEvents, MaxPlanUsers)
    {
    }

    /// <summary>Tetos menores só para ensaiar o plano grande demais com poucos dados sintéticos.</summary>
    internal RegistrationReconciliationCoordinator(NpgsqlDataSource source, TimeProvider clock,
        LegacyRegistrationReconciliationPreviews previews, ILogger<RegistrationReconciliationCoordinator> logger,
        int maxPlanEvents, int maxPlanUsers)
    {
        _source = source;
        _clock = clock;
        _previews = previews;
        _logger = logger;
        _maxPlanEvents = maxPlanEvents;
        _maxPlanUsers = maxPlanUsers;
    }

    /// <summary>
    /// Leva a unidade a um estado terminal, ou devolve o recibo que ela já tem. Um plano que cresce depois da pré-seleção
    /// volta atrás inteiro e recomeça, até três tentativas. Efeito fora dos tetos ou da contagem de vínculos é defeito:
    /// propaga como exceção, com rollback e a unidade ainda <c>Pending</c>.
    /// </summary>
    public async Task<ReconciliationApplyOutcome> ApplyAsModeratorAsync(ModeratorSession moderator, Guid runId, Guid unitId,
        ReconciliationCutoverEvidence? evidence, IdempotencyKey key, CancellationToken ct)
    {
        var command = new ApplyCommand(moderator, runId, unitId, key, Intent(runId, unitId));
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(command, evidence, ct);
            }
            catch (RegistrationPlanChangedException)
            {
                // A transação antiga já voltou atrás: a próxima tranca o conjunto ampliado desde o começo.
            }
            catch (PostgresException error) when (error is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ApplyKeyIndex })
            {
                return new ReconciliationApplyOutcome.KeyReused();
            }
            catch (PostgresException error) when (RegistrationReconciliationRuns.IsTimeout(error, ct))
            {
                return new ReconciliationApplyOutcome.TimedOut();
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                LogDeadlock(_logger);
                return new ReconciliationApplyOutcome.Busy();
            }
            catch (PostgresException error) when (RegistrationReconciliationRuns.IsContention(error))
            {
                return new ReconciliationApplyOutcome.Busy();
            }
        }

        LogUnstablePlan(_logger);
        return new ReconciliationApplyOutcome.Busy();
    }

    /// <summary>Passos 1 a 4: tudo o que decide sem tocar a unidade.</summary>
    private async Task<ReconciliationApplyOutcome> ApplyOnceAsync(ApplyCommand command, ReconciliationCutoverEvidence? evidence,
        CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await RegistrationReconciliationRuns.BeginAsync(connection, IsolationLevel.ReadCommitted,
            _previews.QueryTimeout, ct);
        if (await LockRunAsync(connection, command.RunId, ct) is not { } run
            || await LockUnitAsync(connection, command, ct) is not { } unit)
        {
            return new ReconciliationApplyOutcome.NotFound();
        }

        if (!await IsAuthorizedAsync(connection, command, ct))
        {
            return new ReconciliationApplyOutcome.Forbidden();
        }

        if (await KeyBelongsToAnotherUnitAsync(connection, command, ct))
        {
            return new ReconciliationApplyOutcome.KeyReused();
        }

        if (unit.Receipt is { } receipt)
        {
            return new ReconciliationApplyOutcome.Receipt(receipt);
        }

        if (run.State == ClosedRun)
        {
            return new ReconciliationApplyOutcome.RunClosed();
        }

        // Só existe o seletor deste binário: um manifesto de outro seletor não pode ser reclassificado como o ADR manda.
        if (evidence is null || run.Evidence != evidence || run.SelectorVersion != LegacyRegistrationSelector.Version)
        {
            return new ReconciliationApplyOutcome.CutoverChanged();
        }

        return await ExecuteAsync(new UnitWork(connection, transaction, command, unit, run.Evidence), ct);
    }

    /// <summary>Passos 5 a 12: plano, locks na ordem do ADR-0044, releitura e o desfecho num commit único.</summary>
    private async Task<ReconciliationApplyOutcome> ExecuteAsync(UnitWork work, CancellationToken ct)
    {
        var (connection, _, command, unit, evidence) = work;
        var plan = await RegistrationLockPlan.ForSuspensionAsync(connection, unit.SubjectUserId, command.Moderator.UserId, ct);
        if (plan.Events.Length > _maxPlanEvents || plan.Users.Length > _maxPlanUsers)
        {
            return await FinishAsync(work, ReconciliationUnitState.NeedsReview, ReconciliationUnitRevalidation.PlanTooLarge, [], ct);
        }

        await Guard(connection, plan.Users, ct);
        if (!await IsAuthorizedAsync(connection, command, ct))
        {
            return new ReconciliationApplyOutcome.Forbidden();
        }

        await Execute(connection, "select 1 from safety.moderation_actions where id=$1 for share", ct, unit.OriginalMeasureId);
        await plan.LockEventsAsync(connection, ct);
        plan.RequireCovered(await RegistrationLockPlan.ForSuspensionAsync(connection, unit.SubjectUserId, command.Moderator.UserId, ct));
        var current = await LegacyRegistrationReconciliationPreviews.ReassessAsync(connection, evidence, unit.SubjectUserId, _clock.GetUtcNow(), ct);
        if (ReconciliationUnitRevalidation.Decide(unit.OriginalMeasureId, unit.MeasureVersion, unit.Items, current) is { } refusal)
        {
            return await FinishAsync(work, refusal.State, refusal.Reason, [], ct);
        }

        var facts = await ApplyItemsAsync(connection, unit, plan, ct);
        return await FinishAsync(work, ReconciliationUnitState.Applied, reason: null, facts, ct);
    }

    /// <summary>
    /// Passos 10 e 11, evento a evento. O plano restrito só escolhe a linha do sujeito neste evento: já está trancado pelo
    /// plano completo e nunca passa por <c>LockEventsAsync</c> nem por <c>RequireCovered</c>.
    /// </summary>
    private async Task<Guid[]> ApplyItemsAsync(NpgsqlConnection connection, UnitRow unit, RegistrationLockPlan plan, CancellationToken ct)
    {
        List<Guid> facts = [];
        foreach (var item in unit.Items)
        {
            var before = await ReadCountersAsync(connection, item.EventId, ct);
            await RegistrationTransitions.WithdrawForSuspensionAsync(connection, new RegistrationLockPlan([item.EventId], plan.Users),
                unit.SubjectUserId, unit.OriginalMeasureId, _clock.GetUtcNow(), ct);
            var after = await ReadCountersAsync(connection, item.EventId, ct);
            var promoted = await RegistrationTransitions.PromotionFactsAsync(connection, item.EventId, before.Version, after.Version, ct);
            var linked = await Execute(connection, """
                insert into ops.registration_reconciliation_unit_facts(fact_id,unit_id) select fact,$2 from unnest($1::uuid[]) fact
                """, ct, promoted, unit.Id);
            RequireExpectedEffects(item, before, after, linked);
            facts.AddRange(promoted);
        }

        return [.. facts];
    }

    /// <summary>
    /// A retirada incrementa a versão uma vez, e cada promoção de novo: as promoções efetivas saem do evento, não dos
    /// fatos, para a contagem de vínculos ter contra o que ser conferida. Uma retirada que não aconteceu dá -1 promoção,
    /// e a contagem, que nunca é negativa, também a recusa. A liberação é exata: o passo 9 já igualou versão e estado, e só
    /// a promoção é um teto, porque a fila e o relógio ainda podem reduzi-la.
    /// </summary>
    private static void RequireExpectedEffects(ReconciliationPreviewItem item, EventCounters before, EventCounters after, int linked)
    {
        var promotions = after.Version - before.Version - 1;
        var released = before.ConfirmedCount - after.ConfirmedCount + promotions;
        if (released != item.MaxReleasedSeats || promotions > item.MaxPromotions || linked != promotions)
        {
            throw new InvalidOperationException("Os efeitos da unidade divergem do manifesto.");
        }
    }

    /// <summary>Passo 12: recibo e auditoria no commit dos efeitos. O recibo guarda o instante real, depois dos locks.</summary>
    private async Task<ReconciliationApplyOutcome> FinishAsync(UnitWork work, ReconciliationUnitState state, string? reason, Guid[] facts,
        CancellationToken ct)
    {
        var (connection, transaction, command, unit, _) = work;
        var now = _clock.GetUtcNow();
        var correlation = ReconciliationAudit.Correlation(now);
        var recorded = await Execute(connection, """
            update ops.registration_reconciliation_units
            set state=$2,receipt_reason=$3,receipt_fact_ids=$4,receipt_executed_by=$5,receipt_executed_at=$6,receipt_correlation_id=$7,
              apply_idempotency_key=$8,apply_intent_hash=$9
            where id=$1 and state='Pending'
            """, ct, unit.Id, state.ToString(), reason is null ? DBNull.Value : reason, facts, command.Moderator.UserId, now, correlation,
            command.Key.Value, command.Intent);
        if (recorded != 1)
        {
            throw new InvalidOperationException("A unidade trancada deixou de estar Pending.");
        }

        var action = state == ReconciliationUnitState.Applied
            ? AuditAction.LegacyRegistrationReconciliationApplied
            : AuditAction.LegacyRegistrationReconciliationSkipped;
        await ReconciliationAudit.AppendAsync(connection, new AuditRecord(AuditActorKind.Moderator, command.Moderator.UserId, action,
            AuditTargetTypes.RegistrationReconciliationUnit, unit.Id), now, correlation, ct);
        await transaction.CommitAsync(ct);
        return new ReconciliationApplyOutcome.Receipt(new ReconciliationUnitReceipt(command.RunId, unit.Id, state, reason, facts,
            command.Moderator.UserId, now, correlation));
    }

    private Task<bool> IsAuthorizedAsync(NpgsqlConnection connection, ApplyCommand command, CancellationToken ct) =>
        LegacyRegistrationReconciliationPreviews.IsModeratorSessionAsync(connection, command.Moderator, _clock.GetUtcNow(), ct);

    /// <summary>
    /// <c>FOR SHARE</c>: applies não disputam o run, e o <c>FOR UPDATE</c> do encerramento espera todos eles.
    /// </summary>
    private static async Task<RunRow?> LockRunAsync(NpgsqlConnection connection, Guid run, CancellationToken ct)
    {
        await using var query = Command(connection, """
            select state,selector_version,cutover_evidence_id,cutover_lower,cutover_upper
            from ops.registration_reconciliation_runs where id=$1 for share
            """, run);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RunRow(reader.GetString(0), reader.GetInt32(1), ReconciliationCutoverEvidence.Of(reader.GetString(2),
            ReconciliationCutover.Between(reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4))));
    }

    /// <summary><c>NOWAIT</c>: outra aplicação da mesma unidade em curso vira 503 na hora, sem fila.</summary>
    private static async Task<UnitRow?> LockUnitAsync(NpgsqlConnection connection, ApplyCommand command, CancellationToken ct)
    {
        await using var query = Command(connection, $"""
            select id,subject_user_id,original_measure_id,measure_version,items::text,state,{RegistrationReconciliationRuns.ReceiptColumns}
            from ops.registration_reconciliation_units where id=$1 and run_id=$2 for update nowait
            """, command.UnitId, command.RunId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new UnitRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt64(3),
            RegistrationReconciliationRuns.ReadItems(reader.GetString(4)),
            RegistrationReconciliationRuns.ReadReceipt(reader, command.RunId, reader.GetGuid(0), stateColumn: 5));
    }

    /// <summary>A chave só detecta intento divergente: a idempotência da unidade é o recibo, com qualquer chave.</summary>
    private static async Task<bool> KeyBelongsToAnotherUnitAsync(NpgsqlConnection connection, ApplyCommand command, CancellationToken ct)
    {
        await using var query = Command(connection, """
            select apply_intent_hash from ops.registration_reconciliation_units where receipt_executed_by=$1 and apply_idempotency_key=$2
            """, command.Moderator.UserId, command.Key.Value);
        return await query.ExecuteScalarAsync(ct) is byte[] recorded && !CryptographicOperations.FixedTimeEquals(recorded, command.Intent);
    }

    private static async Task<EventCounters> ReadCountersAsync(NpgsqlConnection connection, Guid eventId, CancellationToken ct)
    {
        await using var query = Command(connection, "select version,confirmed_count from events.events where id=$1", eventId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new EventCounters(reader.GetInt64(0), reader.GetInt32(1))
            : throw new InvalidOperationException("Evento trancado sem linha.");
    }

    private static byte[] Intent(Guid runId, Guid unitId) => SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"v1\n{runId:D}\n{unitId:D}")));

    [LoggerMessage(Level = LogLevel.Error, Message = "Deadlock ao aplicar uma unidade da reconciliação: defeito a investigar, sem retry.")]
    private static partial void LogDeadlock(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "O plano de uma unidade da reconciliação mudou nas três tentativas; nada foi gravado.")]
    private static partial void LogUnstablePlan(ILogger logger);

    private sealed record ApplyCommand(ModeratorSession Moderator, Guid RunId, Guid UnitId, IdempotencyKey Key, byte[] Intent);

    private sealed record RunRow(string State, int SelectorVersion, ReconciliationCutoverEvidence Evidence);

    private sealed record UnitRow(
        Guid Id,
        Guid SubjectUserId,
        Guid OriginalMeasureId,
        long MeasureVersion,
        ReconciliationPreviewItem[] Items,
        ReconciliationUnitReceipt? Receipt);

    private sealed record UnitWork(
        NpgsqlConnection Connection,
        NpgsqlTransaction Transaction,
        ApplyCommand Command,
        UnitRow Unit,
        ReconciliationCutoverEvidence Evidence);

    private readonly record struct EventCounters(long Version, int ConfirmedCount);
}
