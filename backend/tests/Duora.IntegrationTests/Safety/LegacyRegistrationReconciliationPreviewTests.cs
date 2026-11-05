using Duora.Core.Events;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Issue #321: prévia da reconciliação de inscrições legadas. O histórico anterior à política é montado direto na
/// fixture (medida inserida sem passar pelo escritor atual, que retiraria a inscrição na hora).
/// </summary>
public sealed class LegacyRegistrationReconciliationPreviewTests(PostgresFixture postgres)
{
    private static async Task<SuspendedRegistrationScenario> ScenarioAsync(PostgresFixture postgres)
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, s.Clock.GetUtcNow().AddHours(1));
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        return s;
    }

    /// <summary>Suspensão anterior à política: a medida existe, a conta está suspensa e a inscrição seguiu ativa.</summary>
    private static async Task<Guid> SuspendBeforePolicyAsync(SuspendedRegistrationScenario s, Guid subject)
    {
        var measure = Guid.NewGuid();
        await s.ExecuteAsync("""
            insert into safety.moderation_actions(id,subject_user_id,measure,reason_code,internal_reason,
              author_kind,decided_at,command_key,command_intent,source_code,source_id)
            values($1,$2,'Suspension','SelfieVerificationFailed','Synthetic legacy fixture',
              'Automated',$3,encode(sha256($1::text::bytea),'hex'),sha256($2::text::bytea),'synthetic',$1);
            """, measure, subject, s.Clock.GetUtcNow().AddSeconds(10));
        await s.ExecuteAsync("update identity.users set status_before_suspension=status,status='Suspended' where id=$1", subject);
        return measure;
    }

    private static ReconciliationCutoverEvidence CutoverAfterFixtures(SuspendedRegistrationScenario s) =>
        Evidence(ReconciliationCutover.Between(s.Clock.GetUtcNow().AddMinutes(1), s.Clock.GetUtcNow().AddMinutes(2)));

    private static ReconciliationCutoverEvidence Evidence(ReconciliationCutover cutover) =>
        ReconciliationCutoverEvidence.Of("synthetic-321", cutover);

    private static LegacyRegistrationReconciliationPreviews Previews(SuspendedRegistrationScenario s,
        int maxUnits = LegacyRegistrationReconciliationPreviews.MaxUnitsPerPage,
        int maxItems = LegacyRegistrationReconciliationPreviews.MaxItemsPerPage) =>
        new(s.Source, s.Clock, maxUnits, maxItems);

    [Fact]
    public async Task ALegacyRegistrationIsPreviewedWithItsForecastAndNoLogicalWrite()
    {
        await using var s = await ScenarioAsync(postgres);
        var measure = await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        var cutover = CutoverAfterFixtures(s);
        var before = await FingerprintAsync(s);

        var preview = await Previews(s).ReadAsync(cutover, after: null, s.Token);

        Assert.Equal(before, await FingerprintAsync(s));
        Assert.Equal(s.Clock.GetUtcNow(), preview.AsOf);
        Assert.Equal(LegacyRegistrationSelector.Version, preview.SelectorVersion);
        Assert.Equal(cutover, preview.Evidence);
        Assert.Equal((1, 0), (preview.ActionableRegistrations, preview.HeldRegistrations));
        Assert.Equal(new ReconciliationUnitCounts(Eligible: 1, Blocked: 0, Oversized: 0), preview.Units);
        Assert.Empty(preview.RegistrationsByPrimaryIssue);
        var unit = Assert.Single(preview.Page);
        ReconciliationPreviewItem[] items =
            [new ReconciliationPreviewItem(s.EventId, 1, RegistrationState.Confirmed, MaxReleasedSeats: 1, MaxPromotions: 1)];
        var digest = ReconciliationUnitDigest.Compute(LegacyRegistrationSelector.Version, cutover, s.Seed.Alice, measure, 1, items);
        Assert.Equal(new ReconciliationPreviewUnit(s.Seed.Alice, measure, 1, items, digest),
            unit, ReconciliationPreviewUnitComparer.Instance);
        Assert.Null(preview.Next);
    }

    [Fact]
    public async Task WithoutDeploymentEvidenceNothingIsListed()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);

        var preview = await Previews(s).ReadAsync(evidence: null, after: null, s.Token);

        Assert.Equal((0, 0), (preview.ActionableRegistrations, preview.HeldRegistrations));
        Assert.Empty(preview.Page);
        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.MissingDeploymentEvidence]);
    }

    [Fact]
    public async Task ARegistrationWithdrawnByTheCurrentPolicyIsNotACandidate()
    {
        await using var s = await ScenarioAsync(postgres);
        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.AlreadyWithdrawnBySuspension]);
        Assert.Equal((0, 0), (preview.ActionableRegistrations, preview.HeldRegistrations));
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task ALiftedOriginalIsReportedButNeverListed()
    {
        await using var s = await ScenarioAsync(postgres);
        var measure = await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("""
            update safety.moderation_actions set version=2,lifted_at=decided_at,lifted_by_kind='Automated',
              lift_reason='Synthetic revocation' where id=$1
            """, measure);
        await s.ExecuteAsync(
            "update identity.users set status=status_before_suspension,status_before_suspension=null where id=$1",
            s.Seed.Alice);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.LiftedOriginal]);
        Assert.Empty(preview.Page);
        Assert.Equal(new ReconciliationUnitCounts(0, 0, 0), preview.Units);
    }

    [Fact]
    public async Task ACounterThatDisagreesWithTheOccupantsBlocksTheWholeUnit()
    {
        await using var s = await ScenarioAsync(postgres);
        await s.Participation.RegisterAsync(s.Seed.Events[1], s.Seed.Alice, false, s.Token);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("update events.events set confirmed_count=0 where id=$1", s.EventId);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.CapacityMismatch]);
        Assert.Equal(new ReconciliationUnitCounts(Eligible: 0, Blocked: 1, Oversized: 0), preview.Units);
        Assert.Equal((0, 1), (preview.ActionableRegistrations, preview.HeldRegistrations));
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task PagesFollowTheDatabaseOrderAndStopAtTheUnitLimit()
    {
        await using var s = await ScenarioAsync(postgres);
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third })
        {
            await SuspendBeforePolicyAsync(s, user);
        }
        var cutover = CutoverAfterFixtures(s);
        var previews = Previews(s, maxUnits: 2);

        var first = await previews.ReadAsync(cutover, after: null, s.Token);
        var second = await previews.ReadAsync(cutover, first.Next, s.Token);

        Assert.Equal((await DatabaseOrderAsync(s, s.Seed.Alice, s.Seed.Bob, s.Seed.Third))[..2],
            first.Page.Select(unit => unit.SubjectUserId));
        Assert.NotNull(first.Next);
        Assert.Equal(new ReconciliationPreviewCursor(first.Page[1].SubjectUserId, first.Page[1].OriginalMeasureId), first.Next);
        var last = Assert.Single(second.Page);
        Assert.Null(second.Next);
        Assert.Equal(
            new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third }.Order(),
            first.Page.Concat(second.Page).Select(unit => unit.SubjectUserId).Order());
        Assert.Equal(3, first.ActionableRegistrations);
        Assert.Equal(3, second.ActionableRegistrations);
        var waiting = first.Page.Concat(second.Page).SelectMany(unit => unit.Items)
            .Where(item => item.State == RegistrationState.Waitlisted).ToArray();
        Assert.Equal(2, waiting.Length);
        // Quem estava na espera não libera vaga; a prévia prevê no máximo as vagas livres do evento.
        Assert.All(waiting, item => Assert.Equal((0, 0), (item.MaxReleasedSeats, item.MaxPromotions)));
        Assert.DoesNotContain(last.SubjectUserId, first.Page.Select(unit => unit.SubjectUserId));
    }

    [Fact]
    public async Task ThePageStopsBeforeExceedingTheItemLimit()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await SuspendBeforePolicyAsync(s, s.Seed.Bob);

        var preview = await Previews(s, maxItems: 1).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        var unit = Assert.Single(preview.Page);
        Assert.Equal((await DatabaseOrderAsync(s, s.Seed.Alice, s.Seed.Bob))[0], unit.SubjectUserId);
        Assert.Equal(new ReconciliationPreviewCursor(unit.SubjectUserId, unit.OriginalMeasureId), preview.Next);
        Assert.Equal(2, preview.Units.Eligible);
    }

    [Fact]
    public async Task OnlyEligibleWaitersCountTowardsTheForecast()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("update identity.users set status='Restricted' where id=any($1)",
            new[] { s.Seed.Bob, s.Seed.Third });

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        var item = Assert.Single(Assert.Single(preview.Page).Items);
        Assert.Equal((1, 0), (item.MaxReleasedSeats, item.MaxPromotions));
    }

    [Fact]
    public async Task ARunningEventAllowsTheWithdrawalButForecastsNoPromotion()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("update events.events set state='Running' where id=$1", s.EventId);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        var item = Assert.Single(Assert.Single(preview.Page).Items);
        Assert.Equal((1, 0), (item.MaxReleasedSeats, item.MaxPromotions));
    }

    [Fact]
    public async Task AClosedEventIsReportedAndLeavesNoUnit()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("update events.events set state='Completed' where id=$1", s.EventId);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.ClosedEvent]);
        Assert.Equal(new ReconciliationUnitCounts(0, 0, 0), preview.Units);
    }

    [Fact]
    public async Task ASuspendedAccountWithoutAnyMeasureStillAppearsInTheDiagnosis()
    {
        await using var s = await ScenarioAsync(postgres);
        await s.ExecuteAsync("update identity.users set status_before_suspension=status,status='Suspended' where id=$1",
            s.Seed.Third);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.MissingOriginal]);
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task ADecisionAtOrAfterTheUpperBoundIsPostCutover()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        var atomic = Evidence(ReconciliationCutover.Between(s.Clock.GetUtcNow(), s.Clock.GetUtcNow()));

        var preview = await Previews(s).ReadAsync(atomic, after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.PostCutover]);
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task AnInfiniteCheckInTimestampIsUntrusted()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("update events.registrations set checked_in_at='infinity' where event_id=$1 and user_id=$2",
            s.EventId, s.Seed.Alice);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.TimestampUntrusted]);
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task AnInfiniteEventStartIsUntrustedInsteadOfForecastingZero()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await s.ExecuteAsync("update events.events set starts_at='infinity' where id=$1", s.EventId);

        var preview = await Previews(s).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(1, preview.RegistrationsByPrimaryIssue[LegacyRegistrationIssue.TimestampUntrusted]);
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task AUnitAboveTheItemLimitIsOversizedNotTruncated()
    {
        await using var s = await ScenarioAsync(postgres);
        await s.Participation.RegisterAsync(s.Seed.Events[1], s.Seed.Alice, false, s.Token);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);

        var preview = await Previews(s, maxItems: 1).ReadAsync(CutoverAfterFixtures(s), after: null, s.Token);

        Assert.Equal(new ReconciliationUnitCounts(Eligible: 0, Blocked: 0, Oversized: 1), preview.Units);
        Assert.Empty(preview.Page);
    }

    [Fact]
    public async Task AnEventThatAlreadyStartedForecastsNoPromotion()
    {
        await using var s = await ScenarioAsync(postgres);
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        var cutover = CutoverAfterFixtures(s);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, s.Clock.GetUtcNow());

        var preview = await Previews(s).ReadAsync(cutover, after: null, s.Token);

        var item = Assert.Single(Assert.Single(preview.Page).Items);
        Assert.Equal((1, 0), (item.MaxReleasedSeats, item.MaxPromotions));
    }

    /// <summary>A ordem do PostgreSQL para uuid, que não coincide com <see cref="Guid.CompareTo(Guid)"/>.</summary>
    private static async Task<Guid[]> DatabaseOrderAsync(SuspendedRegistrationScenario s, params Guid[] ids) =>
        [.. (await s.TextAsync("select string_agg(id::text,',' order by id) from unnest($1::uuid[]) id", ids))
            .Split(',').Select(Guid.Parse)];

    private static Task<string> FingerprintAsync(SuspendedRegistrationScenario s) => s.TextAsync("""
        select jsonb_build_object(
          'r',(select jsonb_agg(to_jsonb(t) order by event_id,user_id) from events.registrations t),
          'e',(select jsonb_agg(to_jsonb(t) order by id) from events.events t),
          'u',(select jsonb_agg(to_jsonb(t) order by id) from identity.users t),
          's',(select jsonb_agg(to_jsonb(t) order by id) from identity.login_sessions t),
          'g',(select jsonb_agg(to_jsonb(t) order by user_id) from safety.user_guards t),
          'm',(select jsonb_agg(to_jsonb(t) order by id) from safety.moderation_actions t),
          'a',(select count(*) from ops.audit_events),
          'o',(select count(*) from ops.outbox_events),
          'j',(select count(*) from ops.jobs))::text
        """);

    /// <summary>Records com listas não têm igualdade estrutural; compara unidade e itens campo a campo.</summary>
    private sealed class ReconciliationPreviewUnitComparer : IEqualityComparer<ReconciliationPreviewUnit>
    {
        public static ReconciliationPreviewUnitComparer Instance { get; } = new();

        public bool Equals(ReconciliationPreviewUnit? x, ReconciliationPreviewUnit? y) =>
            x is not null && y is not null && x.SubjectUserId == y.SubjectUserId
            && x.OriginalMeasureId == y.OriginalMeasureId && x.MeasureVersion == y.MeasureVersion
            && x.Items.SequenceEqual(y.Items) && x.UnitDigest == y.UnitDigest;

        public int GetHashCode(ReconciliationPreviewUnit obj) => HashCode.Combine(obj.SubjectUserId, obj.OriginalMeasureId);
    }
}
