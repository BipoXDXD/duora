extern alias worker;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Runtime;
using Duora.Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-17: fornecedor da selfie lento ou indisponível, no PostgreSQL real. A conta não libera participação, nada fica
/// pela metade e cada chamada tem tempo limitado. Sem rede: o SDK real fala com um transporte sintético, e os demais
/// casos usam um fornecedor falso. O corte de 10 s do dispatcher usa o relógio real, então os dois testes de stall
/// esperam esse tempo de verdade, sem CPU.
/// </summary>
public sealed class SelfieSlowProviderTests(PostgresFixture postgres)
{
    /// <summary>Abaixo dos 10 s do dispatcher: prova que a chamada ficou presa até o corte, sem medir precisão de timer.</summary>
    private static readonly TimeSpan BelowTheCallTimeout = TimeSpan.FromSeconds(9);

    /// <summary>O lease da entrega: uma chamada que passasse dele deixaria outro worker repetir o trabalho em paralelo.</summary>
    private static readonly TimeSpan CallCeiling = TimeSpan.FromSeconds(30);

    /// <summary>Maior que a espera de 5 s até a próxima tentativa.</summary>
    private static readonly TimeSpan UntilNextTry = TimeSpan.FromSeconds(6);

    /// <summary>Maior que o lease de 30 s, contado a partir do relógio real do worker no caso da lacuna.</summary>
    private static readonly TimeSpan PastTheLease = TimeSpan.FromSeconds(90);

    private const int MoreTriesThanTheAttemptAllows = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Fact]
    public async Task AStalledSessionStartThroughTheRealSdkIsCutAndRetriedWithoutReleasingTheAccount()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var transport = new SyntheticAwsTransport(Stall);
        await using var host = RealSdkHost(connection, transport);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);

        var elapsed = await TimedDispatchAsync(host);

        Assert.InRange(elapsed, BelowTheCallTimeout, CallCeiling);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("Pending|no-reference|no-feedback|open", await AttemptAsync(scenario));
        Assert.Equal("Pending|ProviderUnavailable|1", await DeliveryAsync(scenario));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and available_at = $1",
            _time.GetUtcNow().AddSeconds(5)));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        transport.Send = HealthySession;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);
    }

    [Fact]
    public async Task AStalledResultReadIsCutAndOnlyTheNextAnswerDecides()
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        provider.ReadFault = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken);

        var elapsed = await TimedDispatchAsync(host);

        Assert.InRange(elapsed, BelowTheCallTimeout, CallCeiling);
        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
        Assert.Equal("Pending|ProviderUnavailable|2", await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = null;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
        Assert.True(await SelfieConcurrencyHarness.EligibleAsync(host, account.UserId, Ct));
    }

    /// <summary>A tentativa dura 3 minutos e o retry vem a cada 6 s no relógio do teste: 30 chamadas ao todo, contando o início.</summary>
    [Theory]
    [InlineData(true, 30, 0)]
    [InlineData(false, 1, 29)]
    public async Task AProviderThatStaysDownEndsTheAttemptExpiredWithoutCountingIt(bool startFails, int expectedStarts, int expectedReads)
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.ReadFault = Unavailable;
        provider.StartFault = startFails ? Unavailable : null;
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);

        for (var attempt = 0; attempt < MoreTriesThanTheAttemptAllows && (await DeliveryAsync(scenario)).StartsWith("Pending", StringComparison.Ordinal); attempt++)
        {
            await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
            _time.Advance(UntilNextTry);
        }

        Assert.Equal((expectedStarts, expectedReads), (provider.StartCalls, provider.ReadCalls));
        Assert.Equal("Expired|-|31", await DeliveryAsync(scenario));
        Assert.Equal(startFails ? "Expired|no-reference|no-feedback|closed" : "Expired|has-reference|no-feedback|closed", await AttemptAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = provider.StartFault = null;
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
    }

    [Theory]
    [InlineData(SelfieState.Passed, 0)]
    [InlineData(SelfieState.RecaptureRequired, 1)]
    public async Task AnAnswerAfterTheLeaseExpiredIsDiscardedAndReadAgainOnce(SelfieState answer, int expectedFailures)
    {
        var provider = new FaultySelfieProvider(_time) { Answer = answer };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        provider.ReadFault = _ =>
        {
            _time.Advance(TimeSpan.FromSeconds(31));
            return Task.CompletedTask;
        };

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = null;
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(2, provider.ReadCalls);
        Assert.Equal(answer == SelfieState.Passed, await SelfieConcurrencyHarness.EligibleAsync(host, account.UserId, Ct));
        Assert.Equal(expectedFailures, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    /// <summary>
    /// Lacuna do T-17, gravada como está hoje para não passar despercebida: o SDK entrega o reset de conexão como
    /// <see cref="IOException"/>, que nem o dispatcher nem o loop do worker tratam, e a exceção para o host inteiro. O
    /// dado continua íntegro. Quando a produção tratar esse caso, este teste falha e deve virar a garantia de que o
    /// worker segue vivo e reagenda a entrega.
    /// </summary>
    [Fact]
    public async Task KnownGapAConnectionResetStopsTheWholeWorkerButLeavesTheAttemptIntact()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var transport = new SyntheticAwsTransport(ConnectionReset);
        await using var web = RealSdkHost(connection, transport);
        var scenario = new IdentityScenario(connection, web);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        using var workerHost = BuildWorker(connection, transport);
        var lifetime = workerHost.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        await workerHost.StartAsync(Ct);
        var loop = workerHost.Services.GetServices<IHostedService>().OfType<SelfieVerificationProviderService>().Single();
        await Assert.ThrowsAsync<IOException>(() => loop.ExecuteTask!.WaitAsync(CallCeiling, Ct));
        await stopping.Task.WaitAsync(CallCeiling, Ct);
        await workerHost.StopAsync(Ct);

        Assert.Equal("Pending|no-reference|no-feedback|open", await AttemptAsync(scenario));
        Assert.Equal("InFlight|-|1", await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, web, account.UserId);

        transport.Send = HealthySession;
        _time.Advance(PastTheLease);
        await SelfieConcurrencyHarness.DispatchAsync(web, Ct);

        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
    }

    private static Task<HttpResponseMessage> Stall(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => new HttpResponseMessage(), TaskScheduler.Default);

    private static Task Unavailable(CancellationToken cancellationToken) =>
        throw new HttpRequestException("Fornecedor sintético indisponível.");

    private static Task<HttpResponseMessage> ConnectionReset(CancellationToken cancellationToken) =>
        throw new HttpRequestException("Conexão sintética encerrada.", new IOException("Conexão sintética encerrada."));

    private static Task<HttpResponseMessage> HealthySession(CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"SessionId":"{{Guid.NewGuid():D}}"}""", Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.1");
        return Task.FromResult(response);
    }

    private DuoraWebFactory RealSdkHost(string connection, SyntheticAwsTransport transport) => new(connection)
    {
        Time = _time,
        Settings = SelfieTestSettings.Enabled(ScriptedSelfieProvider.Policy),
        TestServices = services => services.AddSingleton<HttpClientFactory>(transport),
    };

    private static IHost BuildWorker(string connection, SyntheticAwsTransport transport)
    {
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={connection}",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. SelfieTestSettings.Enabled(ScriptedSelfieProvider.Policy).Select(setting => $"--{setting.Key}={setting.Value}"),
        ]);
        builder.Services.AddSingleton<HttpClientFactory>(transport);
        return builder.Build();
    }

    /// <summary>Tempo real de um lote; falha em vez de pendurar a suíte se a chamada não for cortada.</summary>
    private static async Task<TimeSpan> TimedDispatchAsync(DuoraWebFactory host)
    {
        var watch = Stopwatch.StartNew();
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct).WaitAsync(CallCeiling, Ct);
        return watch.Elapsed;
    }

    private static Task<string> AttemptAsync(IdentityScenario scenario) => scenario.ScalarTextAsync("""
        select state
            || case when provider_reference is null then '|no-reference' else '|has-reference' end
            || '|' || coalesce(feedback_code, 'no-feedback')
            || case when completed_at is null then '|open' else '|closed' end
        from identity.selfie_verifications
        order by requested_at desc, id
        limit 1
        """);

    private static Task<string> DeliveryAsync(IdentityScenario scenario) => scenario.ScalarTextAsync("""
        select status || '|' || coalesce(last_error, '-') || '|' || attempts
        from ops.outbox_deliveries
        where consumer = 'selfie-provider'
        order by created_at
        limit 1
        """);

    private static async Task AssertNotReleasedAsync(IdentityScenario scenario, DuoraWebFactory host, Guid userId)
    {
        Assert.False(await SelfieConcurrencyHarness.EligibleAsync(host, userId, Ct));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and status = 'PendingEligibility'", userId));
        Assert.Equal(0, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_failures where user_id = $1", userId));
        Assert.Equal(0, await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-attempts-suspension'"));
    }

    /// <summary>Transporte do SDK da AWS só de teste: nenhuma chamada sai da máquina e o comportamento troca no meio do teste.</summary>
    private sealed class SyntheticAwsTransport(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpClientFactory
    {
        private int _calls;

        public Func<CancellationToken, Task<HttpResponseMessage>> Send { get; set; } = send;

        public int Calls => Volatile.Read(ref _calls);

        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new Handler(this));

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;

        private sealed class Handler(SyntheticAwsTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                return owner.Send(cancellationToken);
            }
        }
    }

    /// <summary>Fornecedor sintético cuja falha ou demora roda antes de cada resposta e pode ser trocada entre lotes.</summary>
    private sealed class FaultySelfieProvider(FakeTimeProvider time) : ISelfieProvider
    {
        private int _startCalls;
        private int _readCalls;

        public Func<CancellationToken, Task>? StartFault { get; set; }

        public Func<CancellationToken, Task>? ReadFault { get; set; }

        public SelfieState Answer { get; init; } = SelfieState.Passed;

        public int StartCalls => Volatile.Read(ref _startCalls);

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public async Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCalls);
            await (StartFault?.Invoke(cancellationToken) ?? Task.CompletedTask);
            return new SelfieSession(attemptId.ToString("D"), time.GetUtcNow().AddMinutes(3));
        }

        public async Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCalls);
            await (ReadFault?.Invoke(cancellationToken) ?? Task.CompletedTask);
            return new SelfieResult(Answer, Answer == SelfieState.Passed ? null : "CAPTURE_QUALITY", ScriptedSelfieProvider.Policy);
        }
    }
}
