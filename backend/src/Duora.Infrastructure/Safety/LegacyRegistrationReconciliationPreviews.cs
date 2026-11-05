using System.Data;
using Duora.Core.Events;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Safety;

/// <summary>
/// Conta/medida original com suas inscrições candidatas, todas do mesmo snapshot, e o digest que a identifica na
/// criação de um run (ADR-0049, D1).
/// </summary>
public sealed record ReconciliationPreviewUnit(
    Guid SubjectUserId,
    Guid OriginalMeasureId,
    long MeasureVersion,
    IReadOnlyList<ReconciliationPreviewItem> Items,
    string UnitDigest);

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
    ReconciliationCutoverEvidence? Evidence,
    IReadOnlyDictionary<LegacyRegistrationIssue, int> RegistrationsByPrimaryIssue,
    int ActionableRegistrations,
    int HeldRegistrations,
    ReconciliationUnitCounts Units,
    IReadOnlyList<ReconciliationPreviewUnit> Page,
    ReconciliationPreviewCursor? Next);

/// <summary>Resultado da prévia pedida por quem modera.</summary>
public abstract record ReconciliationPreviewOutcome
{
    private ReconciliationPreviewOutcome()
    {
    }

    public sealed record Ready(ReconciliationPreview Preview) : ReconciliationPreviewOutcome;

    /// <summary>A sessão não autoriza (ou não autoriza mais) a moderação no snapshot: nenhuma inscrição foi lida.</summary>
    public sealed record Forbidden : ReconciliationPreviewOutcome;

    /// <summary>O snapshot passou do <c>statement_timeout</c>; nada foi lido até o fim e nada foi escrito.</summary>
    public sealed record TimedOut : ReconciliationPreviewOutcome;
}

/// <summary>
/// Prévia estritamente sem escrita da reconciliação de inscrições legadas (issue #321, proposta seção 6): uma
/// transação <c>REPEATABLE READ READ ONLY</c> só com SELECTs. Não chama Guard, escritores, jobs nem <c>nextval</c>, e
/// não grava auditoria, outbox ou idempotência. A rota (API-ADM25) usa <see cref="ReadAsModeratorAsync"/>, que
/// revalida a sessão de moderação dentro do mesmo snapshot e devolve o timeout como resultado próprio; o envelope
/// assinado é etapa separada.
/// </summary>
public sealed class LegacyRegistrationReconciliationPreviews
{
    public const int MaxUnitsPerPage = ReconciliationRunOrder.MaxUnits;
    public const int MaxItemsPerPage = 100;

    /// <summary>Teto de cada consulta do snapshot (proposta, seção 6).</summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(10);

    private readonly NpgsqlDataSource _source;
    private readonly TimeProvider _clock;
    private readonly int _maxUnits;
    private readonly int _maxItems;
    private readonly TimeSpan _statementTimeout;

    public LegacyRegistrationReconciliationPreviews(NpgsqlDataSource source, TimeProvider clock)
        : this(source, clock, MaxUnitsPerPage, MaxItemsPerPage, StatementTimeout)
    {
    }

    /// <summary>
    /// Tetos menores só para ensaiar paginação, unidades grandes e timeout com poucos dados sintéticos.
    /// </summary>
    internal LegacyRegistrationReconciliationPreviews(NpgsqlDataSource source, TimeProvider clock, int maxUnits, int maxItems,
        TimeSpan? statementTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        var timeout = statementTimeout ?? StatementTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.FromMilliseconds(1));
        _source = source;
        _clock = clock;
        _maxUnits = maxUnits;
        _maxItems = maxItems;
        _statementTimeout = timeout;
    }

    /// <summary>Teto de cada consulta desta instância, que a criação de um run segue no próprio snapshot.</summary>
    internal TimeSpan QueryTimeout => _statementTimeout;

    /// <summary>
    /// Prévia para quem modera: no próprio snapshot, antes de ler inscrições, confere a sessão administrativa viva com
    /// papel <c>moderator</c>, MFA provado e TOTP recente, como os comandos de suspensão. Null quando a sessão não
    /// (ou não mais) autoriza: nenhuma linha de participante é lida.
    /// </summary>
    public async Task<ReconciliationPreviewOutcome> ReadAsModeratorAsync(ModeratorSession moderator,
        ReconciliationCutoverEvidence? evidence, ReconciliationPreviewCursor? after, CancellationToken ct)
    {
        var asOf = _clock.GetUtcNow();
        List<SnapshotRow>? rows;
        try
        {
            rows = await ReadSnapshotAsync(moderator, asOf, after, ct);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.QueryCanceled && !ct.IsCancellationRequested)
        {
            // 57014 sem cancelamento do pedido é o statement_timeout desta transação READ ONLY: indisponibilidade
            // passageira da prévia, não defeito. O cancelamento do cliente continua propagando como antes.
            return new ReconciliationPreviewOutcome.TimedOut();
        }

        return rows is null
            ? new ReconciliationPreviewOutcome.Forbidden()
            : new ReconciliationPreviewOutcome.Ready(Summarize(asOf, evidence, Assess(rows, evidence, asOf)));
    }

    /// <summary>Prévia sem conferir quem pede; só para ensaio do seletor nos testes.</summary>
    internal async Task<ReconciliationPreview> ReadAsync(ReconciliationCutoverEvidence? evidence, ReconciliationPreviewCursor? after,
        CancellationToken ct)
    {
        var asOf = _clock.GetUtcNow();
        var rows = await ReadSnapshotAsync(moderator: null, asOf, after, ct)
            ?? throw new InvalidOperationException("Sem sessão a conferir, o snapshot sempre devolve linhas.");
        return Summarize(asOf, evidence, Assess(rows, evidence, asOf));
    }

    /// <summary>
    /// As unidades elegíveis destas contas, com a mesma classificação e o mesmo digest da prévia, no snapshot e na
    /// transação de quem chama (ADR-0049, D1): é o recálculo da criação de um run.
    /// </summary>
    internal async Task<IReadOnlyList<ReconciliationPreviewUnit>> ReclassifyAsync(NpgsqlConnection connection,
        ReconciliationCutoverEvidence? evidence, Guid[] subjects, DateTimeOffset asOf, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(connection, after: null, subjects, ct);
        return [.. Assess(rows, evidence, asOf).Where(unit => unit.Kind == UnitKind.Eligible).Select(unit => unit.Unit!)];
    }

    private UnitAssessment[] Assess(List<SnapshotRow> rows, ReconciliationCutoverEvidence? evidence, DateTimeOffset asOf) =>
        [.. rows.GroupBy(row => (row.SubjectUserId, row.OriginalMeasureId))
            .Select(group => Assess(group.ToArray(), evidence, asOf))];

    private ReconciliationPreview Summarize(DateTimeOffset asOf, ReconciliationCutoverEvidence? evidence, UnitAssessment[] units)
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
        return new ReconciliationPreview(asOf, LegacyRegistrationSelector.Version, evidence, byIssue,
            actionable, classified.Count(c => c.IsCandidate) - actionable,
            new ReconciliationUnitCounts(eligible.Length, units.Count(u => u.Kind == UnitKind.Blocked),
                units.Count(u => u.Kind == UnitKind.Oversized)),
            page, next);
    }

    private UnitAssessment Assess(SnapshotRow[] rows, ReconciliationCutoverEvidence? evidence, DateTimeOffset asOf)
    {
        var classifications = rows.Select(row => LegacyRegistrationSelector.Classify(row.Facts, evidence?.Cutover)).ToArray();
        var candidates = rows.Zip(classifications).Where(pair => pair.Second.IsCandidate).Select(pair => pair.First).ToArray();
        var first = rows[0];
        // Unidade sem nada que a reconciliação pudesse tocar fica fora das contagens de unidade.
        var touchable = rows.Any(row => LegacyRegistrationSelector.IsTouchable(row.Facts.Registration, row.Facts.Event));
        var kind = first.OriginalMeasureId is null || !touchable ? UnitKind.None
            : classifications.Any(c => c.BlocksUnit) ? UnitKind.Blocked
            : candidates.Length == 0 ? UnitKind.None
            : candidates.Length > _maxItems ? UnitKind.Oversized
            : UnitKind.Eligible;
        var unit = kind == UnitKind.Eligible ? EligibleUnit(first, candidates, evidence!, asOf) : null;
        return new UnitAssessment(kind, classifications, unit, first.AfterCursor);
    }

    /// <summary>Unidade elegível sempre tem corte: sem ele nenhuma inscrição é candidata.</summary>
    private static ReconciliationPreviewUnit EligibleUnit(SnapshotRow first, SnapshotRow[] candidates,
        ReconciliationCutoverEvidence evidence, DateTimeOffset asOf)
    {
        var subject = first.SubjectUserId;
        var measure = first.OriginalMeasureId!.Value;
        var version = first.Facts.Measures.Active!.Version;
        ReconciliationPreviewItem[] items = [.. candidates.Select(row => Forecast(row, asOf))];
        return new ReconciliationPreviewUnit(subject, measure, version, items, ReconciliationUnitDigest.Compute(
            LegacyRegistrationSelector.Version, evidence, subject, measure, version, items));
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

    private async Task<List<SnapshotRow>?> ReadSnapshotAsync(ModeratorSession? moderator, DateTimeOffset asOf,
        ReconciliationPreviewCursor? after, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        // Antes da primeira consulta: a partir daqui o PostgreSQL recusa qualquer escrita lógica nesta transação.
        await Execute(connection, "set transaction read only", ct);
        await Execute(connection, "select set_config('statement_timeout', $1, true)", ct,
            $"{(long)_statementTimeout.TotalMilliseconds}ms");
        await RequireReadOnlySnapshotAsync(connection, ct);
        if (moderator is { } session && !await IsModeratorSessionAsync(connection, session, asOf, ct))
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var rows = await ReadRowsAsync(connection, after, subjects: null, ct);
        await transaction.CommitAsync(ct);
        return rows;
    }

    private static async Task<List<SnapshotRow>> ReadRowsAsync(NpgsqlConnection connection,
        ReconciliationPreviewCursor? after, Guid[]? subjects, CancellationToken ct)
    {
        await using var query = Command(connection, SnapshotSql, AccountRoles.Privileged.ToArray(),
            after?.SubjectUserId ?? (object)DBNull.Value, after?.OriginalMeasureId ?? (object)DBNull.Value,
            subjects ?? (object)DBNull.Value);
        query.Parameters[1].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        query.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        query.Parameters[3].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid;
        List<SnapshotRow> rows = [];
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(SnapshotRow.From(reader));
        }
        return rows;
    }

    /// <summary>
    /// Mesmo critério do <c>AccountSuspensions</c> para comandos de moderação (conta ativa com MFA, papel
    /// <c>moderator</c>, sessão administrativa viva com o security stamp atual, MFA provado) e TOTP no intervalo
    /// fechado de <see cref="RecentAuthentication.Window"/>; prova no futuro não vale.
    /// </summary>
    internal static async Task<bool> IsModeratorSessionAsync(NpgsqlConnection connection, ModeratorSession moderator,
        DateTimeOffset now, CancellationToken ct)
    {
        if (moderator.UserId == Guid.Empty || moderator.SessionId == Guid.Empty)
        {
            return false;
        }

        await using var query = Command(connection, """
            select s.totp_verified_at
            from identity.users u join identity.login_sessions s on s.user_id=u.id
            where u.id=$1 and s.id=$2
              and u.status='Active' and u.email_confirmed and u.two_factor_enabled
              and u.temporary_password_expires_at is null
              and exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id=u.id and r.name=$3)
              and s.policy='Administrative' and s.security_stamp=u.security_stamp
              and s.revoked_at is null and s.idle_expires_at>$4 and s.absolute_expires_at>$4
              and s.mfa_verified_at is not null and s.mfa_verified_at<=$4
            """, moderator.UserId, moderator.SessionId, AccountRoles.Moderator, now);
        List<DateTimeOffset?> proofs = [];
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                proofs.Add(reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0));
            }
        }
        return proofs is [var proof] && RecentAuthentication.IsRecent(proof, now);
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
    // $4, quando presente, restringe as contas sem mudar a ocupação nem a fila dos eventos: é o recálculo de um run.
    private const string SnapshotSql = """
        with subjects as (
          select id from (
            select id from identity.users where status='Suspended'
            union select subject_user_id from safety.moderation_actions where measure='Suspension') s
          where $4::uuid[] is null or id=any($4)),
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
