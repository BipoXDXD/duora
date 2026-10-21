using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieProcessCrashTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("lost-session-response", "Pending", 0, "PendingEligibility", 2, 1)]
    [InlineData("before-session-commit", "Pending", 0, "PendingEligibility", 2, 1)]
    [InlineData("after-session-commit", "Capturing", 1, "PendingEligibility", 1, 1)]
    [InlineData("before-result-commit", "Capturing", 1, "PendingEligibility", 1, 2)]
    [InlineData("after-result-commit", "Passed", 2, "Active", 1, 1)]
    public async Task KilledOwnerIsRecoveredByASeparateWorkerProcess(string interruption, string durableState,
        int durableVersion, string durableAccount, int expectedStarts, int expectedReads)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var fake = await LoopbackSelfieServer.StartAsync(time.GetUtcNow(), interruption == "lost-session-response", ct);
        await using var factory = SelfieConcurrencyHarness.Host(connection, time, new SimulatedSelfieProvider(time));
        var scenario = new IdentityScenario(connection, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(factory, account.UserId, ct);
        var checkpoint = interruption == "lost-session-response" ? "none" : interruption;
        // Both OS processes are ready before the provider's real 10-second timeout begins.
        await using var first = await SelfieWorkerProcess.CreateAsync(connection, fake.Address, time.GetUtcNow(), checkpoint, ct);
        await using var second = await SelfieWorkerProcess.CreateAsync(connection, fake.Address, time.GetUtcNow(), "none", ct);
        Assert.NotEqual(Environment.ProcessId, first.Id);
        Assert.NotEqual(Environment.ProcessId, second.Id);
        Assert.NotEqual(first.Id, second.Id);
        await first.CommandAsync("start", ct);
        if (interruption.Contains("result", StringComparison.Ordinal))
        {
            await UntilAsync(async () => await scenario.ScalarAsync("""
                select count(*) from identity.selfie_verifications v
                where v.user_id = $1 and v.state = 'Capturing'
                    and exists (select 1 from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Pending')
                """, account.UserId) == 1, ct);
            await first.CommandAsync("advance", ct, 6);
            await second.CommandAsync("advance", ct, 6);
        }
        if (interruption == "lost-session-response")
        {
            await fake.WaitForAcceptedStartAsync(ct);
        }
        else
        {
            await first.WaitForCheckpointAsync(ct);
        }
        await second.CommandAsync("start", ct);
        Assert.Equal(0, await second.CommandAsync("probe", ct));
        Assert.False(first.HasExited);
        Assert.False(second.HasExited);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(interruption.Contains("result", StringComparison.Ordinal) ? 1 : 0, fake.Reads);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = $2 and version = $3", account.UserId, durableState, durableVersion));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = $2", account.UserId, durableAccount));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight' and completed_at is null"));
        if (interruption == "lost-session-response")
        {
            Assert.False(fake.FirstStartAborted, "The process must be killed while the external response is still pending, not after timeout.");
        }
        await first.ForceKillAsync(ct);
        // A provider timeout must not have rescheduled the delivery before the forced termination.
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight' and completed_at is null"));
        fake.ReleaseStartResponse();
        Assert.False(second.HasExited);
        await second.CommandAsync("advance", ct, 31);
        if (expectedStarts == 2)
        {
            await UntilAsync(async () => await scenario.ScalarAsync("""
                select count(*) from identity.selfie_verifications where user_id = $1 and state = 'Capturing' and version = 1
                """, account.UserId) == 1 && await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Pending'") == 1, ct);
            await second.CommandAsync("advance", ct, 6);
        }
        await UntilAsync(async () => await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'") == 1, ct);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = 'Passed' and version = 2 and completed_at is not null", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = 'Active' and email_confirmed", account.UserId));
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().GetEligibilityAsync(account.UserId, ct));
        Assert.Equal(expectedStarts, fake.Starts);
        Assert.Equal(expectedReads, fake.Reads);
        Assert.Equal(1, fake.Sessions);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id = $1", account.UserId));
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
    }
}
