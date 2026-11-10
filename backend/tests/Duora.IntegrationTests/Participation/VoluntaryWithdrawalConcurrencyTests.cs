using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Safety;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-E04 sob concorrência real (T-28, ADR-0053). Uma conexão segura a linha do evento até as duas transações estarem
/// na fila, para o resultado depender da ordem de locks e não de chegadas em momentos diferentes. Sem <c>sleep</c>.
/// </summary>
public sealed class VoluntaryWithdrawalConcurrencyTests(PostgresFixture postgres)
{
    private const string PromotionFacts = "select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'";

    [Fact]
    public async Task TwoSimultaneousWithdrawalsOfTheSamePersonHaveOneEffect()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        var first = s.WithdrawNamedAsync(s.Seed.Alice, "same-first");
        await s.WaitForWaiterAsync("same-first", "events.events");
        var second = s.WithdrawNamedAsync(s.Seed.Alice, "same-second");
        await s.WaitForWaiterAsync("same-second", "safety.user_guards");

        await hold.CommitAsync(s.Token);

        Assert.Equal(ParticipationOutcome.Done, await first);
        Assert.Equal(ParticipationOutcome.Done, await second);
        Assert.Equal("Withdrawn|Confirmed|2", await s.TextAsync("""
            select state||'|'||withdrawn_from_state||'|'||version from events.registrations where event_id=$1 and user_id=$2
            """, s.EventId, s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync(PromotionFacts));
    }

    [Fact]
    public async Task AWithdrawalRacingANewcomerGivesTheSeatToTheWaitlist()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        var withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "seat-withdraw");
        await s.WaitForWaiterAsync("seat-withdraw", "events.events");
        var registering = s.RegisterNamedAsync(s.Seed.Third, "seat-register");
        await s.WaitForWaiterAsync("seat-register", "safety.user_guards");

        await hold.CommitAsync(s.Token);

        Assert.Equal(ParticipationOutcome.Done, await withdrawing);
        Assert.Equal(ParticipationOutcome.Done, await registering);
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        Assert.Equal(2, await s.CountAsync(
            "select waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    /// <summary>Quem commita antes deixa a proveniência; a vaga cai uma vez e a fila é promovida uma vez.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithdrawalAndSuspensionOfTheSamePersonDecrementOnce(bool withdrawalFirst)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        Task<ParticipationOutcome> withdrawing;
        Task<SuspensionOutcome> suspending;
        if (withdrawalFirst)
        {
            withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "both-withdraw");
            await s.WaitForWaiterAsync("both-withdraw", "events.events");
            suspending = s.SuspendAsync(s.Seed.Alice, application: "both-suspend");
            await s.WaitForWaiterAsync("both-suspend", "safety.user_guards");
        }
        else
        {
            suspending = s.SuspendAsync(s.Seed.Alice, application: "both-suspend");
            await s.WaitForWaiterAsync("both-suspend", "events.events");
            withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "both-withdraw");
            await s.WaitForWaiterAsync("both-withdraw", "safety.user_guards");
        }

        await hold.CommitAsync(s.Token);

        Assert.Equal(ParticipationOutcome.Done, await withdrawing);
        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.Equal(withdrawalFirst ? "Withdrawn|true" : "Withdrawn|false", await s.TextAsync("""
            select state||'|'||(withdrawn_by_suspension_id is null) from events.registrations where event_id=$1 and user_id=$2
            """, s.EventId, s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync(PromotionFacts));
    }
}
