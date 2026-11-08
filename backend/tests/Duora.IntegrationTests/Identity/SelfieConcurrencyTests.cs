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
        // Uma versão a mais que o caminho sem falha: a resposta perdida grava o sinal de instabilidade.
        await AssertCompletedAsync(scenario, restarted, account.UserId, ct, expectedVersion: 3);
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

    /// <summary>
    /// A fila entrega pelo menos uma vez: a mesma tentativa chega em duas entregas, uma em cada worker, e as duas leem
    /// a aprovação. Só a primeira resposta grava e libera a conta; a segunda é descartada, e a entrega que ficou presa
    /// fecha depois do lease sem chamar o fornecedor de novo.
    /// </summary>
    [Fact]
    public async Task ADuplicatedDeliveryForTheSameAttemptReleasesTheAccountOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var barrier = new SelfieCallBarrier();
        var provider = new SimulatedSelfieProvider(time) { FirstReadBarrier = barrier };
        await using var first = Host(connection, time, provider);
        await using var second = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(first, account.UserId, ct);
        await DispatchAsync(first, ct);
        Assert.Equal(1, await DuplicateSelfieDeliveryAsync(scenario));
        time.Advance(TimeSpan.FromSeconds(6));
        _ = second.Services;

        string releasedBy;
        var owning = DispatchAsync(first, ct);
        try
        {
            await barrier.WaitForEntryAsync(ct);
            Assert.Equal(1, await DispatchAsync(second, ct));
            releasedBy = await ReleaseFootprintAsync(scenario, account.UserId);
            Assert.True(await EligibleAsync(second, account.UserId, ct));
        }
        finally
        {
            barrier.Release();
            await owning;
        }

        Assert.Equal(releasedBy, await ReleaseFootprintAsync(scenario, account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'InFlight'"));

        time.Advance(TimeSpan.FromSeconds(31));
        await DispatchAsync(second, ct);

        Assert.Equal(2, provider.ReadCalls);
        Assert.Equal(releasedBy, await ReleaseFootprintAsync(scenario, account.UserId));
        Assert.Equal(2, await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'"));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    /// <summary>
    /// O <c>/complete</c> do navegador é só um aviso: repetido antes e depois do resultado, não cria trabalho, não muda a
    /// tentativa nem a fila, não chama o fornecedor e não libera nem mexe de novo na conta.
    /// </summary>
    [Fact]
    public async Task RepeatedCompletionHintsChangeNeitherTheAttemptNorTheQueue()
    {
        const int Hints = 3;
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new SimulatedSelfieProvider(time);
        await using var host = Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await RequestAsync(host, account.UserId, ct);
        await DispatchAsync(host, ct);
        var attempt = (await CurrentAsync(host, account.UserId, ct)).Id!.Value;
        using var browser = await scenario.SignedInBrowserAsync(account);
        var beforeResult = await QueueFootprintAsync(scenario, account.UserId);

        for (var hint = 0; hint < Hints; hint++)
        {
            Assert.Equal("Capturing|Capture", await CompleteAsync(browser, attempt, ct));
        }

        Assert.Equal(beforeResult, await QueueFootprintAsync(scenario, account.UserId));
        Assert.Equal((1, 0), (provider.StartCalls, provider.ReadCalls));
        Assert.False(await EligibleAsync(host, account.UserId, ct));

        time.Advance(TimeSpan.FromSeconds(6));
        await DispatchAsync(host, ct);
        var afterResult = await QueueFootprintAsync(scenario, account.UserId);
        Assert.True(await EligibleAsync(host, account.UserId, ct));

        for (var hint = 0; hint < Hints; hint++)
        {
            Assert.Equal("Passed|None", await CompleteAsync(browser, attempt, ct));
        }

        Assert.Equal(afterResult, await QueueFootprintAsync(scenario, account.UserId));
        Assert.Equal((1, 1), (provider.StartCalls, provider.ReadCalls));
    }

    private static async Task<string> CompleteAsync(Browser browser, Guid attempt, CancellationToken ct)
    {
        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/complete", new { });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return $"{(string?)body["state"]}|{(string?)body["nextStep"]}";
    }

    /// <summary>Uma segunda entrega da mesma tentativa, em outro evento, pronta no mesmo instante da original.</summary>
    private static Task<long> DuplicateSelfieDeliveryAsync(IdentityScenario scenario) => scenario.ScalarAsync("""
        with original as (
            select e.type, e.schema_version, e.resource_type, e.resource_id, e.resource_version, e.payload, e.occurred_at,
                   d.consumer, d.recipient_key, d.available_at, d.deadline_at, d.created_at
            from ops.outbox_deliveries d join ops.outbox_events e on e.id = d.outbox_event_id
            where d.consumer = 'selfie-provider'
        ), copied as (
            insert into ops.outbox_events (id, type, schema_version, resource_type, resource_id, resource_version, payload, occurred_at)
            select gen_random_uuid(), type, schema_version, resource_type, resource_id, resource_version, payload, occurred_at
            from original
            returning id
        )
        insert into ops.outbox_deliveries (id, outbox_event_id, consumer, recipient_key, status, attempts, available_at,
                                           deadline_at, fencing_token, created_at)
        select gen_random_uuid(), copied.id, original.consumer, original.recipient_key, 'Pending', 0, original.available_at,
               original.deadline_at, 0, original.created_at
        from copied, original
        returning 1
        """);

    /// <summary>Tudo que a liberação escreve: tentativa, versão, conclusão e a conta com o carimbo de concorrência.</summary>
    private static Task<string> ReleaseFootprintAsync(IdentityScenario scenario, Guid userId) => scenario.ScalarTextAsync("""
        select v.state || '|' || v.version || '|' || v.completed_at || '|' || u.status || '|' || u.concurrency_stamp
        from identity.selfie_verifications v join identity.users u on u.id = v.user_id
        where v.user_id = $1
        """, userId);

    /// <summary>Tentativa, conta e cada entrega da selfie, com tentativas e horário: qualquer trabalho novo aparece aqui.</summary>
    private static Task<string> QueueFootprintAsync(IdentityScenario scenario, Guid userId) => scenario.ScalarTextAsync("""
        select (select v.state || '|' || v.version || '|' || coalesce(v.feedback_code, '-') || '|' || u.status || '|' || u.concurrency_stamp
                from identity.selfie_verifications v join identity.users u on u.id = v.user_id where v.user_id = $1)
            || '#' || (select string_agg(d.status || '|' || d.attempts || '|' || d.available_at || '|' || d.fencing_token, ';' order by d.id)
                       from ops.outbox_deliveries d where d.consumer = 'selfie-provider')
            || '#' || (select count(*) from ops.outbox_events)
        """, userId);

    private static async Task AssertCompletedAsync(IdentityScenario scenario, DuoraWebFactory host, Guid userId, CancellationToken ct, int expectedVersion = 2)
    {
        Assert.True(await EligibleAsync(host, userId, ct));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id = $1 and status = 'Active' and email_confirmed", userId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1 and state = 'Passed' and version = $2 and completed_at is not null", userId, (long)expectedVersion));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'"));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id = $1", userId));
    }
}
