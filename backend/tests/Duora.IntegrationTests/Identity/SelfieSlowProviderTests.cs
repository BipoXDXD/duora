extern alias worker;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Runtime;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    private const int MoreTriesThanTheAttemptAllows = 40;

    private const string TransportCanary = "CANARY-mensagem-do-fornecedor";

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
        Assert.Equal("Pending|no-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
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
        Assert.Equal("Capturing|has-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        Assert.Equal("Pending|ProviderUnavailable|2", await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = null;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
        Assert.True(await SelfieConcurrencyHarness.EligibleAsync(host, account.UserId, Ct));
    }

    /// <summary>
    /// A tentativa dura 3 minutos e o retry vem a cada 6 s no relógio do teste. Sem sessão, a última chamada é a de 2:30,
    /// e a entrega seguinte encerra: 26 inícios em 27 entregas. Com sessão, a leitura vai até o fim do prazo.
    /// </summary>
    [Theory]
    [InlineData(true, 26, 0, 27)]
    [InlineData(false, 1, 29, 31)]
    public async Task AProviderThatStaysDownEndsTheAttemptExpiredWithoutCountingIt(bool startFails, int expectedStarts, int expectedReads, int expectedDeliveries)
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
        Assert.Equal($"Expired|-|{expectedDeliveries}", await DeliveryAsync(scenario));
        Assert.Equal(startFails ? "Expired|no-reference|PROVIDER_UNAVAILABLE|closed" : "Expired|has-reference|PROVIDER_UNAVAILABLE|closed", await AttemptAsync(scenario));
        var expired = await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct);
        Assert.Equal(("Recapture", SelfieFeedbackCodes.ProviderUnavailable), (expired.NextStep, expired.FeedbackCode));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = provider.StartFault = null;
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
    }

    /// <summary>Em 2:31 restam 29 s: abrir a sessão daria à pessoa um Capturing que ela não tem tempo de usar.</summary>
    [Fact]
    public async Task ASessionIsNotOpenedWithoutTimeLeftToCapture()
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        _time.Advance(TimeSpan.FromSeconds(151));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(0, provider.StartCalls);
        Assert.Equal("Expired|no-reference|PROVIDER_UNAVAILABLE|closed", await AttemptAsync(scenario));
        Assert.Equal("Expired|-|1", await DeliveryAsync(scenario));
        var status = await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct);
        Assert.Equal(("Recapture", new SelfieAttemptStanding.Available(5, LastCycle: false)), (status.NextStep, status.Attempts));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
    }

    [Fact]
    public async Task ASessionIsStillOpenedAtTheExactMinimumWindow()
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        _time.Advance(TimeSpan.FromSeconds(150));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
    }

    /// <summary>O corte vale só para abrir sessão: quem já capturou ainda precisa ter o resultado lido no fim do prazo.</summary>
    [Fact]
    public async Task AnOpenSessionIsStillReadWithLessThanTheMinimumWindow()
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(TimeSpan.FromSeconds(170));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(1, provider.ReadCalls);
        Assert.Equal("Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
    }

    /// <summary>Queda em cada ponto do fluxo: a API distingue a instabilidade sem mudar o passo seguinte, e a volta limpa o sinal.</summary>
    [Theory]
    [InlineData(true, "Pending|no-reference", "Wait")]
    [InlineData(false, "Capturing|has-reference", "Capture")]
    public async Task AnOutageIsSignaledOnTheOpenAttemptAndTheRecoveryClearsIt(bool startFails, string attempt, string nextStep)
    {
        var provider = new FaultySelfieProvider(_time) { Answer = SelfieState.Processing };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        provider.StartFault = startFails ? Unavailable : null;
        provider.ReadFault = Unavailable;
        if (!startFails)
        {
            await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
            _time.Advance(UntilNextTry);
        }

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal($"{attempt}|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        var duringOutage = await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct);
        Assert.Equal((nextStep, SelfieFeedbackCodes.ProviderUnavailable), (duringOutage.NextStep, duringOutage.FeedbackCode));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.StartFault = provider.ReadFault = null;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
        var recovered = await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct);
        Assert.Equal(("Capture", (string?)null), (recovered.NextStep, recovered.FeedbackCode));
    }

    [Fact]
    public async Task AnOutageReachesTheOwnerThroughTheApiWithoutTheProviderMessage()
    {
        var provider = new FaultySelfieProvider(_time) { StartFault = _ => throw TransportFailure("IOException") };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        using var browser = await scenario.SignedInBrowserAsync(account);

        using var current = await browser.GetAsync("/api/v1/me/selfie-verifications/current");
        var body = await current.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.DoesNotContain(TransportCanary, body, StringComparison.Ordinal);
        var status = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        Assert.Equal(["id", "state", "nextStep", "feedbackCode", "expiresAt", "attempts"], status.Select(p => p.Key));
        Assert.Equal("Wait", (string?)status["nextStep"]);
        Assert.Equal("PROVIDER_UNAVAILABLE", (string?)status["feedbackCode"]);
        Assert.Equal(5, (int?)status["attempts"]!["remaining"]);
    }

    /// <summary>A cada 5 s a queda repetiria a escrita: só a primeira falha da sequência grava.</summary>
    [Fact]
    public async Task RepeatedFailuresWriteTheSignalOnce()
    {
        var provider = new FaultySelfieProvider(_time) { StartFault = Unavailable };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        var afterFirstFailure = await VersionAsync(scenario);

        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(2, provider.StartCalls);
        Assert.Equal(afterFirstFailure, await VersionAsync(scenario));
    }

    /// <summary>Worker que perdeu o lease não escreve na tentativa: quem assumiu a entrega é que decide.</summary>
    [Fact]
    public async Task AFailureAfterTheLeaseExpiredDoesNotSignal()
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        provider.ReadFault = cancellationToken =>
        {
            _time.Advance(TimeSpan.FromSeconds(31));
            return Unavailable(cancellationToken);
        };

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
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

    [Theory]
    [InlineData(true, "IOException")]
    [InlineData(true, "TimeoutException")]
    [InlineData(false, "IOException")]
    [InlineData(false, "TimeoutException")]
    public async Task ATransportFailureIsRequeuedKeepingTheAttemptAndCountingNothing(bool startFails, string failure)
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var logs = new CapturingLoggerProvider();
        await using var host = SelfieConcurrencyHarness.Host(connection, _time, provider, logs: logs);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        Func<CancellationToken, Task> fault = _ => throw TransportFailure(failure);
        provider.StartFault = startFails ? fault : null;
        provider.ReadFault = fault;
        if (!startFails)
        {
            await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
            _time.Advance(UntilNextTry);
        }

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(startFails ? "Pending|no-reference|PROVIDER_UNAVAILABLE|open" : "Capturing|has-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        Assert.Equal($"Pending|ProviderUnavailable|{(startFails ? 1 : 2)}", await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);
        var trace = Assert.Single(logs.Entries, entry => entry.Category == typeof(SelfieVerificationDispatcher).FullName);
        Assert.Equal(LogLevel.Warning, trace.Level);
        Assert.Contains(failure, trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TransportCanary, trace.Text, StringComparison.Ordinal);
        Assert.Null(trace.Exception);

        provider.StartFault = provider.ReadFault = null;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(startFails ? "Capturing|has-reference|no-feedback|open" : "Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
    }

    /// <summary>
    /// Pelo SDK real no <see cref="WorkerHost"/> real, com relógio real: o reset de conexão chega como
    /// <see cref="IOException"/> e o timeout do <see cref="HttpClient"/> como <see cref="TimeoutException"/>. O worker
    /// segue vivo e a mesma tentativa é retomada 5 s depois.
    /// </summary>
    [Theory]
    [InlineData("IOException")]
    [InlineData("TimeoutException")]
    public async Task ATransportFailureDoesNotStopTheWorkerAndTheAttemptIsRetried(string failure)
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var transport = new SyntheticAwsTransport(failure == "IOException" ? ConnectionReset : HttpClientTimeout);
        await using var web = RealSdkHost(connection, transport);
        var scenario = new IdentityScenario(connection, web);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        using var workerHost = BuildWorker(connection, services => services.AddSingleton<HttpClientFactory>(transport));
        var lifetime = workerHost.Services.GetRequiredService<IHostApplicationLifetime>();
        var loop = workerHost.Services.GetServices<IHostedService>().OfType<SelfieVerificationProviderService>().Single();

        await workerHost.StartAsync(Ct);
        await WaitWhileRunningAsync(lifetime, async () => await DeliveryAsync(scenario) == "Pending|ProviderUnavailable|1");

        Assert.Equal("Pending|no-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        await AssertNotReleasedAsync(scenario, web, account.UserId);

        transport.Send = HealthySession;
        await WaitWhileRunningAsync(lifetime, async () => await AttemptAsync(scenario) == "Capturing|has-reference|no-feedback|open");

        Assert.False(loop.ExecuteTask!.IsCompleted);
        await AssertNotReleasedAsync(scenario, web, account.UserId);
        await workerHost.StopAsync(Ct);
    }

    /// <summary>
    /// Erro de programação no fornecedor não entra na lista de transitórias: não reagenda, vira log de erro com o tipo,
    /// e a fila segue. A entrega fica presa só até o lease vencer, e o teto de tentativas a encerra.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedProviderErrorIsLoggedWithoutStoppingTheQueue()
    {
        var provider = new FaultySelfieProvider(_time);
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StartFault = _ =>
        {
            if (provider.StartCalls == 1)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            secondCall.TrySetResult();
            return Task.CompletedTask;
        };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var web = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, web);
        var first = await scenario.CreateConfirmedAccountAsync();
        var second = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, first.UserId, Ct);
        await SelfieConcurrencyHarness.RequestAsync(web, second.UserId, Ct);
        var logs = new CapturingLoggerProvider();
        using var workerHost = BuildWorker(connection, services =>
        {
            services.RemoveAll<ISelfieProvider>();
            services.AddSingleton<ISelfieProvider>(provider);
        }, logs);
        var loop = workerHost.Services.GetServices<IHostedService>().OfType<SelfieVerificationProviderService>().Single();

        await workerHost.StartAsync(Ct);
        await secondCall.Task.WaitAsync(CallCeiling, Ct);

        var error = await logs.FirstWarningFrom(typeof(SelfieVerificationProviderService).FullName!).WaitAsync(CallCeiling, Ct);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains(nameof(ArgumentNullException), error.Text, StringComparison.Ordinal);
        Assert.Contains("FaultySelfieProvider", error.Text, StringComparison.Ordinal);
        Assert.Null(error.Exception);
        Assert.False(loop.ExecuteTask!.IsCompleted);
        Assert.False(workerHost.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await workerHost.StopAsync(Ct);
    }

    /// <summary>Parar o worker com a chamada em voo é rotina: nem erro no log, nem falha do fornecedor no rastro.</summary>
    [Fact]
    public async Task StoppingTheWorkerDuringAProviderCallLogsNoErrorAndNoProviderFailure()
    {
        var provider = new FaultySelfieProvider(_time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StartFault = cancellationToken =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var web = SelfieConcurrencyHarness.Host(connection, _time, provider);
        var scenario = new IdentityScenario(connection, web);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        var logs = new CapturingLoggerProvider();
        using var workerHost = BuildWorker(connection, services =>
        {
            services.RemoveAll<ISelfieProvider>();
            services.AddSingleton<ISelfieProvider>(provider);
        }, logs);

        await workerHost.StartAsync(Ct);
        await entered.Task.WaitAsync(CallCeiling, Ct);
        await workerHost.StopAsync(Ct);

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == typeof(SelfieVerificationDispatcher).FullName);
        Assert.Equal("Pending|no-reference|no-feedback|open", await AttemptAsync(scenario));
        Assert.Equal("InFlight|-|1", await DeliveryAsync(scenario));
    }

    private static Task<HttpResponseMessage> Stall(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => new HttpResponseMessage(), TaskScheduler.Default);

    private static Task Unavailable(CancellationToken cancellationToken) =>
        throw new HttpRequestException("Fornecedor sintético indisponível.");

    private static Task<HttpResponseMessage> ConnectionReset(CancellationToken cancellationToken) =>
        throw new HttpRequestException("Conexão sintética encerrada.", new IOException("Conexão sintética encerrada."));

    private static Task<HttpResponseMessage> HttpClientTimeout(CancellationToken cancellationToken) =>
        throw new TaskCanceledException("Tempo sintético esgotado.", new TimeoutException("Tempo sintético esgotado."));

    private static Exception TransportFailure(string name) => name == "IOException"
        ? new IOException(TransportCanary)
        : new TimeoutException(TransportCanary);

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

    private static IHost BuildWorker(string connection, Action<IServiceCollection> services, CapturingLoggerProvider? logs = null)
    {
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={connection}",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. SelfieTestSettings.Enabled(ScriptedSelfieProvider.Policy).Select(setting => $"--{setting.Key}={setting.Value}"),
        ]);
        services(builder.Services);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        return builder.Build();
    }

    /// <summary>Espera a condição no banco e falha na hora se o host do worker começar a parar.</summary>
    private static async Task WaitWhileRunningAsync(IHostApplicationLifetime lifetime, Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(CallCeiling);
        while (!await condition())
        {
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested, "O host do worker parou.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
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

    private static Task<long> VersionAsync(IdentityScenario scenario) =>
        scenario.ScalarAsync("select version from identity.selfie_verifications");

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
            return new SelfieResult(Answer, Answer is SelfieState.Passed or SelfieState.Processing ? null : "CAPTURE_QUALITY", ScriptedSelfieProvider.Policy);
        }
    }
}
