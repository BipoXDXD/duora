using Duora.Core.Safety;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Participation;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// ADR-0049, Compliance/Corridas: conexões independentes e barreiras por <c>pg_blocking_pids</c>, com a aplicação parada
/// no ponto exato do protocolo. Nenhum caso aceita deadlock nem Busy fora do previsto: quem recebe Busy é só a segunda
/// aplicação da mesma unidade, que é o desfecho do ADR.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationApplyConcurrencyTests(PostgresFixture postgres)
{
    private const string Apply = "reconciliation-apply";

    private static ReconciliationUnitReceipt ReceiptOf(ReconciliationApplyOutcome outcome) =>
        Assert.IsType<ReconciliationApplyOutcome.Receipt>(outcome).Value;

    private static Task<SuspensionOutcome> SuspendNamedAsync(ReconciliationApplyScenario a, Guid user, string application) =>
        a.S.SuspendAsync(user, application: application);

    private static async Task<LiftOutcome> LiftNamedAsync(ReconciliationApplyScenario a, Guid subject, Guid measure, string application)
    {
        // A fixture decide a medida 10 s à frente do relógio, e a revogação não pode antecedê-la.
        a.S.Clock.Advance(TimeSpan.FromSeconds(11));
        var connectionString = new NpgsqlConnectionStringBuilder(a.S.Database.AppConnectionString) { ApplicationName = application }.ConnectionString;
        PostgresFixture.TrackScratchPool(connectionString);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        return await new AccountSuspensions(db, a.S.Clock).LiftAsModeratorAsync(a.Session, subject, measure, 1,
            SuspensionLift.ByModerator(a.ModeratorId, "Revogação sintética."), a.Token);
    }

    [Fact]
    public async Task TwoAppliesOfTheSameUnitLeaveOneEffectAndTheOtherGetsBusyOrTheReceipt()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        var first = a.ApplyAsync(key: "first", application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");

        Assert.IsType<ReconciliationApplyOutcome.Busy>(await a.ApplyAsync(key: "second", application: "reconciliation-second"));

        await hold.CommitAsync();
        var applied = ReceiptOf(await first);
        Assert.Equal(ReconciliationUnitState.Applied, applied.State);
        Assert.Equivalent(applied, ReceiptOf(await a.ApplyAsync(key: "second")));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_applied'"));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='events.registration_promoted'"));
        await s.AssertCapacityAsync(1);
    }

    /// <summary>A revogação pega a guarda primeiro e para na medida; a aplicação espera a guarda e já vê a medida revogada.</summary>
    [Fact]
    public async Task ALiftThatTakesTheSubjectGuardFirstSkipsTheUnit()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var before = await s.TextAsync("select md5(row(r.*)::text) from events.registrations r where event_id=$1 and user_id=$2", s.EventId, s.Seed.Alice);
        await using var hold = await a.HoldAsync("select 1 from safety.moderation_actions where id=$1 for update", a.Measure);
        var lifting = LiftNamedAsync(a, s.Seed.Alice, a.Measure, "reconciliation-lift");
        await s.WaitForWaiterAsync("reconciliation-lift", "safety.moderation_actions");
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "safety.user_guards");

        await hold.CommitAsync();

        Assert.IsType<LiftOutcome.Lifted>(await lifting);
        var receipt = ReceiptOf(await applying);
        Assert.Equal((ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.MeasureChanged), (receipt.State, receipt.Reason));
        Assert.Equal(before, await s.TextAsync("select md5(row(r.*)::text) from events.registrations r where event_id=$1 and user_id=$2", s.EventId, s.Seed.Alice));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
    }

    /// <summary>A aplicação já tem a guarda: a revogação espera, confirma depois e não restaura a inscrição (ADR-0044).</summary>
    [Fact]
    public async Task ALiftThatArrivesAfterTheSubjectGuardWaitsAndRestoresNothing()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");
        var lifting = LiftNamedAsync(a, s.Seed.Alice, a.Measure, "reconciliation-lift");
        await s.WaitForWaiterAsync("reconciliation-lift", "safety.user_guards");

        await hold.CommitAsync();

        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await applying).State);
        Assert.IsType<LiftOutcome.Lifted>(await lifting);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
    }

    public static TheoryData<string> Revocations =>
    [
        "delete from identity.user_roles where user_id=$1 and role_id=(select id from identity.roles where name='moderator') and $2=$2",
        "update identity.login_sessions set revoked_at=now() where id=$2 and $1=$1",
        "update identity.login_sessions set totp_verified_at=null where id=$2 and $1=$1",
    ];

    [Theory]
    [MemberData(nameof(Revocations))]
    public async Task ARevocationBeforeTheSecondAuthorizationPointLeavesNoEffect(string revocation)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var before = await a.FingerprintAsync();
        await using var hold = await a.HoldGuardAsync(s.Seed.Alice);
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "safety.user_guards");
        await s.ExecuteAsync(revocation, a.ModeratorId, a.Session.SessionId);

        await hold.CommitAsync();

        Assert.IsType<ReconciliationApplyOutcome.Forbidden>(await applying);
        await a.AssertNoEffectAsync(before);
    }

    [Theory]
    [MemberData(nameof(Revocations))]
    public async Task ARevocationAfterTheSecondAuthorizationPointLetsTheUnitFinishAndRefusesTheNext(string revocation)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, pick: s => Task.FromResult(new[] { s.Seed.Alice, s.Seed.Third }));
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        var applying = a.ApplyAsync(a.Units[s.Seed.Alice], application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");
        await s.ExecuteAsync(revocation, a.ModeratorId, a.Session.SessionId);

        await hold.CommitAsync();

        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await applying).State);
        Assert.IsType<ReconciliationApplyOutcome.Forbidden>(await a.ApplyAsync(a.Units[s.Seed.Third]));
        Assert.Equal("Pending", await a.UnitStateAsync(a.Units[s.Seed.Third]));
    }

    /// <summary>Revogação e medida nova enquanto a aplicação espera o run: a medida é outra e a retirada não se repete.</summary>
    [Fact]
    public async Task ANewSuspensionOfTheSubjectSkipsTheUnit()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldAsync("select 1 from ops.registration_reconciliation_runs where id=$1 for update", a.Run);
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "ops.registration_reconciliation_runs");
        Assert.IsType<LiftOutcome.Lifted>(await LiftNamedAsync(a, s.Seed.Alice, a.Measure, "reconciliation-lift"));
        Assert.IsType<SuspensionOutcome.Suspended>(await SuspendNamedAsync(a, s.Seed.Alice, "reconciliation-suspend"));

        await hold.CommitAsync();

        var receipt = ReceiptOf(await applying);
        Assert.Equal((ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.MeasureChanged), (receipt.State, receipt.Reason));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='events.registration_withdrawn_by_suspension'"));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='events.registration_promoted'"));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2 and withdrawn_by_suspension_id<>$3
            """, s.EventId, s.Seed.Alice, a.Measure));
        await s.AssertCapacityAsync(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendingTheFirstWaiterSerializesWithTheApplyInEitherOrder(bool applyFirst)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        Task<ReconciliationApplyOutcome> applying;
        Task<SuspensionOutcome> suspending;
        if (applyFirst)
        {
            applying = a.ApplyAsync(application: Apply);
            await s.WaitForWaiterAsync(Apply, "events.events");
            suspending = SuspendNamedAsync(a, s.Seed.Bob, "reconciliation-suspend");
            await s.WaitForWaiterAsync("reconciliation-suspend", "safety.user_guards");
        }
        else
        {
            suspending = SuspendNamedAsync(a, s.Seed.Bob, "reconciliation-suspend");
            await s.WaitForWaiterAsync("reconciliation-suspend", "events.events");
            applying = a.ApplyAsync(application: Apply);
            await s.WaitForWaiterAsync(Apply, "safety.user_guards");
        }

        await hold.CommitAsync();

        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await applying).State);
        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    /// <summary>A inscrição confirma antes: o plano da aplicação ganha um usuário novo e a transação inteira recomeça.</summary>
    [Fact]
    public async Task ANewWaiterRestartsTheTransactionAndTheQueueKeepsItsOrder()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, thirdWaits: false);
        var s = a.S;
        await a.CountAttemptsAsync(s.Seed.Alice);
        await using var hold = await a.HoldEventAsync();
        var registering = s.RegisterNamedAsync(s.Seed.Third, "reconciliation-register");
        await s.WaitForWaiterAsync("reconciliation-register", "events.events");
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "safety.user_guards");

        await hold.CommitAsync();

        Assert.Equal(ParticipationOutcome.Done, await registering);
        var receipt = ReceiptOf(await applying);
        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Equal(2, await a.AttemptsAsync());
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_applied'"));
    }

    /// <summary>
    /// Plano que muda a cada tentativa: um gatilho deste banco avulso cria um novo inscrito ao criar a guarda, sempre depois
    /// da pré-seleção. O ADR manda parar em três tentativas, com 503 e sem efeito.
    /// </summary>
    [Fact]
    public async Task APlanThatKeepsChangingIsBusyAfterThreeAttemptsWithoutEffect()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, thirdWaits: false);
        var s = a.S;
        await a.AsMigratorAsync($$"""
            create function events.test_plan_grows() returns trigger language plpgsql as $f$
            begin
              insert into events.registrations(event_id,user_id,state,waitlist_position,registered_at)
              values ('{{s.EventId}}','{{s.Seed.Third}}','Waitlisted',99,now()) on conflict do nothing;
              return new;
            end $f$;
            create trigger test_plan_grows before insert on safety.user_guards for each row
              when (new.user_id = '{{s.Seed.Bob}}') execute function events.test_plan_grows();
            """);
        await a.CountAttemptsAsync(s.Seed.Alice);
        var before = await a.FingerprintAsync();

        Assert.IsType<ReconciliationApplyOutcome.Busy>(await a.ApplyAsync());

        Assert.Equal(3, await a.AttemptsAsync());
        Assert.Single(a.CoordinatorLogs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && entry.Text.Contains("plano", StringComparison.Ordinal));
        await a.AssertNoEffectAsync(before);
    }

    /// <summary>Check-in de terceiro disputa só o evento e não muda a versão da inscrição do sujeito.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AThirdPartyCheckInSerializesWithTheApply(bool applyFirst)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, capacity: 2, thirdWaits: true);
        var s = a.S;
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, s.Clock.GetUtcNow().AddMinutes(5));
        await using var hold = await a.HoldEventAsync();
        Task<ReconciliationApplyOutcome> applying;
        Task<ParticipationOutcome> checkingIn;
        if (applyFirst)
        {
            applying = a.ApplyAsync(application: Apply);
            await s.WaitForWaiterAsync(Apply, "events.events");
            checkingIn = s.RegisterNamedAsync(s.Seed.Bob, "reconciliation-checkin", checkIn: true);
            await s.WaitForWaiterAsync("reconciliation-checkin", "safety.user_guards");
        }
        else
        {
            checkingIn = s.RegisterNamedAsync(s.Seed.Bob, "reconciliation-checkin", checkIn: true);
            await s.WaitForWaiterAsync("reconciliation-checkin", "events.events");
            applying = a.ApplyAsync(application: Apply);
            await s.WaitForWaiterAsync(Apply, "safety.user_guards");
        }

        await hold.CommitAsync();

        Assert.Equal(ParticipationOutcome.Done, await checkingIn);
        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await applying).State);
        Assert.Equal("CheckedIn", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        await s.AssertCapacityAsync(2);
    }

    /// <summary>
    /// O plano completo tranca também o evento que o manifesto não aprova (ADR-0049, passo 7): a unidade espera quem o
    /// segura, e a inscrição dele continua como estava.
    /// </summary>
    [Fact]
    public async Task AnEventOutsideTheManifestIsLockedBeforeTheWithdrawal()
    {
        var outside = Guid.Empty;
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, arrange: async s =>
        {
            outside = s.Seed.Events[1];
            await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", outside, s.Clock.GetUtcNow().AddHours(1));
            await s.Participation.RegisterAsync(outside, s.Seed.Alice, false, s.Token);
            await s.ExecuteAsync("update events.registrations set registered_at=$3 where event_id=$1 and user_id=$2",
                outside, s.Seed.Alice, s.Clock.GetUtcNow().AddMinutes(30));
        });
        var s = a.S;
        await using var hold = await a.HoldAsync("select 1 from events.events where id=$1 for update", outside);
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");

        Assert.False(applying.IsCompleted);
        await hold.CommitAsync();

        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await applying).State);
        Assert.Equal(1, await s.CountAsync(
            "select count(*) from events.registrations where event_id=$1 and user_id=$2 and state='Confirmed'", outside, s.Seed.Alice));
    }

    /// <summary>Um escritor que muda a inscrição do sujeito sob o lock do evento muda a versão: nada é retirado.</summary>
    [Fact]
    public async Task ACheckInOfTheSubjectCommittedUnderTheEventLockSkipsTheUnit()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        await hold.ExecuteAsync("""
            update events.registrations set state='CheckedIn',checked_in_at=$3,version=version+1 where event_id=$1 and user_id=$2
            """, s.EventId, s.Seed.Alice, s.Clock.GetUtcNow());
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");

        await hold.CommitAsync();

        var receipt = ReceiptOf(await applying);
        Assert.Equal((ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.RegistrationChanged), (receipt.State, receipt.Reason));
        Assert.Equal("CheckedIn", await s.StateAsync(s.Seed.Alice));
    }

    /// <summary>Promoção de outra medida confirma antes: a vaga já foi preenchida e a da unidade vai para o próximo.</summary>
    [Fact]
    public async Task APromotionCommittedFirstKeepsTheCountersAndEachWaiterOnce()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, capacity: 2, arrange: async s =>
        {
            var extra = await BrowserScenarioSeed.InitializeAsync(s.Database.AppConnectionString, s.Clock.GetUtcNow(), s.Token, Guid.NewGuid().ToString("N"));
            await s.RegisterAsync(extra.Alice);
        });
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        var suspending = SuspendNamedAsync(a, s.Seed.Bob, "reconciliation-suspend");
        await s.WaitForWaiterAsync("reconciliation-suspend", "events.events");
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "safety.user_guards");

        await hold.CommitAsync();

        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        var receipt = ReceiptOf(await applying);
        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Single(receipt.FactIds);
        await s.AssertCapacityAsync(2);
        Assert.Equal(2, await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
    }

    /// <summary>O relógio é lido depois dos locks (ADR-0044): atraso esperando o evento não estende o prazo.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task TheClockIsReadAfterTheEventLockAtTheExactStart(long microsecondsBeforeStart, bool promotes)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var startsAt = s.Clock.GetUtcNow().AddMinutes(5);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, startsAt);
        await using var hold = await a.HoldEventAsync();
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");
        s.Clock.SetUtcNow(startsAt.AddTicks(-microsecondsBeforeStart * 10));

        await hold.CommitAsync();

        var receipt = ReceiptOf(await applying);
        Assert.Equal(ReconciliationUnitState.Applied, receipt.State);
        Assert.Equal(promotes ? 1 : 0, receipt.FactIds.Count);
        Assert.Equal(promotes ? "Confirmed" : "Waitlisted", await s.StateAsync(s.Seed.Bob));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task ClosingTheEventUnderItsLockSkipsTheUnit(string state)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        await hold.ExecuteAsync("update events.events set state=$2 where id=$1", s.EventId, state);
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");

        await hold.CommitAsync();

        var receipt = ReceiptOf(await applying);
        Assert.Equal(ReconciliationUnitState.SkippedChanged, receipt.State);
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
    }

    [Fact]
    public async Task TheCloseWaitsForTheUnitInProgressAndTheNextOneSeesItClosed()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, pick: s => Task.FromResult(new[] { s.Seed.Alice, s.Seed.Third }));
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        var applying = a.ApplyAsync(a.Units[s.Seed.Alice], application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");
        var closing = a.CloseAsync("reconciliation-close");
        await s.WaitForWaiterAsync("reconciliation-close", "ops.registration_reconciliation_runs");

        await hold.CommitAsync();

        Assert.Equal(ReconciliationUnitState.Applied, ReceiptOf(await applying).State);
        Assert.IsType<ReconciliationRunCloseOutcome.Closed>(await closing);
        Assert.IsType<ReconciliationApplyOutcome.RunClosed>(await a.ApplyAsync(a.Units[s.Seed.Third]));
        Assert.Equal("Pending", await a.UnitStateAsync(a.Units[s.Seed.Third]));
    }

    /// <summary>O segundo só lê a versão depois de ter o lock: recebe a recusa própria, nunca o erro do gatilho.</summary>
    [Fact]
    public async Task TwoConcurrentClosesLeaveOneClosureAndAPreconditionFailure()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldAsync("select 1 from ops.registration_reconciliation_runs where id=$1 for share", a.Run);
        var first = a.CloseAsync("reconciliation-close-1");
        await s.WaitForWaiterAsync("reconciliation-close-1", "ops.registration_reconciliation_runs");
        var second = a.CloseAsync("reconciliation-close-2");
        await s.WaitForWaiterAsync("reconciliation-close-2", "ops.registration_reconciliation_runs");

        await hold.CommitAsync();

        ReconciliationRunCloseOutcome[] outcomes = [await first, await second];
        Assert.Single(outcomes, outcome => outcome is ReconciliationRunCloseOutcome.Closed);
        Assert.Single(outcomes, outcome => outcome is ReconciliationRunCloseOutcome.PreconditionFailed);
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_closed'"));
    }

    [Fact]
    public async Task ACloseThatOutlastsTheLockTimeoutIsBusyAndTheRunStaysOpen()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        await using var hold = await a.HoldAsync("select 1 from ops.registration_reconciliation_runs where id=$1 for share", a.Run);

        Assert.IsType<ReconciliationRunCloseOutcome.Busy>(await a.CloseAsync());

        await hold.RollbackAsync();
        Assert.Equal("Open", await a.S.TextAsync("select state from ops.registration_reconciliation_runs where id=$1", a.Run));
    }

    /// <summary>Duas unidades, a mesma chave: o índice único decide, e quem perde volta atrás inteiro.</summary>
    [Fact]
    public async Task TheSameKeyRacingOnTwoUnitsAppliesOnlyOne()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, pick: s => Task.FromResult(new[] { s.Seed.Alice, s.Seed.Third }));
        var s = a.S;
        await using var hold = await a.HoldEventAsync();
        var first = a.ApplyAsync(a.Units[s.Seed.Alice], key: "shared", application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");
        var second = a.ApplyAsync(a.Units[s.Seed.Third], key: "shared", application: "reconciliation-second");
        await s.WaitForWaiterAsync("reconciliation-second", "safety.user_guards");

        await hold.CommitAsync();

        ReconciliationApplyOutcome[] outcomes = [await first, await second];
        Assert.Single(outcomes, outcome => outcome is ReconciliationApplyOutcome.Receipt);
        Assert.Single(outcomes, outcome => outcome is ReconciliationApplyOutcome.KeyReused);
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.registration_reconciliation_units where state='Applied'"));
    }

    /// <summary>
    /// Inversão provocada de propósito por um terceiro: a aplicação tem a guarda e quer o evento; o outro tem o evento e quer
    /// a guarda. O PostgreSQL derruba um dos dois; a aplicação não repete e responde Busy, sem efeito.
    /// </summary>
    [Fact]
    public async Task ADeadlockIsBusyWithoutRetryAndWithoutEffect()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await a.CountAttemptsAsync(s.Seed.Alice);
        var before = await a.FingerprintAsync();
        await using var hold = await a.HoldEventAsync();
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "events.events");

        var inverted = hold.ExecuteAsync("select 1 from safety.user_guards where user_id=$1 for update", s.Seed.Alice);
        var outcome = await applying;

        Assert.IsType<ReconciliationApplyOutcome.Busy>(outcome);
        await inverted;
        await hold.RollbackAsync();
        Assert.Equal(1, await a.AttemptsAsync());
        Assert.Single(a.CoordinatorLogs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && entry.Text.Contains("Deadlock", StringComparison.Ordinal));
        await a.AssertNoEffectAsync(before);
    }

    /// <summary>O gatilho prende o <c>:close</c> no UPDATE, já com o run trancado, e quem segura a trava pede o run.</summary>
    [Fact]
    public async Task ADeadlockOnTheCloseIsBusyAndLogged()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await a.AsMigratorAsync("""
            create function ops.test_close_barrier() returns trigger language plpgsql as $f$
            begin perform pg_advisory_xact_lock(321322); return new; end $f$;
            create trigger test_close_barrier before update on ops.registration_reconciliation_runs
              for each row execute function ops.test_close_barrier();
            """);
        await using var hold = await a.HoldAsync("select pg_advisory_xact_lock(321322)");
        var closing = a.PostCloseAsync();
        await s.WaitForWaiterAsync("", "update ops.registration_reconciliation_runs");

        var inverted = hold.ExecuteAsync("select 1 from ops.registration_reconciliation_runs where id=$1 for update", a.Run);
        using var response = await closing;

        await ProblemAssert.IsProblemAsync(response, System.Net.HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        await inverted;
        await hold.RollbackAsync();
        Assert.Single(a.HostLogs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && entry.Text.Contains("Deadlock", StringComparison.Ordinal));
        Assert.Equal("Open", await s.TextAsync("select state from ops.registration_reconciliation_runs where id=$1", a.Run));
    }

    /// <summary>Um escritor sem guarda revoga a medida: o <c>FOR SHARE</c> espera o commit e a releitura já a vê revogada.</summary>
    [Fact]
    public async Task TheOriginalMeasureIsLockedBeforeTheReassessment()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var hold = await a.HoldAsync("""
            update safety.moderation_actions set version=2,lifted_at=decided_at,lifted_by_kind='Automated',lift_reason='Sintética' where id=$1
            """, a.Measure);
        var applying = a.ApplyAsync(application: Apply);
        await s.WaitForWaiterAsync(Apply, "safety.moderation_actions");

        await hold.CommitAsync();

        var receipt = ReceiptOf(await applying);
        Assert.Equal((ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.MeasureChanged), (receipt.State, receipt.Reason));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));
    }
}
