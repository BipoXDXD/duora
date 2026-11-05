using Duora.Core.Safety;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-ADM28 (issue #321, ADR-0049, protocolo), com PostgreSQL real e dados sintéticos: o que uma unidade aplicada grava,
/// o que ela não toca, os desfechos sem retirada e o recibo como idempotência.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationApplyTests(PostgresFixture postgres)
{
    private const string FactType = "events.waitlisted_registration_promoted";

    private static ReconciliationUnitReceipt ReceiptOf(ReconciliationApplyOutcome outcome) =>
        Assert.IsType<ReconciliationApplyOutcome.Receipt>(outcome).Value;

    [Fact]
    public async Task ApplyingWithdrawsPromotesLinksAndAuditsInOneCommit()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var measure = await s.TextAsync("select md5(row(m.*)::text) from safety.moderation_actions m where id=$1", a.Measure);
        var accounts = await a.AccountsAsync();

        var receipt = ReceiptOf(await a.ApplyAsync(key: "apply-1"));

        Assert.Equal((a.Run, a.Unit, ReconciliationUnitState.Applied, (string?)null, a.ModeratorId, s.Clock.GetUtcNow()),
            (receipt.RunId, receipt.UnitId, receipt.State, receipt.Reason, receipt.ExecutedByUserId, receipt.ExecutedAt));
        Assert.False(string.IsNullOrEmpty(receipt.CorrelationId));
        var fact = Assert.Single(receipt.FactIds);
        Assert.Equal("Applied", await a.UnitStateAsync());
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2 and state='Withdrawn' and version=2
              and withdrawn_by_suspension_id=$3 and withdrawn_from_state='Confirmed' and withdrawn_at=$4
            """, s.EventId, s.Seed.Alice, a.Measure, s.Clock.GetUtcNow()));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync($"""
            select count(*) from ops.outbox_events e join ops.registration_reconciliation_unit_facts f on f.fact_id=e.id
            where e.id=$1 and f.unit_id=$2 and e.type='{FactType}' and e.resource_id=$3
              and e.resource_version=(select version from events.events where id=$3)
            """, fact, a.Unit, s.EventId));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.registration_reconciliation_units where id=$1 and receipt_fact_ids=$2 and receipt_reason is null
              and receipt_executed_by=$3 and receipt_executed_at=$4 and apply_idempotency_key='apply-1'
              and octet_length(apply_intent_hash)=32 and receipt_correlation_id=$5
            """, a.Unit, new[] { fact }, a.ModeratorId, s.Clock.GetUtcNow(), receipt.CorrelationId));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_applied'
              and actor_kind='Moderator' and actor_id=$1 and target_type='registration_reconciliation_unit' and target_id=$2
              and occurred_at=$3
            """, a.ModeratorId, a.Unit, s.Clock.GetUtcNow()));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='events.registration_withdrawn_by_suspension'
              and actor_kind='System' and actor_id is null and target_id=$1
            """, s.EventId));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='events.registration_promoted' and actor_kind='System' and target_id=$1
            """, s.EventId));
        Assert.Equal(measure, await s.TextAsync("select md5(row(m.*)::text) from safety.moderation_actions m where id=$1", a.Measure));
        Assert.Equal("Suspended", await s.TextAsync("select status from identity.users where id=$1", s.Seed.Alice));
        Assert.Equal(accounts, await a.AccountsAsync());
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.outbox_deliveries where outbox_event_id=$1", fact));
    }

    [Fact]
    public async Task ARegistrationOutsideTheManifestIsNeverTouched()
    {
        var outside = Guid.Empty;
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, arrange: async s =>
        {
            outside = s.Seed.Events[1];
            await s.ExecuteAsync("update events.events set capacity=1,starts_at=$2 where id=$1", outside, s.Clock.GetUtcNow().AddHours(1));
            await s.Participation.RegisterAsync(outside, s.Seed.Alice, false, s.Token);
            await s.Participation.RegisterAsync(outside, s.Seed.Third, false, s.Token);
            // Inscrita depois da medida, que nasce adiante com o login do moderador: não é candidata, e o plano ainda a tranca.
            await s.ExecuteAsync("update events.registrations set registered_at=$3 where event_id=$1 and user_id=$2",
                outside, s.Seed.Alice, s.Clock.GetUtcNow().AddMinutes(30));
        });
        var s = a.S;
        var before = await s.TextAsync("""
            select string_agg(md5(row(r.*)::text),',' order by user_id)||(select md5(row(e.*)::text) from events.events e where e.id=$1)
            from events.registrations r where event_id=$1
            """, outside);

        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await a.ApplyAsync()).State);

        Assert.Equal(before, await s.TextAsync("""
            select string_agg(md5(row(r.*)::text),',' order by user_id)||(select md5(row(e.*)::text) from events.events e where e.id=$1)
            from events.registrations r where event_id=$1
            """, outside));
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
    }

    [Fact]
    public async Task EveryApprovedEventIsAppliedAndEachFactIsLinked()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, arrange: async s =>
        {
            var other = s.Seed.Events[1];
            await s.ExecuteAsync("update events.events set capacity=1,starts_at=$2 where id=$1", other, s.Clock.GetUtcNow().AddHours(1));
            await s.Participation.RegisterAsync(other, s.Seed.Alice, false, s.Token);
            await s.Participation.RegisterAsync(other, s.Seed.Third, false, s.Token);
        });
        var s = a.S;

        var receipt = ReceiptOf(await a.ApplyAsync());

        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Equal(2, receipt.FactIds.Count);
        Assert.Equal(2, await s.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts where unit_id=$1", a.Unit));
        Assert.Equal(2, await s.CountAsync("select count(*) from events.registrations where user_id=$1 and state='Withdrawn'", s.Seed.Alice));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action like 'events.legacy_%'"));
    }

    /// <summary>Vínculo pela faixa de versões (D3): o fato anterior do mesmo evento é da outra unidade.</summary>
    [Fact]
    public async Task TwoUnitsOfTheSameEventLinkOnlyTheirOwnFacts()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, pick: s => Task.FromResult(new[] { s.Seed.Alice, s.Seed.Third }));
        var s = a.S;
        var first = ReceiptOf(await a.ApplyAsync(a.Units[s.Seed.Alice]));

        var second = ReceiptOf(await a.ApplyAsync(a.Units[s.Seed.Third]));

        Assert.Single(first.FactIds);
        Assert.Equal(ReconciliationUnitState.Applied, second.State);
        Assert.Empty(second.FactIds);
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts where unit_id=$1 and fact_id=$2",
            a.Units[s.Seed.Alice], first.FactIds[0]));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
    }

    [Fact]
    public async Task AWaitlistedSubjectReleasesNoSeatAndLinksNothing()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, pick: s => Task.FromResult(new[] { s.Seed.Bob }));
        var s = a.S;

        var receipt = ReceiptOf(await a.ApplyAsync(a.Units[s.Seed.Bob]));

        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Empty(receipt.FactIds);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2
              and withdrawn_from_state='Waitlisted' and withdrawn_waitlist_position is not null
            """, s.EventId, s.Seed.Bob));
    }

    [Fact]
    public async Task ACheckedInSubjectOfARunningEventFreesTheSeatWithoutPromotion()
    {
        var checkedInAt = DateTimeOffset.MinValue;
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, arrange: async s =>
        {
            checkedInAt = s.Clock.GetUtcNow();
            await s.ExecuteAsync("update events.events set state='Running' where id=$1", s.EventId);
            await s.ExecuteAsync("update events.registrations set state='CheckedIn',checked_in_at=$3 where event_id=$1 and user_id=$2",
                s.EventId, s.Seed.Alice, checkedInAt);
        });
        var s = a.S;

        var receipt = ReceiptOf(await a.ApplyAsync());

        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Empty(receipt.FactIds);
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(0);
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2 and state='Withdrawn'
              and withdrawn_from_state='CheckedIn' and checked_in_at=$3
            """, s.EventId, s.Seed.Alice, checkedInAt));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ThePromotionStopsExactlyAtTheStartOfTheEvent(long ticksBeforeStart, bool promotes)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var startsAt = s.Clock.GetUtcNow().AddMinutes(5);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, startsAt);
        s.Clock.SetUtcNow(startsAt.AddTicks(-ticksBeforeStart * 10));

        var receipt = ReceiptOf(await a.ApplyAsync());

        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Equal(promotes ? 1 : 0, receipt.FactIds.Count);
        Assert.Equal(promotes ? "Confirmed" : "Waitlisted", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(promotes ? 1 : 0);
    }

    public static TheoryData<string, string, string> Divergences => new()
    {
        { "update events.registrations set version=version+1 where event_id=$1 and user_id=$2", "SkippedChanged", "RegistrationChanged" },
        { "update safety.moderation_actions set version=2,lifted_at=decided_at,lifted_by_kind='Automated',lift_reason='Sintética' where subject_user_id=$2 and $1=$1", "SkippedChanged", "MeasureChanged" },
        { "update events.events set state='Cancelled' where id=$1 and $2=$2", "SkippedChanged", "ClosedEvent" },
        { "update events.events set confirmed_count=0 where id=$1 and $2=$2", "SkippedChanged", "CapacityMismatch" },
        { "update identity.users set status_before_suspension=null where id=$2 and $1=$1", "SkippedChanged", "InconsistentAccount" },
        { "update events.events set capacity=capacity+1 where id=$1 and $2=$2", "NeedsReview", "CeilingExceeded" },
    };

    [Theory]
    [MemberData(nameof(Divergences))]
    public async Task ADivergenceUnderLockWritesOnlyTheReceiptAndTheAudit(string change, string state, string reason)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await s.ExecuteAsync(change, s.EventId, s.Seed.Alice);
        var before = await a.FingerprintAsync();

        var receipt = ReceiptOf(await a.ApplyAsync(key: "skip-1"));

        Assert.Equal((state, reason), (receipt.State.ToString(), receipt.Reason));
        Assert.Empty(receipt.FactIds);
        Assert.Equal(before, await a.FingerprintAsync());
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.registration_reconciliation_units where id=$1 and state=$2 and receipt_reason=$3
              and cardinality(receipt_fact_ids)=0 and receipt_executed_by=$4 and apply_idempotency_key='skip-1'
            """, a.Unit, state, reason, a.ModeratorId));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_skipped'
              and actor_kind='Moderator' and actor_id=$1 and target_type='registration_reconciliation_unit' and target_id=$2
            """, a.ModeratorId, a.Unit));
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.audit_events where action like 'events.registration_%'"));
    }

    [Fact]
    public async Task AnUnexpectedActiveReservationNeedsReview()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await s.ExecuteAsync("""
            insert into events.pairings(id,round_id,event_id,user_low,user_high,algorithm_version,communication_language,
              communication_policy_version,created_at)
            select gen_random_uuid(),id,event_id,least($2::uuid,$3::uuid),greatest($2::uuid,$3::uuid),2,'en',1,now()
            from events.rounds where event_id=$1 limit 1
            """, s.EventId, s.Seed.Alice, s.Seed.Bob);
        var before = await a.FingerprintAsync();

        var receipt = ReceiptOf(await a.ApplyAsync());

        Assert.Equal((ReconciliationUnitState.NeedsReview, "ActiveReservation"), (receipt.State, receipt.Reason));
        Assert.Equal(before, await a.FingerprintAsync());
    }

    [Theory]
    [InlineData(0, RegistrationReconciliationCoordinator.MaxPlanUsers, "PlanTooLarge")]
    [InlineData(RegistrationReconciliationCoordinator.MaxPlanEvents, 3, "PlanTooLarge")]
    [InlineData(1, 4, null)]
    public async Task ThePlanCeilingsAreInclusiveAndCommitTheReceiptBeforeTheGuards(int maxEvents, int maxUsers, string? reason)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var before = await a.FingerprintAsync();
        var guards = await s.CountAsync("select count(*) from safety.user_guards");

        var receipt = ReceiptOf(await a.ApplyAsync(maxPlanEvents: maxEvents, maxPlanUsers: maxUsers));

        Assert.Equal(reason, receipt.Reason);
        if (reason is null)
        {
            Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
            return;
        }

        Assert.Equal(ReconciliationUnitState.NeedsReview, receipt.State);
        Assert.Equal(before, await a.FingerprintAsync());
        Assert.Equal(guards, await s.CountAsync("select count(*) from safety.user_guards"));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_skipped'"));
    }

    [Fact]
    public async Task AReplayReturnsTheReceiptWithoutTheWritersEvenWithAnotherKeyAModeratorOrAClosedRun()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var first = ReceiptOf(await a.ApplyAsync(key: "lost-response"));
        using var other = await RegistrationReconciliationPreviewEffectsTests.ModeratorAsync(s, a.Host);
        var sessionId = await MfaScenario.ReadSessionIdAsync(other);
        var otherSession = new ModeratorSession(
            Guid.Parse(await s.TextAsync("select user_id::text from identity.login_sessions where id=$1", sessionId)), sessionId);
        var before = await a.FingerprintAsync();
        var audits = await s.CountAsync("select count(*) from ops.audit_events");

        Assert.Equivalent(first, ReceiptOf(await a.ApplyAsync(key: "lost-response")));
        Assert.Equivalent(first, ReceiptOf(await a.ApplyAsync(key: "another-key")));
        Assert.Equivalent(first, ReceiptOf(await a.ApplyAsync(key: "from-another", session: otherSession)));
        Assert.IsType<ReconciliationRunCloseOutcome.Closed>(await a.CloseAsync());
        audits++;
        Assert.Equivalent(first, ReceiptOf(await a.ApplyAsync(key: "after-close")));

        Assert.Equal(before, await a.FingerprintAsync());
        Assert.Equal(audits, await s.CountAsync("select count(*) from ops.audit_events"));
    }

    [Fact]
    public async Task TheSameKeyInAnotherUnitIsRefusedWhetherThatUnitIsPendingOrTerminal()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres,
            pick: s => Task.FromResult(new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third }));
        var s = a.S;
        var (alice, bob, third) = (a.Units[s.Seed.Alice], a.Units[s.Seed.Bob], a.Units[s.Seed.Third]);
        ReceiptOf(await a.ApplyAsync(alice, key: "shared"));
        ReceiptOf(await a.ApplyAsync(third, key: "own"));

        Assert.IsType<ReconciliationApplyOutcome.KeyReused>(await a.ApplyAsync(bob, key: "shared"));
        Assert.IsType<ReconciliationApplyOutcome.KeyReused>(await a.ApplyAsync(third, key: "shared"));

        Assert.Equal("Pending", await a.UnitStateAsync(bob));
    }

    [Fact]
    public async Task APendingUnitOfAClosedRunIsRefusedWithoutEffect()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        Assert.IsType<ReconciliationRunCloseOutcome.Closed>(await a.CloseAsync());
        var before = await a.FingerprintAsync();

        Assert.IsType<ReconciliationApplyOutcome.RunClosed>(await a.ApplyAsync());

        await a.AssertNoEffectAsync(before);
    }

    [Fact]
    public async Task ATerminalReceiptDoesNotDependOnTheCutover()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var first = ReceiptOf(await a.ApplyAsync());

        Assert.Equivalent(first, ReceiptOf(await a.ApplyAsync(evidence: Changed(a.S))));
    }

    [Fact]
    public async Task ACutoverDifferentFromTheOneOfTheRunIsRefusedWithTheUnitStillPending()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var before = await a.FingerprintAsync();

        Assert.IsType<ReconciliationApplyOutcome.CutoverChanged>(await a.ApplyAsync(evidence: Changed(a.S)));

        await a.AssertNoEffectAsync(before);
    }

    /// <summary>O código só sabe reclassificar com o seletor atual: um manifesto de outro seletor não se aplica.</summary>
    [Fact]
    public async Task ARunOfAnotherSelectorIsRefusedAsACutoverChange()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, createRun: false);
        var s = a.S;
        var measure = await RegistrationReconciliationPreviewEffectsTests.SuspendBeforePolicyAsync(s, s.Seed.Alice);
        var evidence = a.Evidence!;
        var (run, unit) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await using (var creation = await a.HoldAsync("select 1"))
        {
            await creation.ExecuteAsync("""
                insert into ops.registration_reconciliation_runs(id,selector_version,cutover_evidence_id,cutover_lower,cutover_upper,
                  created_by_user_id,justification,authorization_reference,state,version,created_at,idempotency_key,intent_hash)
                values($1,$2,$3,$4,$5,$6,'Outro seletor','AUTH-2','Open',1,$7,'selector',sha256('s'::bytea))
                """, run, LegacyRegistrationSelector.Version + 1, evidence.Id, evidence.Cutover.Lower, evidence.Cutover.Upper,
                a.ModeratorId, s.Clock.GetUtcNow());
            await creation.ExecuteAsync("""
                insert into ops.registration_reconciliation_units(id,run_id,subject_user_id,original_measure_id,measure_version,items)
                values($1,$2,$3,$4,1,$5::jsonb)
                """, unit, run, s.Seed.Alice, measure,
                $$"""[{"eventId":"{{s.EventId}}","registrationVersion":1,"state":"Confirmed","maxReleasedSeats":1,"maxPromotions":1}]""");
            await creation.CommitAsync();
        }

        var before = await a.FingerprintAsync();

        Assert.IsType<ReconciliationApplyOutcome.CutoverChanged>(await a.ApplyRunAsync(run, unit));

        await a.AssertNoEffectAsync(before, unit);
    }

    [Fact]
    public async Task AnUnknownRunOrAUnitOfAnotherRunIsNotFound()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);

        Assert.IsType<ReconciliationApplyOutcome.NotFound>(await a.ApplyRunAsync(Guid.NewGuid(), a.Unit));
        Assert.IsType<ReconciliationApplyOutcome.NotFound>(await a.ApplyAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task EveryAuditOfTheRunHasACorrelationEvenOutsideATracedRequest()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);

        Assert.IsType<ReconciliationRunCloseOutcome.Closed>(await a.CloseAsync());

        Assert.Equal(1, await a.S.CountAsync("""
            select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_closed'
              and char_length(correlation_id) between 1 and 32
            """));
    }

    [Fact]
    public async Task TheCloseChecksTheAuthorizationInsideItsTransaction()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);

        Assert.IsType<ReconciliationRunCloseOutcome.Forbidden>(await a.CloseAsync(session: a.Session with { SessionId = Guid.NewGuid() }));

        Assert.Equal("Open", await a.S.TextAsync("select state from ops.registration_reconciliation_runs where id=$1", a.Run));
        Assert.Equal(0, await a.S.CountAsync("select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_closed'"));
    }

    [Fact]
    public async Task AUnitOfAnotherRunIsNotFoundEvenWithItsReceipt()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var previous = a.Unit;
        ReceiptOf(await a.ApplyAsync());
        Assert.IsType<ReconciliationRunCloseOutcome.Closed>(await a.CloseAsync());
        await a.CreateRunAsync(s.Seed.Third);

        Assert.IsType<ReconciliationApplyOutcome.NotFound>(await a.ApplyRunAsync(a.Run, previous));
    }

    [Fact]
    public async Task TheFirstAuthorizationPointComesBeforeTheReceiptAndAnyWork()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var before = await a.FingerprintAsync();
        var stranger = a.Session with { SessionId = Guid.NewGuid() };

        Assert.IsType<ReconciliationApplyOutcome.Forbidden>(await a.ApplyAsync(session: stranger));
        await a.AssertNoEffectAsync(before);

        ReceiptOf(await a.ApplyAsync());
        Assert.IsType<ReconciliationApplyOutcome.Forbidden>(await a.ApplyAsync(session: stranger));
        await s.ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1",
            a.Session.SessionId, s.Clock.GetUtcNow() - Duora.Core.Identity.RecentAuthentication.Window - TimeSpan.FromTicks(10));
        Assert.IsType<ReconciliationApplyOutcome.Forbidden>(await a.ApplyAsync());
    }

    private static ReconciliationCutoverEvidence Changed(SuspendedRegistrationScenario s) => ReconciliationCutoverEvidence.Of(
        "synthetic-321-r2", ReconciliationCutover.Between(s.Clock.GetUtcNow().AddMinutes(1), s.Clock.GetUtcNow().AddMinutes(2)));
}
