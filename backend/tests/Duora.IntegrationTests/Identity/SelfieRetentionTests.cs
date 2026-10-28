using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Decisões do dono em 28/10/2026 (issue #93): "a selfie fica guardada mesmo após 30 dias e a pessoa não perde
/// participação". O Passed (estado, política e datas, sem imagem) fica enquanto a conta existir; tentativa não aprovada
/// sai 30 dias depois; a exclusão da conta leva tudo. A limpeza roda com a integração desligada.
/// </summary>
public sealed class SelfieRetentionTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    [Theory]
    [InlineData("RecaptureRequired")]
    [InlineData("Inconclusive")]
    [InlineData("Expired")]
    public async Task UnapprovedAttemptIsDeletedWhenItReachesThirtyDaysAfterCompletion(string state)
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        var account = await scenario.CreateConfirmedAccountAsync();
        var onPoint = await InsertCompletedAsync(scenario, account.UserId, state, Now - Retention);
        var offPoint = await InsertCompletedAsync(scenario, account.UserId, state, Now - Retention + OneMillisecond);

        await dispatcher.DispatchBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAsync(scenario, onPoint));
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
    public async Task PassedOlderThanThirtyDaysStaysAndKeepsParticipation()
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        var account = await scenario.CreateConfirmedAccountAsync();
        await scenario.ScalarAsync("update identity.users set status = 'Active' where id = $1 returning 1", account.UserId);
        var passed = await InsertCompletedAsync(scenario, account.UserId, "Passed", Now - TimeSpan.FromDays(400));
        var failedLater = await InsertCompletedAsync(scenario, account.UserId, "Inconclusive", Now - TimeSpan.FromDays(31));
        var verifications = scope.ServiceProvider.GetRequiredService<SelfieVerificationService>();
        var ct = TestContext.Current.CancellationToken;

        await dispatcher.DispatchBatchAsync(ct);

        Assert.Equal(1, await CountAsync(scenario, passed));
        Assert.Equal(0, await CountAsync(scenario, failedLater));
        Assert.Equal("synthetic-policy", await scenario.ScalarTextAsync(
            "select policy_version from identity.selfie_verifications where id = $1", passed));
        Assert.True(await verifications.GetEligibilityAsync(account.UserId, ct));
        Assert.Equal("Passed", (await verifications.GetCurrentAsync(account.UserId, ct)).State);
    }

    [Fact]
    public async Task DeletingTheAccountDeletesItsPassedResult()
    {
        var (scenario, factory) = await StartAsync();
        await using var _ = factory;
        var deleted = await scenario.CreateConfirmedAccountAsync();
        var kept = await scenario.CreateConfirmedAccountAsync();
        var deletedPassed = await InsertCompletedAsync(scenario, deleted.UserId, "Passed", Now - TimeSpan.FromDays(1));
        var keptPassed = await InsertCompletedAsync(scenario, kept.UserId, "Passed", Now - TimeSpan.FromDays(1));

        await scenario.ScalarAsync("delete from identity.users where id = $1 returning 1", deleted.UserId);

        Assert.Equal(0, await CountAsync(scenario, deletedPassed));
        Assert.Equal(1, await CountAsync(scenario, keptPassed));
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
