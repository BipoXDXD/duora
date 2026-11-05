using System.Net;
using Duora.Core.Safety;
using Duora.Infrastructure.Operations;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// ADR-0049, Compliance/Recuperação e Retenção: crash antes do commit, defeitos detectados no passo 11 e a retenção. As
/// falhas vêm de gatilhos criados só neste banco avulso, sem nenhum ponto de injeção no código de produção.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationApplyRecoveryTests(PostgresFixture postgres)
{
    private const long CommitBarrier = 321321;

    private static readonly string[] ProblemKeys = ["code", "status", "title", "traceId", "type"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Com os efeitos já escritos, um gatilho prende a transação no recibo: matar o backend ali é o crash antes do commit.
    /// </summary>
    [Fact]
    public async Task ACrashBeforeTheCommitLeavesNoEffectAndTheUnitPending()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await a.AsMigratorAsync($"""
            create function ops.test_commit_barrier() returns trigger language plpgsql as $f$
            begin perform pg_advisory_xact_lock({CommitBarrier}); return new; end $f$;
            create trigger test_commit_barrier before update on ops.registration_reconciliation_units
              for each row execute function ops.test_commit_barrier();
            """);
        var before = await a.FingerprintAsync();
        await using var hold = await a.HoldAsync($"select pg_advisory_xact_lock({CommitBarrier})");
        var applying = a.ApplyAsync(application: "reconciliation-crash");
        await s.WaitForWaiterAsync("reconciliation-crash", "update ops.registration_reconciliation_units");
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));

        await hold.ExecuteAsync("""
            select pg_terminate_backend(pid) from pg_stat_activity where application_name='reconciliation-crash'
            """);

        await Assert.ThrowsAnyAsync<NpgsqlException>(() => applying);
        await hold.RollbackAsync();
        await a.AssertNoEffectAsync(before);
        await a.AsMigratorAsync("drop trigger test_commit_barrier on ops.registration_reconciliation_units");
        Assert.Equal(ReconciliationUnitState.Applied,
            Assert.IsType<ReconciliationApplyOutcome.Receipt>(await a.ApplyAsync()).Value.State);
    }

    [Fact]
    public async Task ALinkCountThatDivergesRollsBackAndIsAnOpaqueServerError()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await a.AsMigratorAsync("""
            create function ops.test_drop_fact() returns trigger language plpgsql as $f$ begin return null; end $f$;
            create trigger test_drop_fact before insert on ops.outbox_events for each row
              when (new.type='events.waitlisted_registration_promoted') execute function ops.test_drop_fact();
            """);

        await AssertDefectAsync(a);

        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
    }

    [Fact]
    public async Task AnEffectAboveTheCeilingRollsBackAndIsAnOpaqueServerError()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await a.AsMigratorAsync("""
            create function events.test_extra_seat() returns trigger language plpgsql as $f$
            begin update events.events set capacity=capacity+1 where id=new.event_id; return new; end $f$;
            create trigger test_extra_seat after update on events.registrations for each row
              when (new.state='Withdrawn') execute function events.test_extra_seat();
            """);

        await AssertDefectAsync(a);

        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
    }

    /// <summary>
    /// A vaga sai duas vezes do contador e só Third espera: promoções e vínculos batem, e só o teto de liberação recusa.
    /// </summary>
    [Fact]
    public async Task ASeatReleasedTwiceRollsBackAndIsAnOpaqueServerError()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, capacity: 2);
        await CounterTriggerAsync(a, -1);

        await AssertDefectAsync(a);
    }

    /// <summary>O contador sobe junto com a retirada: a vaga prevista não foi liberada, e isso também é defeito.</summary>
    [Fact]
    public async Task ASeatThatWasNotReleasedRollsBackAndIsAnOpaqueServerError()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, capacity: 4);
        await CounterTriggerAsync(a, +1);

        await AssertDefectAsync(a);
    }

    private static Task CounterTriggerAsync(ReconciliationApplyScenario a, int delta) => a.AsMigratorAsync($"""
        create function events.test_shift_counter() returns trigger language plpgsql as $f$
        begin update events.events set confirmed_count=confirmed_count+({delta}) where id=new.event_id; return new; end $f$;
        create trigger test_shift_counter after update on events.registrations for each row
          when (new.state='Withdrawn') execute function events.test_shift_counter();
        """);

    /// <summary>
    /// A retirada do primeiro evento tira a inscrição do segundo por fora: o escritor pula esse item em silêncio, e só o
    /// passo 11 impede um recibo <c>Applied</c> com uma inscrição aprovada que ninguém retirou.
    /// </summary>
    [Fact]
    public async Task AWithdrawalThatSilentlyDidNothingIsADefectAndNotAnAppliedReceipt()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, arrange: async s =>
        {
            var other = s.Seed.Events[1];
            await s.ExecuteAsync("update events.events set capacity=1,starts_at=$2 where id=$1", other, s.Clock.GetUtcNow().AddHours(1));
            await s.Participation.RegisterAsync(other, s.Seed.Alice, false, s.Token);
        });
        await a.AsMigratorAsync("""
            create function events.test_steal_item() returns trigger language plpgsql as $f$
            begin
              update events.registrations set state='Withdrawn',version=version+1
              where user_id=new.user_id and event_id<>new.event_id and state<>'Withdrawn';
              return new;
            end $f$;
            create trigger test_steal_item after update on events.registrations for each row
              when (new.state='Withdrawn' and new.withdrawn_by_suspension_id is not null)
              execute function events.test_steal_item();
            """);

        await AssertDefectAsync(a);
    }

    private static async Task AssertDefectAsync(ReconciliationApplyScenario a)
    {
        var before = await a.FingerprintAsync();

        using var response = await a.PostApplyAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(ProblemKeys, System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain(a.Unit.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await a.FingerprintAsync());
        Assert.Equal("Pending", await a.UnitStateAsync());
        Assert.Equal(0, await a.S.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
        Assert.Contains(a.HostLogs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task TheRetentionDeletesALinkedFactWithItsLinkAndTheReceiptKeepsTheEvidence()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        var fact = Assert.Single(Assert.IsType<ReconciliationApplyOutcome.Receipt>(await a.ApplyAsync()).Value.FactIds);
        await a.AsMigratorAsync($"update ops.outbox_events set occurred_at=occurred_at-interval '8 days' where id='{fact}'");
        var retention = new RetentionService(s.Source, s.Clock);

        var job = await retention.ClaimAsync(Ct);
        Assert.NotNull(job);
        Assert.True(await retention.ExecuteAsync(job, Ct));

        Assert.Equal(0, await s.CountAsync("select count(*) from ops.outbox_events where id=$1", fact));
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
        Assert.Equal(1, await s.CountAsync(
            "select count(*) from ops.registration_reconciliation_units where id=$1 and state='Applied' and receipt_fact_ids=$2",
            a.Unit, new[] { fact }));
    }

    [Fact]
    public async Task ADifferentConfiguredCutoverIsAConflictAtTheRouteAndTheUnitStaysPending()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        await using var changed = new DuoraWebFactory(s.Database.AppConnectionString)
        {
            Time = s.Clock,
            Settings = new Dictionary<string, string?>
            {
                ["RegistrationReconciliation:Cutover:EvidenceId"] = "synthetic-321-r2",
                ["RegistrationReconciliation:Cutover:Lower"] = "2099-01-01T00:00:00Z",
                ["RegistrationReconciliation:Cutover:Upper"] = "2099-01-01T00:01:00Z",
            },
        };
        using var other = await RegistrationReconciliationPreviewEffectsTests.ModeratorAsync(s, changed);
        await using var absent = new DuoraWebFactory(s.Database.AppConnectionString) { Time = s.Clock };
        using var without = await RegistrationReconciliationPreviewEffectsTests.ModeratorAsync(s, absent);
        var before = await a.FingerprintAsync();

        using var different = await a.PostApplyAsync(browser: other);
        using var missing = await a.PostApplyAsync(browser: without);

        var body = await ProblemAssert.IsProblemAsync(different, HttpStatusCode.Conflict, "CUTOVER_CHANGED");
        Assert.Equal(ProblemKeys, body.Select(p => p.Key).Order(StringComparer.Ordinal));
        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.Conflict, "CUTOVER_CHANGED");
        await a.AssertNoEffectAsync(before);
    }
}
