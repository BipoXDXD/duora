using System.Data;
using Duora.Core.Events;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Safety;

/// <summary>Uma inscrição candidata, com a versão observada e o teto de vagas liberadas e de promoções.</summary>
public sealed record ReconciliationPreviewItem(
    Guid EventId,
    long RegistrationVersion,
    RegistrationState State,
    int MaxReleasedSeats,
    int MaxPromotions);

/// <summary>Conta/medida original com suas inscrições candidatas, todas do mesmo snapshot.</summary>
public sealed record ReconciliationPreviewUnit(
    Guid SubjectUserId,
    Guid OriginalMeasureId,
    long MeasureVersion,
    IReadOnlyList<ReconciliationPreviewItem> Items);

/// <summary>Última unidade de uma página, na ordem do PostgreSQL por <c>(subject_user_id, original_suspension_id)</c>.</summary>
public readonly record struct ReconciliationPreviewCursor(Guid SubjectUserId, Guid OriginalMeasureId);

/// <param name="Eligible">Unidades com candidatas, sem bloqueio e dentro do teto de itens.</param>
/// <param name="Blocked">Unidades pausadas por indício de corrupção, conta incoerente ou sujeito privilegiado.</param>
/// <param name="Oversized">Unidades acima do teto de itens, que exigem revisão dedicada em vez de truncamento.</param>
public sealed record ReconciliationUnitCounts(int Eligible, int Blocked, int Oversized);

/// <summary>
/// Resultado da prévia. As contagens cobrem toda a população num único snapshot; a página traz só as unidades
/// elegíveis depois do cursor. Páginas pedidas em separado têm snapshots próprios: contagens globais são indicativas.
/// Inscrições candidatas se dividem entre <paramref name="ActionableRegistrations"/> (unidades elegíveis) e
/// <paramref name="HeldRegistrations"/> (unidades bloqueadas ou grandes demais), que nenhuma aplicação automática toca.
/// </summary>
public sealed record ReconciliationPreview(
    DateTimeOffset AsOf,
    int SelectorVersion,
    ReconciliationCutover? Cutover,
    IReadOnlyDictionary<LegacyRegistrationIssue, int> RegistrationsByPrimaryIssue,
    int ActionableRegistrations,
    int HeldRegistrations,
    ReconciliationUnitCounts Units,
    IReadOnlyList<ReconciliationPreviewUnit> Page,
    ReconciliationPreviewCursor? Next);

/// <summary>
/// Prévia estritamente sem escrita da reconciliação de inscrições legadas (issue #321, proposta seção 6): uma
/// transação <c>REPEATABLE READ READ ONLY</c> só com SELECTs. Não chama Guard, escritores, jobs nem <c>nextval</c>, e
/// não grava auditoria, outbox ou idempotência. Ainda não tem rota nem registro em DI: autorização, sessão sem
/// renovação e envelope assinado são etapas separadas.
/// </summary>
public sealed class LegacyRegistrationReconciliationPreviews
{
    public const int MaxUnitsPerPage = 10;
    public const int MaxItemsPerPage = 100;

    private readonly NpgsqlDataSource _source;
    private readonly TimeProvider _clock;
    private readonly int _maxUnits;
    private readonly int _maxItems;

    public LegacyRegistrationReconciliationPreviews(NpgsqlDataSource source, TimeProvider clock)
        : this(source, clock, MaxUnitsPerPage, MaxItemsPerPage)
    {
    }

    /// <summary>Tetos menores só para ensaiar paginação e unidades grandes com poucos dados sintéticos.</summary>
    internal LegacyRegistrationReconciliationPreviews(NpgsqlDataSource source, TimeProvider clock, int maxUnits, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        _source = source;
        _clock = clock;
        _maxUnits = maxUnits;
        _maxItems = maxItems;
    }

    public async Task<ReconciliationPreview> ReadAsync(ReconciliationCutover? cutover, ReconciliationPreviewCursor? after,
        CancellationToken ct)
    {
        var asOf = _clock.GetUtcNow();
        var rows = await ReadSnapshotAsync(after, ct);
        var units = rows.GroupBy(row => (row.SubjectUserId, row.OriginalMeasureId))
            .Select(group => Assess(group.ToArray(), cutover, asOf))
            .ToArray();
        return Summarize(asOf, cutover, units);
    }

    private ReconciliationPreview Summarize(DateTimeOffset asOf, ReconciliationCutover? cutover, UnitAssessment[] units)
    {
        var classified = units.SelectMany(unit => unit.Classifications).ToArray();
        var byIssue = classified.Where(c => c.Primary is not null)
            .GroupBy(c => c.Primary!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var eligible = units.Where(unit => unit.Kind == UnitKind.Eligible).ToArray();
        var pending = eligible.Where(unit => unit.AfterCursor).ToArray();
        List<ReconciliationPreviewUnit> page = [];
        var items = 0;
        foreach (var unit in pending)
        {
            if (page.Count == _maxUnits || items + unit.Unit!.Items.Count > _maxItems)
            {
                break;
            }
            page.Add(unit.Unit);
            items += unit.Unit.Items.Count;
        }
        var next = page.Count < pending.Length && page.Count > 0
            ? new ReconciliationPreviewCursor(page[^1].SubjectUserId, page[^1].OriginalMeasureId)
            : (ReconciliationPreviewCursor?)null;
        var actionable = eligible.Sum(unit => unit.Unit!.Items.Count);
        return new ReconciliationPreview(asOf, LegacyRegistrationSelector.Version, cutover, byIssue,
            actionable, classified.Count(c => c.IsCandidate) - actionable,
            new ReconciliationUnitCounts(eligible.Length, units.Count(u => u.Kind == UnitKind.Blocked),
                units.Count(u => u.Kind == UnitKind.Oversized)),
            page, next);
    }

    private UnitAssessment Assess(SnapshotRow[] rows, ReconciliationCutover? cutover, DateTimeOffset asOf)
    {
        var classifications = rows.Select(row => LegacyRegistrationSelector.Classify(row.Facts, cutover)).ToArray();
        var candidates = rows.Zip(classifications).Where(pair => pair.Second.IsCandidate).Select(pair => pair.First).ToArray();
        var first = rows[0];
        // Unidade sem nada que a reconciliação pudesse tocar fica fora das contagens de unidade.
        var touchable = rows.Any(row => LegacyRegistrationSelector.IsTouchable(row.Facts.Registration, row.Facts.Event));
        var kind = first.OriginalMeasureId is null || !touchable ? UnitKind.None
            : classifications.Any(c => c.BlocksUnit) ? UnitKind.Blocked
            : candidates.Length == 0 ? UnitKind.None
            : candidates.Length > _maxItems ? UnitKind.Oversized
            : UnitKind.Eligible;
        var unit = kind == UnitKind.Eligible
            ? new ReconciliationPreviewUnit(first.SubjectUserId, first.OriginalMeasureId!.Value,
                first.Facts.Measures.Active!.Version, [.. candidates.Select(row => Forecast(row, asOf))])
            : null;
        return new UnitAssessment(kind, classifications, unit, first.AfterCursor);
    }

    /// <summary>
    /// Mesma regra do helper de promoção: só Published e antes do início, no máximo as vagas livres depois da
    /// retirada e quem é elegível na espera. É um teto condicional; fila e elegibilidade podem mudar até a aplicação.
    /// </summary>
    private static ReconciliationPreviewItem Forecast(SnapshotRow row, DateTimeOffset asOf)
    {
        var released = row.Facts.Registration.State is RegistrationState.Confirmed or RegistrationState.CheckedIn ? 1 : 0;
        var @event = row.Facts.Event;
        var promotes = @event.State == EventState.Published && row.StartsAt is { } startsAt && asOf < startsAt;
        var available = Math.Max(0, @event.Capacity - @event.ConfirmedCount + released);
        return new ReconciliationPreviewItem(row.EventId, row.RegistrationVersion, row.Facts.Registration.State, released,
            promotes ? Math.Min(available, row.EligibleWaiters) : 0);
    }

    private async Task<List<SnapshotRow>> ReadSnapshotAsync(ReconciliationPreviewCursor? after, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        // Antes da primeira consulta: a partir daqui o PostgreSQL recusa qualquer escrita lógica nesta transação.
        await Execute(connection, "set transaction read only", ct);
        await Execute(connection, "set local statement_timeout='10s'", ct);
        await RequireReadOnlySnapshotAsync(connection, ct);
        await using var query = Command(connection, SnapshotSql, AccountRoles.Privileged.ToArray(),
            after?.SubjectUserId ?? (object)DBNull.Value, after?.OriginalMeasureId ?? (object)DBNull.Value);
        query.Parameters[1].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        query.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        List<SnapshotRow> rows = [];
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(SnapshotRow.From(reader));
            }
        }
        await transaction.CommitAsync(ct);
        return rows;
    }

    /// <summary>A garantia "sem escrita" é do banco, não da disciplina do SQL: sem ela, a prévia não roda.</summary>
    private static async Task RequireReadOnlySnapshotAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var check = Command(connection, """
            select current_setting('transaction_read_only')='on'
              and current_setting('transaction_isolation')='repeatable read'
            """);
        if (!(bool)(await check.ExecuteScalarAsync(ct))!)
        {
            throw new InvalidOperationException("A prévia exige uma transação REPEATABLE READ READ ONLY.");
        }
    }

    // População: contas suspensas ou com alguma suspensão registrada, e todas as suas inscrições, inclusive
    // terminais, para que ausências apareçam no diagnóstico em vez de sumirem num INNER JOIN. Infinito vira nulo.
    private const string SnapshotSql = """
        with subjects as (
          select id from identity.users where status='Suspended'
          union select subject_user_id from safety.moderation_actions where measure='Suspension'),
        measures as (
          select s.id user_id,
            (count(m.id) filter (where m.lifted_at is null))::int active_count,
            coalesce(bool_or(m.lifted_at is not null),false) has_lifted,
            (array_agg(m.id order by m.id) filter (where m.lifted_at is null))[1] active_id,
            (array_agg(m.version order by m.id) filter (where m.lifted_at is null))[1] active_version,
            (array_agg(m.decided_at order by m.id) filter (where m.lifted_at is null))[1] active_decided_at
          from subjects s left join safety.moderation_actions m on m.subject_user_id=s.id and m.measure='Suspension'
          group by s.id),
        occupancy as (
          select e.id event_id,
            (count(r.user_id) filter (where r.state in ('Confirmed','CheckedIn')))::int occupants,
            coalesce(bool_or(r.state='Waitlisted'
              and (r.waitlist_position<1 or r.waitlist_position>=e.next_waitlist_position)),false) invalid_waitlist
          from events.events e left join events.registrations r on r.event_id=e.id
          where e.id in (select r2.event_id from events.registrations r2 join subjects s2 on s2.id=r2.user_id)
          group by e.id)
        select r.user_id, ms.active_id, ($2::uuid is null or (r.user_id,ms.active_id)>($2::uuid,$3::uuid)) after_cursor,
          u.status, u.status_before_suspension is not null,
          exists(select 1 from identity.user_roles ur join identity.roles ro on ro.id=ur.role_id
            where ur.user_id=u.id and ro.name=any($1)) privileged,
          ms.active_count, ms.has_lifted, ms.active_version, ms.active_decided_at,
          r.event_id, r.version, r.state, case when isfinite(r.registered_at) then r.registered_at end,
          r.waitlist_position, case when isfinite(r.checked_in_at) then r.checked_in_at end,
          r.withdrawn_by_suspension_id is not null,
          exists(select 1 from events.pairings p where p.event_id=r.event_id and p.released_at is null
            and (p.user_low=r.user_id or p.user_high=r.user_id)) reservation,
          e.state, e.capacity, e.confirmed_count, o.occupants, e.next_waitlist_position, o.invalid_waitlist,
          case when isfinite(e.starts_at) then e.starts_at end,
          coalesce(not isfinite(r.checked_in_at),false) checked_in_untrusted,
          not isfinite(e.starts_at) starts_at_untrusted,
          (select count(*) from events.registrations w join identity.users wu on wu.id=w.user_id
            where w.event_id=r.event_id and w.state='Waitlisted'
              and wu.status='Active' and wu.email_confirmed
              and exists(select 1 from identity.selfie_verifications v where v.user_id=wu.id and v.state='Passed'))::int
        from events.registrations r
          join subjects s on s.id=r.user_id
          join identity.users u on u.id=r.user_id
          join measures ms on ms.user_id=r.user_id
          join events.events e on e.id=r.event_id
          join occupancy o on o.event_id=r.event_id
        order by r.user_id, ms.active_id, r.event_id
        """;

    private enum UnitKind
    {
        None,
        Eligible,
        Blocked,
        Oversized,
    }

    private sealed record UnitAssessment(UnitKind Kind, LegacyRegistrationClassification[] Classifications,
        ReconciliationPreviewUnit? Unit, bool AfterCursor);

    private sealed record SnapshotRow(
        Guid SubjectUserId,
        Guid? OriginalMeasureId,
        bool AfterCursor,
        Guid EventId,
        long RegistrationVersion,
        DateTimeOffset? StartsAt,
        int EligibleWaiters,
        LegacyRegistrationFacts Facts)
    {
        public static SnapshotRow From(NpgsqlDataReader reader)
        {
            var measureId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
            var account = new LegacyAccountFacts(Enum.Parse<AccountStatus>(reader.GetString(3)), reader.GetBoolean(4),
                reader.GetBoolean(5));
            var active = measureId is { } id
                ? new LegacyMeasureFacts(id, reader.GetInt64(8), reader.GetFieldValue<DateTimeOffset>(9))
                : null;
            var measures = new LegacyMeasuresFacts(active, reader.GetInt32(6), reader.GetBoolean(7));
            var registration = new LegacyRegistrationRow(
                Enum.Parse<RegistrationState>(reader.GetString(12)),
                NullableInstant(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14),
                NullableInstant(reader, 15),
                reader.GetBoolean(16),
                reader.GetBoolean(17),
                reader.GetBoolean(25));
            var @event = new LegacyEventFacts(Enum.Parse<EventState>(reader.GetString(18)), reader.GetInt32(19),
                reader.GetInt32(20), reader.GetInt32(21), reader.GetInt64(22), reader.GetBoolean(23),
                reader.GetBoolean(26));
            return new SnapshotRow(reader.GetGuid(0), measureId, !reader.IsDBNull(2) && reader.GetBoolean(2),
                reader.GetGuid(10), reader.GetInt64(11), NullableInstant(reader, 24), reader.GetInt32(27),
                new LegacyRegistrationFacts(account, measures, registration, @event));
        }

        private static DateTimeOffset? NullableInstant(NpgsqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }
}
