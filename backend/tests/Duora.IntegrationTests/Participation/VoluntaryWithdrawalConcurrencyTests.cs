using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Safety;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>
    /// O lock do evento é a única proteção aqui: o build da rodada só guarda quem fez check-in, e Alice está só
    /// <c>Confirmed</c>, então as duas transações não dividem nenhuma guarda. O build para com o evento trancado, antes
    /// de passá-lo a <c>Running</c>; a retirada, um instante antes do início, precisa esperar e então ver o evento
    /// em andamento. Sem o lock, ela leria <c>Published</c>, retiraria e só esperaria no contador, depois do início.
    /// </summary>
    [Fact]
    public async Task AWithdrawalRacingTheRoundBuildSeesTheEventRunningAndChangesNothing()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres, capacity: 3);
        await s.RegisterAsync(s.Seed.Alice);
        foreach (var user in new[] { s.Seed.Bob, s.Seed.Third })
        {
            await s.RegisterAsync(user);
            Assert.Equal(ParticipationOutcome.Done, await s.RegisterAsync(user, checkIn: true));
        }
        s.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMicroseconds(1));
        var builder = new ParticipationService(s.Source, new FakeTimeProvider(s.StartsAt));
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.ObserveMatchingStage = async (stage, ct) =>
        {
            if (stage == "PairingsWritten")
            {
                locked.SetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var building = builder.BuildRoundPairingsAsync(s.Seed.Rounds[0], s.Token);
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(10), s.Token);
        var withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "build-withdraw");
        await s.WaitForWaiterAsync("build-withdraw", "events.events");

        release.SetResult();
        await building;

        Assert.Equal(ParticipationOutcome.Unavailable, await withdrawing);
        Assert.Equal("Running", await s.TextAsync("select state from events.events where id=$1", s.EventId));
        Assert.Equal("Confirmed|1", await s.TextAsync("""
            select state||'|'||version from events.registrations where event_id=$1 and user_id=$2
            """, s.EventId, s.Seed.Alice));
        await s.AssertCapacityAsync(3);
    }

    /// <summary>
    /// T-28 (<c>concurrency.md</c>, C5): duas pessoas na última vaga e depois uma retirada concorrente com o check-in
    /// de quem ficou com a vaga. A lotação nunca passa da capacidade e a fila é promovida pela menor posição.
    /// </summary>
    [Fact]
    public async Task TheLastSeatRaceAndAConcurrentWithdrawalKeepCapacity()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres, capacity: 2);
        var extra = await BrowserScenarioSeed.InitializeAsync(s.Database.AppConnectionString, s.Clock.GetUtcNow(),
            s.Token, Guid.NewGuid().ToString("N"));
        await s.RegisterAsync(s.Seed.Alice);
        await using (var blocker = await s.Source.OpenConnectionAsync(s.Token))
        await using (var hold = await blocker.BeginTransactionAsync(s.Token))
        {
            await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
            var bob = s.RegisterNamedAsync(s.Seed.Bob, "last-bob");
            await s.WaitForWaiterAsync("last-bob", "events.events");
            var third = s.RegisterNamedAsync(s.Seed.Third, "last-third");
            await s.WaitForWaiterAsync("last-third", "events.events");
            await hold.CommitAsync(s.Token);
            Assert.Equal(ParticipationOutcome.Done, await bob);
            Assert.Equal(ParticipationOutcome.Done, await third);
        }
        await s.AssertCapacityAsync(2);
        await s.RegisterAsync(extra.Alice);
        var seated = await s.StateAsync(s.Seed.Bob) == "Confirmed" ? s.Seed.Bob : s.Seed.Third;
        var waiting = seated == s.Seed.Bob ? s.Seed.Third : s.Seed.Bob;
        Assert.Equal(1, await s.CountAsync(
            "select waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, waiting));

        await using (var blocker = await s.Source.OpenConnectionAsync(s.Token))
        await using (var hold = await blocker.BeginTransactionAsync(s.Token))
        {
            await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
            var withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "last-withdraw");
            await s.WaitForWaiterAsync("last-withdraw", "events.events");
            var checkingIn = s.RegisterNamedAsync(seated, "last-checkin", checkIn: true);
            await s.WaitForWaiterAsync("last-checkin", "safety.user_guards");
            await hold.CommitAsync(s.Token);
            Assert.Equal(ParticipationOutcome.Done, await withdrawing);
            Assert.Equal(ParticipationOutcome.Done, await checkingIn);
        }

        Assert.Equal("CheckedIn", await s.StateAsync(seated));
        Assert.Equal("Confirmed", await s.StateAsync(waiting));
        Assert.Equal("Waitlisted", await s.StateAsync(extra.Alice));
        await s.AssertCapacityAsync(2);
        Assert.Equal(1, await s.CountAsync(PromotionFacts));
    }

    /// <summary>Check-in e retirada da mesma pessoa serializam: a vaga cai uma vez e o estado de origem é o de quem veio antes.</summary>
    [Theory]
    [InlineData(true, "Withdrawn|CheckedIn|true", ParticipationOutcome.Done)]
    [InlineData(false, "Withdrawn|Confirmed|false", ParticipationOutcome.Unavailable)]
    public async Task WithdrawalAndCheckInSerializeInEitherOrder(bool checkInFirst, string expected, ParticipationOutcome checkIn)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        Task<ParticipationOutcome> withdrawing;
        Task<ParticipationOutcome> checkingIn;
        if (checkInFirst)
        {
            checkingIn = s.RegisterNamedAsync(s.Seed.Alice, "order-checkin", checkIn: true);
            await s.WaitForWaiterAsync("order-checkin", "events.events");
            withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "order-withdraw");
            await s.WaitForWaiterAsync("order-withdraw", "safety.user_guards");
        }
        else
        {
            withdrawing = s.WithdrawNamedAsync(s.Seed.Alice, "order-withdraw");
            await s.WaitForWaiterAsync("order-withdraw", "events.events");
            checkingIn = s.RegisterNamedAsync(s.Seed.Alice, "order-checkin", checkIn: true);
            await s.WaitForWaiterAsync("order-checkin", "safety.user_guards");
        }

        await hold.CommitAsync(s.Token);

        Assert.Equal(ParticipationOutcome.Done, await withdrawing);
        Assert.Equal(checkIn, await checkingIn);
        Assert.Equal(expected, await s.TextAsync("""
            select state||'|'||withdrawn_from_state||'|'||(checked_in_at is not null)
            from events.registrations where event_id=$1 and user_id=$2
            """, s.EventId, s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync(PromotionFacts));
    }
}
