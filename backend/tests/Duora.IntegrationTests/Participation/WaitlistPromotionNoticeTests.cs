using Duora.Infrastructure.Email;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Consumidor do fato <c>events.waitlisted_registration_promoted</c> (issue #320, ADR-0056) no SQL de produção: um fato
/// elegível vira um pedido de e-mail na outbox, uma vez só, depois de revalidar a inscrição; o resto não gera nada e
/// nunca altera inscrição, capacidade ou fila.
/// </summary>
public sealed class WaitlistPromotionNoticeTests(PostgresFixture postgres)
{
    private const string Fact = "events.waitlisted_registration_promoted";

    [Fact]
    public async Task AnEligibleFactSchedulesOneNoticeThatExpiresWhenTheEventStarts()
    {
        await using var s = await PromotedAsync();
        var before = await RegistrationsAsync(s);

        Assert.Equal(1, await Scheduler(s).ScheduleBatchAsync(s.Token));

        Assert.Equal(1, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(0, await NoticesAsync(s, s.Seed.Third));
        Assert.Equal(["Delivered|-"], await MarkersAsync(s));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.outbox_deliveries d join ops.outbox_events e on e.id=d.outbox_event_id
            where e.type='identity.account-email-requested' and d.consumer='transactional-email' and d.status='Pending'
              and d.deadline_at=$1 and d.recipient_key=$2
            """, s.StartsAt, s.Seed.Bob.ToString("D")));
        Assert.Equal(before, await RegistrationsAsync(s));
    }

    [Fact]
    public async Task TheNoticePayloadCarriesNoEventDataAndNoToken()
    {
        await using var s = await PromotedAsync();

        await Scheduler(s).ScheduleBatchAsync(s.Token);

        Assert.Equal("WaitlistPromotionNotice|true|true|true", await s.TextAsync("""
            select payload->>'kind'||'|'||(payload->>'token' is null)||'|'||(payload->>'language' is null)||'|'
              ||(payload::text not like '%'||$1||'%')
            from ops.outbox_events where type='identity.account-email-requested'
            """, s.EventId.ToString("D")));
    }

    [Fact]
    public async Task RunningAgainSchedulesNothingNew()
    {
        await using var s = await PromotedAsync();
        await Scheduler(s).ScheduleBatchAsync(s.Token);

        Assert.Equal(0, await Scheduler(s).ScheduleBatchAsync(s.Token));

        Assert.Equal(1, await NoticesAsync(s, s.Seed.Bob));
        Assert.Single(await MarkersAsync(s));
    }

    [Fact]
    public async Task ConcurrentSchedulersScheduleOneNotice()
    {
        await using var s = await PromotedAsync();
        await using var other = NpgsqlDataSource.Create(s.Database.AppConnectionString);

        var processed = await Task.WhenAll(
            Scheduler(s).ScheduleBatchAsync(s.Token), Scheduler(s, source: other).ScheduleBatchAsync(s.Token));

        Assert.Equal(1, processed.Sum());
        Assert.Equal(1, await NoticesAsync(s, s.Seed.Bob));
        Assert.Single(await MarkersAsync(s));
    }

    [Fact]
    public async Task AWithdrawalBeforeSchedulingSchedulesNothingAndTheNextPromotionStillCounts()
    {
        await using var s = await PromotedAsync();
        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Bob, s.Token));

        Assert.Equal(2, await Scheduler(s).ScheduleBatchAsync(s.Token));

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(1, await NoticesAsync(s, s.Seed.Third));
        Assert.Equal(["Delivered|-", "Expired|RegistrationNotConfirmed"], await MarkersAsync(s));
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Bob));
    }

    [Fact]
    public async Task AWithdrawalCommittedWhileTheSchedulerWaitsIsHonoured()
    {
        await using var s = await PromotedAsync();
        await using var withdrawal = await s.Source.OpenConnectionAsync(s.Token);
        await using var transaction = await withdrawal.BeginTransactionAsync(s.Token);
        await Execute(withdrawal, """
            update events.registrations set withdrawn_from_state=state, state='Withdrawn', withdrawn_at=$3,
              version=version+1
            where event_id=$1 and user_id=$2
            """, s.Token, s.EventId, s.Seed.Bob, s.Clock.GetUtcNow());
        var application = "waitlist-notice-blocked";
        await using var named = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(s.Database.AppConnectionString)
        { ApplicationName = application }.ConnectionString);

        var scheduling = Scheduler(s, source: named).ScheduleBatchAsync(s.Token);
        await s.WaitForWaiterAsync(application, "events.registrations");
        await transaction.CommitAsync(s.Token);

        Assert.Equal(1, await scheduling);
        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(["Expired|RegistrationNotConfirmed"], await MarkersAsync(s));
    }

    [Fact]
    public async Task AnInactiveAccountReceivesNothing()
    {
        await using var s = await PromotedAsync();
        await s.ExecuteAsync("update identity.users set status='Restricted' where id=$1", s.Seed.Bob);

        await Scheduler(s).ScheduleBatchAsync(s.Token);

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(["Expired|AccountNotActive"], await MarkersAsync(s));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
    }

    [Fact]
    public async Task AFactOfAnEventThatAlreadyStartedSchedulesNothing()
    {
        await using var s = await PromotedAsync();
        s.Clock.Advance(s.StartsAt - s.Clock.GetUtcNow());

        await Scheduler(s).ScheduleBatchAsync(s.Token);

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(["Expired|EventStarted"], await MarkersAsync(s));
    }

    [Fact]
    public async Task ACancelledEventSchedulesNothing()
    {
        await using var s = await PromotedAsync();
        await s.ExecuteAsync("update events.events set state='Cancelled' where id=$1", s.EventId);

        await Scheduler(s).ScheduleBatchAsync(s.Token);

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(["Expired|EventNotOpen"], await MarkersAsync(s));
    }

    [Fact]
    public async Task WhenDisabledNothingIsReadOrWritten()
    {
        await using var s = await PromotedAsync();

        Assert.Equal(0, await Scheduler(s, enabled: false).ScheduleBatchAsync(s.Token));

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Empty(await MarkersAsync(s));
    }

    [Fact]
    public async Task FactsRecordedBeforeTheWatermarkAreNeverProcessed()
    {
        await using var s = await PromotedAsync();

        Assert.Equal(0, await Scheduler(s, notBefore: s.Clock.GetUtcNow().AddTicks(10)).ScheduleBatchAsync(s.Token));

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Empty(await MarkersAsync(s));
    }

    [Fact]
    public async Task FactsOlderThanTheMaximumAgeAreNeverProcessed()
    {
        await using var s = await PromotedAsync();
        await s.ExecuteAsync("update events.events set starts_at=starts_at+interval '3 days' where id=$1", s.EventId);
        s.Clock.Advance(WaitlistPromotionNoticeScheduler.MaxFactAge + TimeSpan.FromSeconds(1));

        Assert.Equal(0, await Scheduler(s).ScheduleBatchAsync(s.Token));

        Assert.Empty(await MarkersAsync(s));
    }

    [Theory]
    [InlineData(1, """{"eventId":"00000000-0000-0000-0000-000000000001","userId":"{user}"}""")]
    [InlineData(1, """{"eventId":"{event}","userId":"{user}","extra":1}""")]
    [InlineData(1, """{"eventId":"{event}"}""")]
    [InlineData(1, """{"eventId":"{event}","userId":"not-a-guid"}""")]
    [InlineData(2, """{"eventId":"{event}","userId":"{user}"}""")]
    public async Task AFactOutsideTheV1ContractGoesToReviewWithoutANotice(int schemaVersion, string payload)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Bob);
        await s.ExecuteAsync($"""
            insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,resource_version,payload,occurred_at)
            values ($1,'{Fact}',$2,'event',$3,1,$4::jsonb,$5)
            """, Guid.CreateVersion7(), schemaVersion, s.EventId,
            payload.Replace("{event}", s.EventId.ToString("D"), StringComparison.Ordinal)
                .Replace("{user}", s.Seed.Bob.ToString("D"), StringComparison.Ordinal),
            s.Clock.GetUtcNow());

        Assert.Equal(1, await Scheduler(s).ScheduleBatchAsync(s.Token));

        Assert.Equal(0, await NoticesAsync(s, s.Seed.Bob));
        Assert.Equal(["Failed|InvalidContract"], await MarkersAsync(s));
    }

    /// <summary>Capacidade 1, três inscritos; a retirada de Alice promove Bob e grava um fato.</summary>
    private async Task<SuspendedRegistrationScenario> PromotedAsync()
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third })
        {
            Assert.Equal(ParticipationOutcome.Done, await s.RegisterAsync(user));
        }
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ParticipationOutcome.Done, await s.Participation.WithdrawAsync(s.EventId, s.Seed.Alice, s.Token));
        Assert.Equal(1, await s.CountAsync($"select count(*) from ops.outbox_events where type='{Fact}'"));
        return s;
    }

    private static WaitlistPromotionNoticeScheduler Scheduler(SuspendedRegistrationScenario s, bool enabled = true,
        DateTimeOffset? notBefore = null, NpgsqlDataSource? source = null) =>
        new(source ?? s.Source, s.Clock, new WaitlistPromotionNoticeOptions
        {
            Enabled = enabled,
            NotBefore = notBefore ?? s.StartsAt.AddMinutes(-5),
        }, NullLogger<WaitlistPromotionNoticeScheduler>.Instance);

    private static Task<long> NoticesAsync(SuspendedRegistrationScenario s, Guid user) => s.CountAsync("""
        select count(*) from ops.outbox_events
        where type='identity.account-email-requested' and resource_id=$1 and payload->>'kind'='WaitlistPromotionNotice'
        """, user);

    private static async Task<string[]> MarkersAsync(SuspendedRegistrationScenario s)
    {
        await using var connection = await s.Source.OpenConnectionAsync(s.Token);
        await using var query = Command(connection, """
            select status||'|'||coalesce(last_error,'-') from ops.outbox_deliveries
            where consumer='waitlist-promotion-notice' order by 1
            """);
        await using var reader = await query.ExecuteReaderAsync(s.Token);
        List<string> markers = [];
        while (await reader.ReadAsync(s.Token))
        {
            markers.Add(reader.GetString(0));
        }
        return [.. markers];
    }

    private static Task<string> RegistrationsAsync(SuspendedRegistrationScenario s) => s.TextAsync("""
        select string_agg(user_id||':'||state||':'||coalesce(waitlist_position::text,'-')||':'||version, ',' order by user_id)
          ||'|'||(select confirmed_count||':'||version from events.events where id=$1)
        from events.registrations where event_id=$1
        """, s.EventId);
}
