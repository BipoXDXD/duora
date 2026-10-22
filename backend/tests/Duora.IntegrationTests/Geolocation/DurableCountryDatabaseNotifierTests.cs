using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Geolocation;

public sealed class DurableCountryDatabaseNotifierTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcceptedDeliveryIsNotResentAfterNotifierAndConnectionPoolRestart()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var sent = 0;
        var transport = new Transport((_, _) => { Interlocked.Increment(ref sent); return Task.CompletedTask; });
        CountryDatabaseNotification notice;
        await using (var source = NpgsqlDataSource.Create(connection))
        {
            notice = await Seed(source);
            await Notifier(source, transport).SendAsync(notice, Ct);
        }
        await using var restarted = NpgsqlDataSource.Create(connection);
        await Notifier(restarted, transport).SendAsync(notice, Ct);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task AmbiguousSendAfterAcceptanceNeverAutomaticallyResends()
    {
        await using var source = NpgsqlDataSource.Create(await postgres.CreateMigratedDatabaseAsync(Ct));
        var notice = await Seed(source);
        var sent = 0;
        var transport = new Transport((_, _) =>
        {
            Interlocked.Increment(ref sent);
            throw new IOException("synthetic acceptance followed by lost transport response");
        });
        await Assert.ThrowsAsync<IOException>(() => Notifier(source, transport).SendAsync(notice, Ct));
        await Assert.ThrowsAsync<CountryDatabaseDeliveryUncertainException>(() => Notifier(source, transport).SendAsync(notice, Ct));
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task ConcurrentCallsReserveOnlyOneSmtpAttempt()
    {
        await using var source = NpgsqlDataSource.Create(await postgres.CreateMigratedDatabaseAsync(Ct));
        var notice = await Seed(source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        var transport = new Transport(async (_, ct) =>
        {
            Interlocked.Increment(ref sent);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        var first = Notifier(source, transport).SendAsync(notice, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        { await Assert.ThrowsAsync<CountryDatabaseDeliveryUncertainException>(() => Notifier(source, transport).SendAsync(notice, Ct)); }
        finally { release.TrySetResult(); await first; }
        await Notifier(source, transport).SendAsync(notice, Ct);
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData("ops.country_database_deliveries")]
    [InlineData("ops.country_database_notifications")]
    public async Task AppCannotEraseTheJournalDirectlyOrThroughItsParent(string table)
    {
        await using var source = NpgsqlDataSource.Create(await postgres.CreateMigratedDatabaseAsync(Ct));
        var notice = await Seed(source);
        await Notifier(source, new Transport((_, _) => Task.CompletedTask)).SendAsync(notice, Ct);
        await using var c = await source.OpenConnectionAsync(Ct);
        await using var q = new NpgsqlCommand($"delete from {table}", c);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => q.ExecuteNonQueryAsync(Ct))).SqlState);
    }

    private static DurableCountryDatabaseNotifier Notifier(NpgsqlDataSource source, ICountryDatabaseMailTransport transport) =>
        new(source, transport, new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<DurableCountryDatabaseNotifier>.Instance);

    private static async Task<CountryDatabaseNotification> Seed(NpgsqlDataSource source)
    {
        await using var c = await source.OpenConnectionAsync(Ct);
        await using var q = new NpgsqlCommand("""
            insert into ops.country_database_notifications(code,severity,occurred_at,available_at)
            values('Age',1,now(),now()) returning id,occurred_at
            """, c);
        await using var r = await q.ExecuteReaderAsync(Ct);
        Assert.True(await r.ReadAsync(Ct));
        return new(r.GetInt64(0), "Age", 1, false, r.GetFieldValue<DateTimeOffset>(1));
    }

    private sealed class Transport(Func<CountryDatabaseNotification, CancellationToken, Task> send) : ICountryDatabaseMailTransport
    {
        public Task SendAsync(CountryDatabaseNotification notification, CancellationToken ct) => send(notification, ct);
    }
}
