using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieCommitNetworkTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("session", false, "Pending", 0, "PendingEligibility", "InFlight", 1, 2, 1)]
    [InlineData("session", true, "Capturing", 1, "PendingEligibility", "InFlight", 1, 1, 1)]
    [InlineData("result", false, "Capturing", 1, "PendingEligibility", "InFlight", 1, 1, 2)]
    [InlineData("result", true, "Passed", 2, "Active", "InFlight", 1, 1, 1)]
    [InlineData("settle", false, "Passed", 2, "Active", "InFlight", 1, 1, 1)]
    [InlineData("settle", true, "Passed", 2, "Active", "Delivered", 0, 1, 1)]
    public async Task LostCommitConnectionRecoversFromTheDurableState(string phase, bool serverCommitted,
        string durableState, int durableVersion, string durableAccount, string durableDelivery,
        int recoveryClaims, int expectedStarts, int expectedReads)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var readingResult = phase != "session";
        var barrier = new SelfieCallBarrier();
        var provider = new SimulatedSelfieProvider(time)
        {
            FirstStartBarrier = readingResult ? null : barrier,
            FirstReadBarrier = readingResult ? barrier : null,
        };
        await using var proxy = new CommitConnectionProxy(connection);
        await using var worker = SelfieConcurrencyHarness.Host(proxy.ConnectionString, time, provider);
        await using var recovery = SelfieConcurrencyHarness.Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, recovery);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(recovery, account.UserId, ct);
        if (readingResult)
        {
            Assert.Equal(1, await SelfieConcurrencyHarness.DispatchAsync(worker, ct));
            time.Advance(TimeSpan.FromSeconds(6));
        }

        var dispatch = SelfieConcurrencyHarness.DispatchAsync(worker, ct);
        await barrier.WaitForEntryAsync(ct);
        // Claim/renewal finished before the call. Settlement follows the independently acknowledged apply.
        proxy.Arm(serverCommitted, phase == "settle" ? 1 : 0);
        barrier.Release();
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => dispatch);
        Assert.Equal(serverCommitted, await proxy.WaitForCutAsync(ct));
        Assert.Equal(phase == "settle" ? 1 : 0, proxy.ForwardedCommitAcknowledgements);

        // This connection never traverses the proxy, so an unacknowledged COMMIT cannot fool the inspection.
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = $2 and version = $3",
            account.UserId, durableState, durableVersion));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = $2",
            account.UserId, durableAccount));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = $1 and (completed_at is not null) = $2",
            durableDelivery, durableDelivery == "Delivered"));
        Assert.Equal(0, await SelfieConcurrencyHarness.DispatchAsync(recovery, ct));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(recoveryClaims, await SelfieConcurrencyHarness.DispatchAsync(recovery, ct));
        if (!readingResult && !serverCommitted)
        {
            Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = 'Capturing' and version = 1", account.UserId));
            time.Advance(TimeSpan.FromSeconds(6));
            Assert.Equal(1, await SelfieConcurrencyHarness.DispatchAsync(recovery, ct));
        }

        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = 'Passed' and version = 2 and completed_at is not null", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = 'Active' and email_confirmed", account.UserId));
        Assert.True(await SelfieConcurrencyHarness.EligibleAsync(recovery, account.UserId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'"));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id = $1", account.UserId));
        Assert.Equal(expectedStarts, provider.StartCalls);
        Assert.Equal(expectedReads, provider.ReadCalls);
        Assert.Equal(1, provider.Sessions);
    }
}
