using Duora.Core.Identity;
using Microsoft.Extensions.Time.Testing;
using static Duora.IntegrationTests.Identity.SelfieConcurrencyHarness;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieConcurrencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SecondInstanceCannotCallProviderWhileFirstOwnsLiveLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var barrier = new SelfieCallBarrier();
        var provider = new SimulatedSelfieProvider(time) { FirstStartBarrier = barrier };
        await using var first = Host(connection, time, provider);
        await using var second = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        _ = second.Services;
        var owning = DispatchAsync(first, ct);
        try
        {
            await barrier.WaitForEntryAsync(ct);
            Assert.Equal(0, await DispatchAsync(second, ct));
            Assert.Equal("Pending", (await CurrentAsync(second, account.UserId, ct)).State);
            Assert.False(await EligibleAsync(second, account.UserId, ct));
            Assert.Equal(1, provider.StartCalls);
        }
        finally
        {
            barrier.Release();
            await owning;
        }
        time.Advance(TimeSpan.FromSeconds(6));
        await DispatchAsync(second, ct);
        await AssertCompletedAsync(scenario, second, account.UserId, ct);
        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(1, provider.ReadCalls);
    }

    [Fact]
    public async Task ExpiredLeaseCannotPersistSessionWithoutAnyNewClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var barrier = new SelfieCallBarrier();
        var provider = new SimulatedSelfieProvider(time) { FirstStartBarrier = barrier };
        await using var first = Host(connection, time, provider);
        await using var second = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        _ = second.Services;
        var stale = DispatchAsync(first, ct);
        try
        {
            await barrier.WaitForEntryAsync(ct);
            time.Advance(TimeSpan.FromSeconds(31));
        }
        finally
        {
            barrier.Release();
            await stale;
        }
        Assert.Equal(1, provider.StartResponses);
        // No other owner/version/terminal-state guard can hide an expired-lease write here.
        Assert.Equal("Pending", (await CurrentAsync(second, account.UserId, ct)).State);
        Assert.False(await EligibleAsync(second, account.UserId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and version = 0 and provider_reference is null", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight' and attempts = 1"));
        await DispatchAsync(second, ct);
        time.Advance(TimeSpan.FromSeconds(6));
        await DispatchAsync(second, ct);
        await AssertCompletedAsync(scenario, second, account.UserId, ct);
        Assert.Equal(2, provider.StartCalls);
        Assert.Equal(1, provider.Sessions);
    }

    [Fact]
    public async Task ExpiredStartOwnerCannotPersistAfterAnotherInstanceReclaimsDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var barrier = new SelfieCallBarrier();
        var provider = new SimulatedSelfieProvider(time) { FirstStartBarrier = barrier };
        await using var first = Host(connection, time, provider);
        await using var second = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        _ = second.Services;
        var stale = DispatchAsync(first, ct);
        try
        {
            await barrier.WaitForEntryAsync(ct);
            time.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(1, await DispatchAsync(second, ct));
            Assert.Equal("Capturing", (await CurrentAsync(second, account.UserId, ct)).State);
        }
        finally
        {
            barrier.Release();
            await stale;
        }
        Assert.Equal(2, provider.StartResponses);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and version = 1", account.UserId));
        time.Advance(TimeSpan.FromSeconds(6));
        await DispatchAsync(second, ct);
        await AssertCompletedAsync(scenario, second, account.UserId, ct);
        Assert.Equal(2, provider.StartCalls);
        Assert.Equal(1, provider.Sessions);
        Assert.Equal(1, provider.ReadCalls);
    }

    [Fact]
    public async Task ExpiredOwnerCannotOverwriteNewOwnersInconclusiveResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var barrier = new SelfieCallBarrier();
        var provider = new SimulatedSelfieProvider(time)
        {
            FirstReadBarrier = barrier,
            SubsequentReadResult = SelfieState.Inconclusive,
        };
        await using var first = Host(connection, time, provider);
        await using var second = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        await DispatchAsync(first, ct);
        time.Advance(TimeSpan.FromSeconds(6));
        _ = second.Services;
        var stale = DispatchAsync(first, ct);
        try
        {
            await barrier.WaitForEntryAsync(ct);
            time.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(1, await DispatchAsync(second, ct));
            Assert.Equal("Inconclusive", (await CurrentAsync(second, account.UserId, ct)).State);
        }
        finally
        {
            barrier.Release();
            await stale;
        }
        Assert.Equal("Inconclusive", (await CurrentAsync(second, account.UserId, ct)).State);
        Assert.False(await EligibleAsync(second, account.UserId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = 'PendingEligibility'", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and version = 2", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'"));
        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(2, provider.ReadCalls);
        Assert.Equal(2, provider.ReadResponses);
    }

    [Fact]
    public async Task LostStartResponseRetriesSameAttemptInAnotherInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new SimulatedSelfieProvider(time) { LoseFirstStartResponse = true };
        await using var first = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        await DispatchAsync(first, ct);
        Assert.Equal("Pending", (await CurrentAsync(first, account.UserId, ct)).State);
        Assert.False(await EligibleAsync(first, account.UserId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Pending' and last_error = 'ProviderUnavailable'"));
        await using var restarted = Host(connection, time, provider);
        time.Advance(TimeSpan.FromSeconds(6));
        await DispatchAsync(restarted, ct);
        Assert.Equal("Capturing", (await CurrentAsync(restarted, account.UserId, ct)).State);
        time.Advance(TimeSpan.FromSeconds(6));
        await DispatchAsync(restarted, ct);
        await AssertCompletedAsync(scenario, restarted, account.UserId, ct);
        Assert.Equal(2, provider.StartCalls);
        Assert.Equal(1, provider.Sessions);
        Assert.Equal(1, provider.ReadCalls);
    }

    [Theory]
    [InlineData(false, false, "Pending", 0, "PendingEligibility")]
    [InlineData(false, true, "Capturing", 1, "PendingEligibility")]
    [InlineData(true, false, "Capturing", 1, "PendingEligibility")]
    [InlineData(true, true, "Passed", 2, "Active")]
    public async Task CrashAtApplyCommitRecoversWithoutPartialActivation(bool readingResult, bool afterCommit, string durableState, int durableVersion, string durableAccountStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new SimulatedSelfieProvider(time);
        var fault = new SelfieCommitFault(afterCommit);
        await using var first = Host(connection, time, provider, fault);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        if (readingResult)
        {
            await DispatchAsync(first, ct);
            time.Advance(TimeSpan.FromSeconds(6));
        }
        fault.Arm();
        await Assert.ThrowsAsync<SelfieSimulatedCrash>(() => DispatchAsync(first, ct));
        Assert.True(fault.Fired);
        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(readingResult ? 1 : 0, provider.ReadCalls);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = $2", account.UserId, durableAccountStatus));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and version = $2", account.UserId, durableVersion));
        // Query from a different host/connection, outside the failed transaction/context.
        await using var restarted = Host(connection, time, provider);
        Assert.Equal(durableState, (await CurrentAsync(restarted, account.UserId, ct)).State);
        Assert.Equal(readingResult && afterCommit, await EligibleAsync(restarted, account.UserId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight'"));
        time.Advance(TimeSpan.FromSeconds(31));
        await DispatchAsync(restarted, ct);
        if ((await CurrentAsync(restarted, account.UserId, ct)).State == "Capturing")
        {
            time.Advance(TimeSpan.FromSeconds(6));
            await DispatchAsync(restarted, ct);
        }
        await AssertCompletedAsync(scenario, restarted, account.UserId, ct);
        Assert.Equal(!readingResult && !afterCommit ? 2 : 1, provider.StartCalls);
        Assert.Equal(readingResult && !afterCommit ? 2 : 1, provider.ReadCalls);
        Assert.Equal(1, provider.Sessions);
    }

    private static async Task AssertCompletedAsync(IdentityScenario scenario, DuoraWebFactory host, Guid userId, CancellationToken ct)
    {
        Assert.True(await EligibleAsync(host, userId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = 'Active' and email_confirmed", userId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = 'Passed' and version = 2 and completed_at is not null", userId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'"));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id = $1", userId));
    }
}
