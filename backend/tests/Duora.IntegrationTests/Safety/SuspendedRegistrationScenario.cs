using Duora.Core.Safety;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Participation;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Safety;

internal sealed class SuspendedRegistrationScenario(ScratchDatabase database, ParticipationSeed seed,
    FakeTimeProvider clock, NpgsqlDataSource source) : IAsyncDisposable
{
    public ScratchDatabase Database => database;
    public ParticipationSeed Seed => seed;
    public FakeTimeProvider Clock => clock;
    public NpgsqlDataSource Source => source;
    public Guid EventId => seed.Events[0];
    public DateTimeOffset StartsAt { get; } = clock.GetUtcNow().AddMinutes(5);
    public ParticipationService Participation => new(source, clock);
    public CancellationToken Token { get; } = TestContext.Current.CancellationToken;

    public static async Task<SuspendedRegistrationScenario> CreateAsync(PostgresFixture postgres, int capacity = 1)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateMigratedScratchDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-now.Ticks % 10);
        var seed = await BrowserScenarioSeed.InitializeAsync(database.AppConnectionString, now, ct,
            Guid.NewGuid().ToString("N"), declarePreferences: true);
        var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var scenario = new SuspendedRegistrationScenario(database, seed, new FakeTimeProvider(now), source);
        await scenario.ExecuteAsync("update events.events set capacity=$2 where id=$1", scenario.EventId, capacity);
        return scenario;
    }

    public Task<ParticipationOutcome> RegisterAsync(Guid user, bool checkIn = false) =>
        Participation.RegisterAsync(EventId, user, checkIn, Token);

    public async Task<ParticipationOutcome> RegisterNamedAsync(Guid user, string application, bool checkIn = false)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        { ApplicationName = application }.ConnectionString;
        await using var named = NpgsqlDataSource.Create(connectionString);
        return await new ParticipationService(named, clock).RegisterAsync(EventId, user, checkIn, Token);
    }

    public async Task WaitForWaiterAsync(string application, string queryPart)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = await source.OpenConnectionAsync(timeout.Token);
        while (true)
        {
            await using var query = Command(connection, """
                select exists(select 1 from pg_stat_activity where application_name=$1
                  and cardinality(pg_blocking_pids(pid))>0 and query like '%'||$2||'%')
                """, application, queryPart);
            if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    public async Task<SuspensionOutcome> SuspendAsync(Guid user, Guid? commandId = null, string? application = null)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        { ApplicationName = application ?? "suspended-registration-test" }.ConnectionString;
        PostgresFixture.TrackScratchPool(connectionString);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        return await new AccountSuspensions(db, clock).SuspendAutomaticallyAsync(
            SuspensionOrder.Automated(user, SuspensionReason.SelfieVerificationFailed, "Synthetic suspension."),
            ModerationCommandKey.ForSystem("selfie-verification", commandId ?? Guid.NewGuid()), Token);
    }

    public Task<string> StateAsync(Guid user) => TextAsync(
        "select state from events.registrations where event_id=$1 and user_id=$2", EventId, user);

    public async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync(Token);
        await Execute(connection, sql, Token, values);
    }

    public async Task<long> CountAsync(string sql, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = Command(connection, sql, values);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string> TextAsync(string sql, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var command = Command(connection, sql, values);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    public async Task AssertCapacityAsync(int expected)
    {
        Assert.Equal(expected, await CountAsync("select confirmed_count from events.events where id=$1", EventId));
        Assert.Equal(expected, await CountAsync(
            "select count(*) from events.registrations where event_id=$1 and state in ('Confirmed','CheckedIn')", EventId));
    }

    public async ValueTask DisposeAsync()
    {
        await source.DisposeAsync();
        await database.DisposeAsync();
    }
}
