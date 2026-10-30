using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieDispatchTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task ServerResultActivatesOnlyAnUnrestrictedAccount(bool suspendBeforeResult, bool stealLease, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var provider = new Provider();
        await using var factory = new DuoraWebFactory(connection)
        {
            Settings = SelfieTestSettings.Enabled(),
            TestServices = services =>
            {
                services.RemoveAll<ISelfieProvider>();
                services.AddSingleton<ISelfieProvider>(provider);
            },
        };
        var scenario = new IdentityScenario(connection, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SelfieVerificationService>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
        Assert.IsType<SelfieRequestOutcome.Accepted>(await service.RequestAsync(account.UserId, ct));
        await dispatcher.DispatchBatchAsync(ct);
        Assert.Equal("Capturing", (await service.GetCurrentAsync(account.UserId, ct)).State);
        if (suspendBeforeResult)
        {
            await scenario.ScalarAsync("update identity.users set status = 'Suspended' where id = $1 returning 1", account.UserId);
        }

        await scenario.ScalarAsync("update ops.outbox_deliveries set available_at = now() - interval '1 minute' where consumer = 'selfie-provider' returning 1");
        if (stealLease)
        {
            provider.BeforeRead = async () =>
            {
                await scenario.ScalarAsync("update ops.outbox_deliveries set fencing_token = fencing_token + 1 where consumer = 'selfie-provider' returning 1");
            };
        }

        await dispatcher.DispatchBatchAsync(ct);
        Assert.Equal(1, provider.ReadCalls);
        Assert.Equal(expected, await service.GetEligibilityAsync(account.UserId, ct));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id = $1", account.UserId));
    }

    private sealed class Provider : ISelfieProvider
    {
        public Func<Task>? BeforeRead { get; set; }
        public int ReadCalls { get; private set; }
        public Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
            => Task.FromResult(new SelfieSession(attemptId.ToString(), DateTimeOffset.UtcNow.AddMinutes(3)));
        public async Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (BeforeRead is not null)
            {
                await BeforeRead();
            }

            return new SelfieResult(SelfieState.Passed, null, "test");
        }
    }
}
