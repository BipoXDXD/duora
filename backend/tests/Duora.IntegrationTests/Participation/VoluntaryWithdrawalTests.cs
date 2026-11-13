using System.Text.Json;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Safety;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-E04 no SQL de produção (ADR-0053): a retirada grava o histórico, libera a vaga só de quem a tinha, promove a fila
/// na mesma transação e não repete efeito. Os testes de <c>RegistrationBook</c> não passam por este caminho.
/// </summary>
public sealed class VoluntaryWithdrawalTests(PostgresFixture postgres)
{
    private const string PromotionFacts = "select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'";

    [Fact]
    public async Task WithdrawingAConfirmedRegistrationFreesTheSeatAndPromotesTheFirstWaiter()
    {
        await using var s = await QueuedAsync();
        var nextPosition = await NextPositionAsync(s);
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        var now = s.Clock.GetUtcNow();

        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal("Withdrawn|-|Confirmed|-|true|2", await HistoryAsync(s, s.Seed.Alice));
        Assert.Equal(now, await WithdrawnAtAsync(s, s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal(now, await TimestampAsync(s, "confirmed_at", s.Seed.Bob));
        Assert.Equal(2, await PositionAsync(s, s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(nextPosition, await NextPositionAsync(s));
        Assert.Equal(1, await s.CountAsync(PromotionFacts));
    }

    [Fact]
    public async Task ThePromotionFactKeepsItsContract()
    {
        await using var s = await QueuedAsync();

        await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token);

        Assert.Equal("1|event|true", await s.TextAsync("""
            select o.schema_version||'|'||o.resource_type||'|'||(o.resource_version=e.version)
            from ops.outbox_events o join events.events e on e.id=o.resource_id
            where o.type='events.waitlisted_registration_promoted'
            """));
        using var payload = JsonDocument.Parse(await s.TextAsync(
            "select payload::text from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
        Assert.Equal(["eventId", "userId"], payload.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(s.Seed.Bob, payload.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.outbox_deliveries"));
    }

    [Fact]
    public async Task WithdrawingFromTheWaitlistKeepsTheCountAndOtherPositions()
    {
        await using var s = await QueuedAsync();

        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Bob, s.Token));

        Assert.Equal("Withdrawn|-|Waitlisted|1|true|2", await HistoryAsync(s, s.Seed.Bob));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));
        Assert.Equal(2, await PositionAsync(s, s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(0, await s.CountAsync(PromotionFacts));
    }

    [Fact]
    public async Task ATemporarilyIneligibleWaiterKeepsItsPositionAndIsSkipped()
    {
        await using var s = await QueuedAsync();
        await s.ExecuteAsync("update identity.users set email_confirmed=false where id=$1", s.Seed.Bob);

        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
        Assert.Equal(1, await PositionAsync(s, s.Seed.Bob));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    [Fact]
    public async Task ACheckedInWithdrawalKeepsTheCheckInHistoryAndFreesTheSeat()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        Assert.Equal(ParticipationOutcome.Done, await s.RegisterAsync(s.Seed.Alice, checkIn: true));
        var checkedInAt = await TimestampAsync(s, "checked_in_at", s.Seed.Alice);

        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal("Withdrawn|-|CheckedIn|-|true|3", await HistoryAsync(s, s.Seed.Alice));
        Assert.Equal(checkedInAt, await TimestampAsync(s, "checked_in_at", s.Seed.Alice));
        await s.AssertCapacityAsync(0);
    }

    /// <summary>O evento do cenário começa 5 min depois do relógio inicial; o relógio tem resolução de 1 µs.</summary>
    [Theory]
    [InlineData(-1, ParticipationOutcome.Done)]
    [InlineData(0, ParticipationOutcome.Unavailable)]
    public async Task TheWithdrawalWindowClosesExactlyAtTheStart(int microseconds, ParticipationOutcome expected)
    {
        await using var s = await QueuedAsync();
        var before = await SnapshotAsync(s);
        s.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromMicroseconds(microseconds));

        Assert.Equal(expected, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        if (expected == ParticipationOutcome.Unavailable)
        {
            Assert.Equal(before, await SnapshotAsync(s));
        }
        else
        {
            Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        }
    }

    [Fact]
    public async Task RepeatingTheWithdrawalChangesNothing()
    {
        await using var s = await QueuedAsync();
        await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token);
        var after = await SnapshotAsync(s);
        s.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal(after, await SnapshotAsync(s));
    }

    [Fact]
    public async Task WithdrawingAfterASuspensionKeepsTheSuspensionProvenance()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));
        var before = await SnapshotAsync(s);

        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal(before, await SnapshotAsync(s));
        Assert.Equal("Withdrawn|-|Confirmed|-|false|2", await HistoryAsync(s, s.Seed.Alice));
    }

    [Fact]
    public async Task ReRegisteringAfterAVoluntaryWithdrawalIsUnavailable()
    {
        await using var s = await QueuedAsync();
        await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token);
        var after = await SnapshotAsync(s);

        Assert.Equal(ParticipationOutcome.Unavailable, await s.RegisterAsync(s.Seed.Alice));

        Assert.Equal(after, await SnapshotAsync(s));
    }

    [Fact]
    public async Task AnUnregisteredPersonIsUnavailableAndTheRegisteredOneIsUntouched()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        var before = await SnapshotAsync(s);

        Assert.Equal(ParticipationOutcome.Unavailable, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Bob, s.Token));

        Assert.Equal(before, await SnapshotAsync(s));
        Assert.Equal(0, await s.CountAsync("select count(*) from events.registrations where user_id=$1", s.Seed.Bob));
    }

    [Fact]
    public async Task AnUnknownEventIsUnavailable()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);

        Assert.Equal(ParticipationOutcome.Unavailable, await s.Participation.WithdrawAsync(Guid.NewGuid(), s.Seed.Alice, s.Token));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task AClosedEventRefusesAndKeepsTheRegistration(string state)
    {
        await using var s = await QueuedAsync();
        await s.ExecuteAsync("update events.events set state=$2 where id=$1", s.EventId, state);
        var before = await SnapshotAsync(s);

        Assert.Equal(ParticipationOutcome.Unavailable, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal(before, await SnapshotAsync(s));
    }

    [Fact]
    public async Task ACompletedRegistrationIsUnavailableAndKeepsItsHistory()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.ExecuteAsync("""
            insert into events.registrations(event_id,user_id,state,registered_at) values($1,$2,'Completed',$3)
            """, s.EventId, s.Seed.Alice, s.Clock.GetUtcNow());
        var before = await SnapshotAsync(s);

        Assert.Equal(ParticipationOutcome.Unavailable, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));

        Assert.Equal(before, await SnapshotAsync(s));
    }

    /// <summary>Capacidade 1: Alice confirmada, Bob na posição 1 e Third na 2.</summary>
    private async Task<SuspendedRegistrationScenario> QueuedAsync()
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third })
        {
            Assert.Equal(ParticipationOutcome.Done, await s.RegisterAsync(user));
        }
        return s;
    }

    /// <summary><c>state|posição|origem|posição antiga|sem suspensão|versão</c>.</summary>
    private static Task<string> HistoryAsync(SuspendedRegistrationScenario s, Guid user) => s.TextAsync("""
        select state||'|'||coalesce(waitlist_position::text,'-')||'|'||coalesce(withdrawn_from_state,'-')||'|'
          ||coalesce(withdrawn_waitlist_position::text,'-')||'|'||(withdrawn_by_suspension_id is null)||'|'||version
        from events.registrations where event_id=$1 and user_id=$2
        """, s.EventId, user);

    private static Task<DateTimeOffset> WithdrawnAtAsync(SuspendedRegistrationScenario s, Guid user) =>
        TimestampAsync(s, "withdrawn_at", user);

    private static async Task<DateTimeOffset> TimestampAsync(SuspendedRegistrationScenario s, string column, Guid user) =>
        DateTimeOffset.Parse(await s.TextAsync(
            $"select to_char({column} at time zone 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') from events.registrations where event_id=$1 and user_id=$2",
            s.EventId, user), System.Globalization.CultureInfo.InvariantCulture);

    private static Task<long> PositionAsync(SuspendedRegistrationScenario s, Guid user) => s.CountAsync(
        "select waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, user);

    private static Task<long> NextPositionAsync(SuspendedRegistrationScenario s) => s.CountAsync(
        "select next_waitlist_position from events.events where id=$1", s.EventId);

    /// <summary>Tudo que a retirada pode escrever: inscrições, evento, auditoria e outbox.</summary>
    private static Task<string> SnapshotAsync(SuspendedRegistrationScenario s) => s.TextAsync("""
        select jsonb_build_object(
          'event', (select to_jsonb(e) from events.events e where e.id=$1),
          'registrations', (select jsonb_agg(to_jsonb(r) order by r.user_id) from events.registrations r where r.event_id=$1),
          'audit', (select count(*) from ops.audit_events),
          'outbox', (select count(*) from ops.outbox_events))::text
        """, s.EventId);
}
