using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieContainerCrashTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("lost-session-response", "Pending", 0, "PendingEligibility", 2, 1)]
    [InlineData("before-session-commit", "Pending", 0, "PendingEligibility", 2, 1)]
    [InlineData("after-session-commit", "Capturing", 1, "PendingEligibility", 1, 1)]
    [InlineData("before-result-commit", "Capturing", 1, "PendingEligibility", 1, 2)]
    [InlineData("after-result-commit", "Passed", 2, "Active", 1, 1)]
    public async Task KilledContainerIsRecoveredWithoutRestartingTheOtherWorker(string interruption,
        string durableState, int durableVersion, string durableAccount, int expectedStarts, int expectedReads)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var inside = new NpgsqlConnectionStringBuilder(connection) { Host = "127.0.0.1", Port = 5432 }.ConnectionString;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = await SelfieWorkerProcess.CreateContainerProviderAsync(postgres.ContainerId,
            time.GetUtcNow(), interruption == "lost-session-response", ct);
        Assert.InRange(provider.ProviderPort, 1, 65535);
        var address = new Uri($"http://127.0.0.1:{provider.ProviderPort}/");
        await using var factory = SelfieConcurrencyHarness.Host(connection, time, new SimulatedSelfieProvider(time));
        var scenario = new IdentityScenario(connection, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(factory, account.UserId, ct);
        var checkpoint = interruption == "lost-session-response" ? "none" : interruption;
        // Both containers are ready before the real 10-second provider timeout starts.
        await using var first = await SelfieWorkerProcess.CreateContainerWorkerAsync(postgres.ContainerId, inside,
            address, time.GetUtcNow(), checkpoint, ct);
        await using var second = await SelfieWorkerProcess.CreateContainerWorkerAsync(postgres.ContainerId, inside,
            address, time.GetUtcNow(), "none", ct);
        Assert.Equal(4, new[] { postgres.ContainerId, provider.ContainerId, first.ContainerId, second.ContainerId }.Distinct().Count());
        var firstState = await first.InspectContainerAsync(ct);
        var secondState = await second.InspectContainerAsync(ct);
        var providerState = await provider.InspectContainerAsync(ct);
        Assert.True(firstState.Running && secondState.Running);
        Assert.True(firstState.HostPid > 0 && secondState.HostPid > 0);
        Assert.NotEqual(firstState.HostPid, secondState.HostPid);
        await first.CommandAsync("start", ct);
        if (interruption.Contains("result", StringComparison.Ordinal))
        {
            await UntilAsync(async () => await scenario.ScalarAsync("""
                select count(*) from identity.selfie_verifications v where v.user_id = $1 and v.state = 'Capturing'
                    and exists (select 1 from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Pending')
                """, account.UserId) == 1, ct);
            await first.CommandAsync("advance", ct, 6);
            await second.CommandAsync("advance", ct, 6);
        }
        if (interruption == "lost-session-response")
        {
            await provider.WaitForProviderStartAsync(ct);
        }
        else
        {
            await first.WaitForCheckpointAsync(ct);
        }
        await second.CommandAsync("start", ct);
        Assert.Equal(0, await second.CommandAsync("probe", ct));
        var stats = await provider.ProviderStatsAsync(ct);
        Assert.Equal(1, stats.Starts);
        Assert.Equal(interruption.Contains("result", StringComparison.Ordinal) ? 1 : 0, stats.Reads);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = $2 and version = $3", account.UserId, durableState, durableVersion));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = $2", account.UserId, durableAccount));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight' and completed_at is null"));
        if (interruption == "lost-session-response")
        {
            Assert.False(stats.FirstResponseWritten);
        }
        await first.ForceKillAsync(ct); // docker kill KILL, label ownership and container exit 137 checked.
        var killed = await first.InspectContainerAsync(ct);
        Assert.False(killed.Running);
        Assert.Equal(137, killed.ExitCode);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight' and completed_at is null"));
        Assert.True((await second.InspectContainerAsync(ct)).Running);
        Assert.Equal(secondState.HostPid, (await second.InspectContainerAsync(ct)).HostPid);
        Assert.True((await provider.InspectContainerAsync(ct)).Running);
        await provider.ReleaseProviderResponseAsync(ct);
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
        stats = await provider.ProviderStatsAsync(ct);
        Assert.Equal(expectedStarts, stats.Starts);
        Assert.Equal(expectedReads, stats.Reads);
        Assert.Equal(1, stats.Sessions);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id = $1", account.UserId));
        Assert.Equal(secondState.HostPid, (await second.InspectContainerAsync(ct)).HostPid);
        Assert.Equal(providerState.HostPid, (await provider.InspectContainerAsync(ct)).HostPid);
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
