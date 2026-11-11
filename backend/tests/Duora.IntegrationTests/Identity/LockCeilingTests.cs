using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// O teto de <c>WithLockCeilingAsync</c> vira <c>lock_timeout</c> em ms inteiros. Abaixo de 1 ms o valor seria
/// <c>0</c>, que no PostgreSQL desliga o teto, então a chamada recusa antes de tocar o banco; com fração de ms, o
/// <c>lock_timeout</c> e a <see cref="AccountBusyException.WaitCeiling"/> levam o mesmo valor arredondado para baixo.
/// </summary>
public sealed class LockCeilingTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task ACeilingBelowOneMillisecondIsRejectedBeforeTakingTheLock(long microseconds)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (security, db) = Services(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var lockTaken = false;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            "ceiling",
            () => security.WithLockCeilingAsync(
                TimeSpan.FromMicroseconds(microseconds),
                () =>
                {
                    lockTaken = true;
                    return Task.FromResult(0);
                },
                TestContext.Current.CancellationToken));

        Assert.False(lockTaken);
    }

    [Fact]
    public async Task AOneMillisecondCeilingIsAccepted()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (security, db) = Services(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var applied = await security.WithLockCeilingAsync(
            TimeSpan.FromMilliseconds(1), () => CurrentLockTimeoutAsync(db), TestContext.Current.CancellationToken);

        Assert.Equal("1ms", applied);
    }

    [Fact]
    public async Task AFractionalCeilingSetsTheLockTimeoutToTheWholeMillisecondsBelow()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (security, db) = Services(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var applied = await security.WithLockCeilingAsync(
            TimeSpan.FromMicroseconds(1500), () => CurrentLockTimeoutAsync(db), TestContext.Current.CancellationToken);

        Assert.Equal("1ms", applied);
    }

    [Fact]
    public async Task AFractionalCeilingReportsTheSameWholeMillisecondsItSet()
    {
        var key = Random.Shared.NextInt64();
        await using var holder = await HoldAdvisoryLockAsync(key);
        await using var scope = _factory.Services.CreateAsyncScope();
        var (security, db) = Services(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var busy = await Assert.ThrowsAsync<AccountBusyException>(
            () => security.WithLockCeilingAsync(
                TimeSpan.FromMicroseconds(1500),
                () => db.Database.ExecuteSqlAsync(
                    $"select pg_advisory_xact_lock({key})", TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        Assert.Equal(AccountBusyReason.AccountLock, busy.Reason);
        Assert.Equal(TimeSpan.FromMilliseconds(1), busy.WaitCeiling);
    }

    private static (AccountSecurityService Security, DuoraDbContext Db) Services(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<AccountSecurityService>(),
            scope.ServiceProvider.GetRequiredService<DuoraDbContext>());

    private static async Task<string> CurrentLockTimeoutAsync(DuoraDbContext db) =>
        (await db.Database
            .SqlQuery<string>($"""select current_setting('lock_timeout') as "Value" """)
            .ToListAsync(TestContext.Current.CancellationToken))
        .Single();

    private async Task<AdvisoryLockHolder> HoldAdvisoryLockAsync(long key)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select pg_advisory_xact_lock($1)", connection, transaction);
        command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new AdvisoryLockHolder(connection, transaction);
    }

    private sealed class AdvisoryLockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
