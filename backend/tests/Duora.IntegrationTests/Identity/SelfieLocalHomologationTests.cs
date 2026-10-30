using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieLocalHomologationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DisabledServiceDoesNotCreateAttemptsCredentialsOrCallProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var provider = new ForbiddenProvider();
        await using var factory = new DuoraWebFactory(connection)
        {
            Settings = new Dictionary<string, string?> { ["SelfieVerification:Enabled"] = "false" },
            TestServices = services =>
            {
                services.RemoveAll<ISelfieProvider>();
                services.AddSingleton<ISelfieProvider>(provider);
            },
        };
        var scenario = new IdentityScenario(connection, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var scope = factory.Services.CreateScope();
        Assert.IsType<SelfieRequestOutcome.Unavailable>(await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().RequestAsync(account.UserId, ct));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<SelfieCaptureCredentialService>().CreateAsync(account.UserId, Guid.NewGuid(), ct));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>().DispatchBatchAsync(ct));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", account.UserId));
        Assert.False(await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().GetEligibilityAsync(account.UserId, ct));
        // Seed pending work using only the in-memory test options and forbidden provider.
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SelfieProviderOptions>>().Value;
        options.Enabled = true;
        options.PolicyVersion = "synthetic-policy";
        var pending = await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().RequestAsync(account.UserId, ct);
        options.Enabled = false;
        Assert.IsType<SelfieRequestOutcome.Accepted>(pending);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>().DispatchBatchAsync(ct));
        Assert.Equal("Pending", (await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().GetCurrentAsync(account.UserId, ct)).State);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Pending'"));
    }

    [Theory]
    [InlineData("synthetic-reference")]
    [InlineData(null)]
    public async Task DisabledCleanupRemovesExpiredTransientDataWithoutRemovingResult(string? reference)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        await using var factory = new DuoraWebFactory(connection)
        {
            Settings = new Dictionary<string, string?> { ["SelfieVerification:Enabled"] = "false" },
        };
        var scenario = new IdentityScenario(connection, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        var id = Guid.NewGuid();
        await scenario.ScalarAsync("""
            insert into identity.selfie_verifications
            (id, user_id, state, policy_version, provider_reference, feedback_code, requested_at, expires_at, completed_at, version)
            values ($1, $2, 'Inconclusive', 'synthetic-policy', nullif($3, ''), 'SyntheticFeedback', now() - interval '26 hours',
                now() - interval '25 hours', now() - interval '25 hours', 1) returning 1
            """, id, account.UserId, reference ?? "");
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>().DispatchBatchAsync(ct));
        Assert.Equal(1, await scenario.ScalarAsync("""
            select count(*) from identity.selfie_verifications where id = $1 and provider_reference is null
                and feedback_code is null and state = 'Inconclusive' and policy_version = 'synthetic-policy'
            """, id));
    }

    private sealed class ForbiddenProvider : ISelfieProvider
    {
        public int Calls { get; private set; }
        public Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Provider must not be called while disabled.");
        }
        public Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Provider must not be called while disabled.");
        }
    }
}
