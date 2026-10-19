using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Profiles;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class ParticipationGuardTests(PostgresFixture postgres)
{
    private static readonly string[] Portuguese = ["pt"];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreferenceOrLocaleCommitBeforeGuardsIsObservedAndAfterGuardsIsForFutureRounds(bool afterGuards, bool locale)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        if (locale)
        {
            await using var c = await source.OpenConnectionAsync(ct);
            await Execute(c, "update profiles.preferences set allow_different_display_locales=false where user_id=$1", ct, seed.Alice);
        }
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await service.RegisterAsync(seed.Events[0], user, false, ct);
            await service.RegisterAsync(seed.Events[0], user, true, ct);
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ObserveMatchingStage = async (stage, token) =>
        {
            if (stage == (afterGuards ? "CandidatesGuarded" : "CandidatesRead"))
            {
                reached.SetResult();
                await resume.Task.WaitAsync(token);
            }
        };
        clock.Advance(TimeSpan.FromMinutes(5));
        var build = service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        await reached.Task.WaitAsync(ct);
        Task update = locale
            ? new LocaleStore(source).UpdateOwnAsync(seed.Bob, 0, "pt-BR", ct)
            : new PreferenceStore(source, clock).UpdateOwnAsync(seed.Bob, 1, Portuguese, true, ct);
        if (afterGuards)
        {
            await WaitForGuardWaiter(source, ct);
            Assert.False(update.IsCompleted);
            resume.SetResult();
            await Task.WhenAll(build, update);
            var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId;
            Assert.NotNull(room);
            var projection = (await service.RoomAsync(room.Value, seed.Bob, ct))!;
            Assert.Equal("en", projection.Language);
            Assert.Equal("en-US", projection.Locale);
        }
        else
        {
            await update;
            resume.SetResult();
            await build;
            Assert.Null((await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId);
        }
    }

    [Fact]
    public async Task FailureAfterReservationsRollsBackAndOverlappingEventsReserveOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await Execute(c, "update events.events set starts_at=$2 where id=$1", ct, seed.Events[1], now.AddMinutes(5));
            await Execute(c, "update events.rounds set starts_at=$2,deadline_at=$3 where id=$1", ct, seed.Rounds[1], now.AddMinutes(5), now.AddMinutes(17));
        }
        foreach (var eventId in seed.Events.Take(2))
        {
            foreach (var user in new[] { seed.Alice, seed.Bob })
            {
                await service.RegisterAsync(eventId, user, false, ct);
                await service.RegisterAsync(eventId, user, true, ct);
            }
        }
        service.ObserveMatchingStage = (stage, _) => stage == "PairingsWritten"
            ? Task.FromException(new InvalidOperationException("Synthetic transaction failure")) : Task.CompletedTask;
        clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildRoundPairingsAsync(seed.Rounds[0], ct));
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var q = Command(c, "select count(*) from events.pairings where event_id=$1", seed.Events[0]))
        {
            Assert.Equal(0L, await q.ExecuteScalarAsync(ct));
        }
        service.ObserveMatchingStage = null;
        await Task.WhenAll(service.BuildRoundPairingsAsync(seed.Rounds[0], ct), service.BuildRoundPairingsAsync(seed.Rounds[1], ct));
        var statuses = await Task.WhenAll(seed.Events.Take(2).Select(e => service.StatusAsync(e, seed.Alice, ct)));
        Assert.Single(statuses, s => s!.RoomId.HasValue);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select count(*) from events.pairing_participants where user_id=$1 and released_at is null", seed.Alice);
        Assert.Equal(1L, await query.ExecuteScalarAsync(ct));
    }

    private static async Task WaitForGuardWaiter(NpgsqlDataSource source, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using var c = await source.OpenConnectionAsync(ct);
        while (true)
        {
            await using var q = Command(c, "select exists(select 1 from pg_stat_activity where datname=current_database() and wait_event_type='Lock' and query like '%user_guards%')");
            if ((bool)(await q.ExecuteScalarAsync(linked.Token))!)
            {
                return;
            }

            await Task.Delay(10, linked.Token);
        }
    }
}
