using System.Text.RegularExpressions;
using Duora.Core.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// A consulta que <c>scripts/telemetry/collect.py</c> roda a cada 15 s, contra o schema migrado e o dispatcher real:
/// se a fila mudar de forma, o alerta do fornecedor quebra aqui, e não em silêncio na produção. O texto vem do próprio
/// script, para não existir uma cópia que envelheça.
/// </summary>
public sealed class SelfieProviderProbeTests(PostgresFixture postgres)
{
    private static readonly TimeSpan UntilNextTry = TimeSpan.FromSeconds(6);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AProviderFailingNowIsReportedAndARecoveredOneIsNot()
    {
        await using var attempt = await OpenAttemptAsync(new FakeTimeProvider(Now));
        attempt.Provider.ThenUnavailable();

        await attempt.ReadProviderAsync();

        Assert.True(await attempt.ProbeAsync());

        attempt.Provider.Then(SelfieState.Processing);
        await attempt.ReadProviderAsync();

        Assert.False(await attempt.ProbeAsync());
    }

    [Theory]
    [InlineData(SelfieState.Processing)]
    [InlineData(SelfieState.RecaptureRequired)]
    [InlineData(SelfieState.Inconclusive)]
    [InlineData(SelfieState.Expired)]
    [InlineData(SelfieState.Passed)]
    public async Task WhatThePersonDidOrFailedToDoIsNeverAProviderFailure(SelfieState answer)
    {
        await using var attempt = await OpenAttemptAsync(new FakeTimeProvider(Now));
        attempt.Provider.Then(answer);

        await attempt.ReadProviderAsync();

        Assert.False(await attempt.ProbeAsync());
    }

    [Fact]
    public async Task AnAttemptThatExpiredDuringTheOutageStopsCounting()
    {
        var time = new FakeTimeProvider(Now);
        await using var attempt = await OpenAttemptAsync(time);
        attempt.Provider.ThenUnavailable();
        await attempt.ReadProviderAsync();
        time.Advance(TimeSpan.FromMinutes(4));

        await attempt.DispatchAsync();

        Assert.False(await attempt.ProbeAsync());
    }

    [Fact]
    public async Task AMarkerLeftBehindByAStoppedWorkerDoesNotBlameTheProvider()
    {
        await using var attempt = await OpenAttemptAsync(new FakeTimeProvider(Now - TimeSpan.FromMinutes(2)));
        attempt.Provider.ThenUnavailable();

        await attempt.ReadProviderAsync();

        Assert.False(await attempt.ProbeAsync());
    }

    [Theory]
    [InlineData("30 seconds", true)]
    [InlineData("-1 seconds", false)]
    public async Task AnAttemptBeingRetriedCountsOnlyWhileItsLeaseIsAlive(string lease, bool failing)
    {
        await using var attempt = await OpenAttemptAsync(new FakeTimeProvider(Now));
        attempt.Provider.ThenUnavailable();
        await attempt.ReadProviderAsync();

        await attempt.Scenario.ScalarAsync("""
            with changed as (
                update ops.outbox_deliveries set status = 'InFlight', lease_expires_at = now() + $1::interval returning 1)
            select count(*) from changed
            """, lease);

        Assert.Equal(failing, await attempt.ProbeAsync());
    }

    [Fact]
    public async Task TheSameMarkerOnAnotherConsumerIsIgnored()
    {
        await using var attempt = await OpenAttemptAsync(new FakeTimeProvider(Now));
        attempt.Provider.ThenUnavailable();
        await attempt.ReadProviderAsync();

        await attempt.Scenario.ScalarAsync("""
            with changed as (update ops.outbox_deliveries set consumer = 'another-consumer' returning 1)
            select count(*) from changed
            """);

        Assert.False(await attempt.ProbeAsync());
    }

    private static DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static string ProbeQuery()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "scripts", "telemetry", "collect.py"));
        return Assert.Single(Regex.Matches(script, "SELFIE_PROVIDER_QUERY = \"\"\"(.*?)\"\"\"", RegexOptions.Singleline)).Groups[1].Value;
    }

    private async Task<OpenAttempt> OpenAttemptAsync(FakeTimeProvider time)
    {
        var provider = new ScriptedSelfieProvider(time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = SelfieConcurrencyHarness.Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        var attempt = new OpenAttempt(connection, host, scenario, provider, time);
        await attempt.DispatchAsync();
        return attempt;
    }

    private sealed record OpenAttempt(
        string Connection, DuoraWebFactory Host, IdentityScenario Scenario, ScriptedSelfieProvider Provider, FakeTimeProvider Time)
        : IAsyncDisposable
    {
        public async Task ReadProviderAsync()
        {
            Time.Advance(UntilNextTry);
            await DispatchAsync();
        }

        public Task<int> DispatchAsync() => SelfieConcurrencyHarness.DispatchAsync(Host, Ct);

        public async Task<bool> ProbeAsync()
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(ProbeQuery(), connection);
            return Assert.IsType<bool>(await command.ExecuteScalarAsync(Ct));
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
