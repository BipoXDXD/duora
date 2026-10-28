using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Decisão do dono em 28/10/2026 (issue #93): o resultado mínimo da selfie é guardado por 30 dias e depois apagado,
/// inclusive o Passed. A limpeza roda com a integração desligada, como a remoção de 24h já existente.
/// </summary>
public sealed class SelfieRetentionTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    [Theory]
    [InlineData("Passed")]
    [InlineData("RecaptureRequired")]
    [InlineData("Inconclusive")]
    [InlineData("Expired")]
    public async Task ResultIsDeletedWhenItReachesThirtyDaysAfterCompletion(string state)
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        var account = await scenario.CreateConfirmedAccountAsync();
        var onPoint = await InsertCompletedAsync(scenario, account.UserId, state, Now - Retention);

        await dispatcher.DispatchBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAsync(scenario, onPoint));
    }

    [Theory]
    [InlineData("Passed")]
    [InlineData("Inconclusive")]
    public async Task ResultYoungerThanThirtyDaysIsKept(string state)
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        var account = await scenario.CreateConfirmedAccountAsync();
        var offPoint = await InsertCompletedAsync(scenario, account.UserId, state, Now - Retention + OneMillisecond);

        await dispatcher.DispatchBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountAsync(scenario, offPoint));
    }

    [Fact]
    public async Task AttemptThatNeverCompletedCountsFromItsExpiry()
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        var account = await scenario.CreateConfirmedAccountAsync();
        var old = await InsertNeverCompletedAsync(scenario, account.UserId, Now - Retention);
        var recent = await InsertNeverCompletedAsync(scenario, account.UserId, Now - Retention + OneMillisecond);

        await dispatcher.DispatchBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAsync(scenario, old));
        Assert.Equal(1, await CountAsync(scenario, recent));
    }

    [Fact]
    public async Task DeletingAnExpiredPassedResultEndsEligibilityOnlyForItsOwner()
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        var expired = await scenario.CreateConfirmedAccountAsync();
        var current = await scenario.CreateConfirmedAccountAsync();
        await scenario.ScalarAsync("update identity.users set status = 'Active' where id = any($1) returning 1",
            new[] { expired.UserId, current.UserId });
        await InsertCompletedAsync(scenario, expired.UserId, "Passed", Now - Retention);
        var kept = await InsertCompletedAsync(scenario, current.UserId, "Passed", Now - TimeSpan.FromDays(1));
        var verifications = scope.ServiceProvider.GetRequiredService<SelfieVerificationService>();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await verifications.GetEligibilityAsync(expired.UserId, ct));

        await dispatcher.DispatchBatchAsync(ct);

        Assert.False(await verifications.GetEligibilityAsync(expired.UserId, ct));
        Assert.Equal("NotStarted", (await verifications.GetCurrentAsync(expired.UserId, ct)).State);
        Assert.True(await verifications.GetEligibilityAsync(current.UserId, ct));
        Assert.Equal(1, await CountAsync(scenario, kept));
    }

    private async Task<(IdentityScenario, DuoraWebFactory)> StartAsync()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        var factory = new DuoraWebFactory(connection)
        {
            Time = new FakeTimeProvider(Now),
            Settings = new Dictionary<string, string?> { ["SelfieVerification:Enabled"] = "false" },
        };
        return (new IdentityScenario(connection, factory), factory);
    }

    private static async Task<Guid> InsertCompletedAsync(IdentityScenario scenario, Guid userId, string state, DateTimeOffset completedAt)
    {
        var id = Guid.NewGuid();
        await scenario.ScalarAsync("""
            insert into identity.selfie_verifications
            (id, user_id, state, policy_version, requested_at, expires_at, completed_at, version)
            values ($1, $2, $3, 'synthetic-policy', $4 - interval '1 minute', $4 + interval '2 minutes', $4, 2) returning 1
            """, id, userId, state, completedAt);
        return id;
    }

    private static async Task<Guid> InsertNeverCompletedAsync(IdentityScenario scenario, Guid userId, DateTimeOffset expiresAt)
    {
        var id = Guid.NewGuid();
        await scenario.ScalarAsync("""
            insert into identity.selfie_verifications
            (id, user_id, state, policy_version, requested_at, expires_at, completed_at, version)
            values ($1, $2, 'Expired', 'synthetic-policy', $3 - interval '3 minutes', $3, null, 1) returning 1
            """, id, userId, expiresAt);
        return id;
    }

    private static Task<long> CountAsync(IdentityScenario scenario, Guid id)
        => scenario.ScalarAsync("select count(*) from identity.selfie_verifications where id = $1", id);
}
