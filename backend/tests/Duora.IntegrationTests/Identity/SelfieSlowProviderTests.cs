extern alias worker;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Runtime;
using Duora.Core.Identity;
using Duora.Infrastructure.Email;
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
/// casos usam um fornecedor falso. O corte de 10 s do dispatcher corre no <see cref="TimeProvider"/> do worker, então os
/// testes de travamento avançam o relógio falso até ele, sem esperar tempo de verdade.
/// </summary>
public sealed class SelfieSlowProviderTests(PostgresFixture postgres)
{
    /// <summary>Em tempo real, abaixo dos 10 s do corte: as repetições do próprio SDK terminam antes dele.</summary>
    private static readonly TimeSpan BelowTheCallTimeout = TimeSpan.FromSeconds(9);

    /// <summary>O corte do dispatcher, no relógio do worker.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Teto em tempo real para o que roda em outra thread: falha em vez de pendurar a suíte.</summary>
    private static readonly TimeSpan CallCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Teto em tempo real do teste inteiro, descarte do host incluído. Fica acima dos tetos internos, que falham antes e
    /// apontam a causa; o teste mais lento desta classe levou cerca de 10 s na CI.
    /// </summary>
    private static readonly TimeSpan WholeTestCeiling = TimeSpan.FromSeconds(90);

    /// <summary>Acima do teto do teste: uma leitura vencida nunca chega antes da falha e não se passa pelo corte do worker.</summary>
    private static readonly TimeSpan StalledReadCeiling = WholeTestCeiling + CallCeiling;

    /// <summary>Maior que a espera de 5 s até a próxima tentativa.</summary>
    private static readonly TimeSpan UntilNextTry = TimeSpan.FromSeconds(6);

    private const int MoreTriesThanTheAttemptAllows = 40;

    private const string TransportCanary = "CANARY-mensagem-do-fornecedor";

    private const string HostDisposalStep = "descartando o host";

    private const int AttemptsInTheBacklog = 3;

    /// <summary>A 6 s por rodada, a última falha cai 174 s depois do pedido, e a próxima leitura fica liberada em 179 s.</summary>
    private const int FailuresBeforeTheLastRead = 29;

    /// <summary>Maior que os 5 lotes que cabem no reagendamento de 5 s, para a fila nunca ficar sem trabalho pronto.</summary>
    private const int AttemptsAboveTheRetrySpacing = 7;

    private const int CallsToMeasureTheRate = 6;

    private static readonly TimeSpan FastEmailPoll = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>Onde o teste está, para a falha por teto dizer onde parou. Escrito na tarefa do corpo e lido na do teto.</summary>
    private volatile string _step = "corpo do teste";

    /// <summary>Falha do corpo, guardada antes do descarte para não se perder se o descarte travar depois dela.</summary>
    private volatile BodyFailure? _bodyFailure;

    /// <summary>Descartes dos hosts do teste, do último criado para o primeiro.</summary>
    private readonly Stack<Func<ValueTask>> _hostDisposals = new();

    [Fact]
    public Task AStalledSessionStartThroughTheRealSdkIsCutAndRetriedWithoutReleasingTheAccount() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var transport = new SyntheticAwsTransport(Stall);
        var host = RealSdkHost(connection, transport);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);

        await CutDispatchAsync(host, () => transport.Calls == 1);

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
    });

    [Fact]
    public Task AStalledResultReadIsCutAndOnlyTheNextAnswerDecides() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        provider.ReadFault = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken);

        await CutDispatchAsync(host, () => provider.ReadCalls == 1);

        Assert.Equal("Capturing|has-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        Assert.Equal("Pending|ProviderUnavailable|2", await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = null;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
        Assert.True(await SelfieConcurrencyHarness.EligibleAsync(host, account.UserId, Ct));
    });

    /// <summary>
    /// O corte exato, no relógio do worker: em 9 999 ms a chamada ainda vale e a resposta decide; em 10 000 ms ela já foi
    /// cortada, e a resposta que chega depois não decide nem libera. Vale para o fornecedor que respeita o cancelamento
    /// e para o que o ignora: o corte não depende da cooperação dele.
    /// </summary>
    [Theory]
    [InlineData(9_999, true, true)]
    [InlineData(9_999, false, true)]
    [InlineData(10_000, true, false)]
    [InlineData(10_000, false, false)]
    public Task TheCallIsCutExactlyAtTenSecondsOnTheWorkerClock(int answerAfterMilliseconds, bool honorsCancellation,
        bool decides) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ReadFault = cancellationToken => honorsCancellation ? answer.Task.WaitAsync(cancellationToken) : answer.Task;
        var batch = SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        await WaitUntilAsync(() => provider.ReadCalls == 1, batch);

        _time.Advance(TimeSpan.FromMilliseconds(answerAfterMilliseconds));
        answer.TrySetResult();
        await batch.WaitAsync(CallCeiling, Ct);
        await WaitUntilAsync(() => provider.ReadsFinished == 1);

        Assert.Equal(decides ? "Passed|has-reference|no-feedback|closed" : "Capturing|has-reference|PROVIDER_UNAVAILABLE|open",
            await AttemptAsync(scenario));
        Assert.Equal(decides ? "Delivered|-|2" : "Pending|ProviderUnavailable|2", await DeliveryAsync(scenario));
        Assert.Equal(decides, await SelfieConcurrencyHarness.EligibleAsync(host, account.UserId, Ct));
        Assert.Equal(decides ? "Active" : "PendingEligibility", await scenario.ScalarTextAsync(
            "select status from identity.users where id = $1", account.UserId));
    });

    /// <summary>
    /// A resposta que volta já concluída depois do prazo não decide: o <c>WaitAsync</c> devolve uma tarefa pronta mesmo com
    /// prazo zero. Em 10 000 ms ela é tratada como o corte: volta para a fila, não conta contra a pessoa e não mexe na conta.
    /// </summary>
    [Theory]
    [InlineData(9_999, SelfieState.Passed, "Passed|has-reference|no-feedback|closed", "Delivered|-|2", true, 0)]
    [InlineData(9_999, SelfieState.RecaptureRequired, "RecaptureRequired|has-reference|CAPTURE_QUALITY|closed", "Delivered|-|2", false, 1)]
    [InlineData(10_000, SelfieState.Passed, "Capturing|has-reference|PROVIDER_UNAVAILABLE|open", "Pending|ProviderUnavailable|2", false, 0)]
    [InlineData(10_000, SelfieState.RecaptureRequired, "Capturing|has-reference|PROVIDER_UNAVAILABLE|open", "Pending|ProviderUnavailable|2", false, 0)]
    public Task ASynchronousAnswerDecidesOnlyBeforeTheDeadline(int answerAfterMilliseconds, SelfieState answer,
        string expectedAttempt, string expectedDelivery, bool released, int expectedFailures) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { Answer = answer };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        provider.ReadFault = _ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(answerAfterMilliseconds));
            return Task.CompletedTask;
        };

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(expectedAttempt, await AttemptAsync(scenario));
        Assert.Equal(expectedDelivery, await DeliveryAsync(scenario));
        Assert.Equal(released, await SelfieConcurrencyHarness.EligibleAsync(host, account.UserId, Ct));
        Assert.Equal(released ? "Active" : "PendingEligibility", await scenario.ScalarTextAsync(
            "select status from identity.users where id = $1", account.UserId));
        Assert.Equal(expectedFailures, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    });

    /// <summary>
    /// A abertura de sessão segue a mesma regra da leitura: em 10 000 ms a sessão que volta já concluída é tratada como o
    /// corte e não chega a ser gravada, então a tentativa continua sem referência e só ganha o sinal.
    /// </summary>
    [Theory]
    [InlineData(9_999, "Capturing|has-reference|no-feedback|open", "Pending|AwaitingCapture|1")]
    [InlineData(10_000, "Pending|no-reference|PROVIDER_UNAVAILABLE|open", "Pending|ProviderUnavailable|1")]
    public Task ASynchronouslyStartedSessionIsAppliedOnlyBeforeTheDeadline(int answerAfterMilliseconds, string expectedAttempt,
        string expectedDelivery) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        provider.StartFault = _ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(answerAfterMilliseconds));
            return Task.CompletedTask;
        };

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(expectedAttempt, await AttemptAsync(scenario));
        Assert.Equal(expectedDelivery, await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);
    });

    /// <summary>
    /// Uma chamada por vez ao fornecedor: a chamada cortada que ignora o cancelamento segura a vaga até terminar de fato.
    /// Enquanto ela roda, nenhum lote deste worker chama o fornecedor nem toma entrega, e o aviso sai uma vez só. Quando
    /// ela termina, o lote seguinte volta a chamar.
    /// </summary>
    [Fact]
    public Task NoSecondCallLeavesWhileTheAbandonedCallIsStillRunning() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var logs = new CapturingLoggerProvider();
        var host = FakeProviderHost(connection, provider, logs: logs);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StartFault = _ => release.Task;
        await CutDispatchAsync(host, () => provider.StartCalls == 1);
        _time.Advance(UntilNextTry);

        Assert.Equal(0, await SelfieConcurrencyHarness.DispatchAsync(host, Ct));
        Assert.Equal(0, await SelfieConcurrencyHarness.DispatchAsync(host, Ct));

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal("Pending|ProviderUnavailable|1", await DeliveryAsync(scenario));
        var refusal = Assert.Single(logs.Entries, entry => entry.Category == typeof(SelfieVerificationDispatcher).FullName
            && entry.Text.Contains("still running", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, refusal.Level);

        release.TrySetResult();
        await WaitUntilAsync(() => provider.StartsFinished == 1);
        provider.StartFault = null;

        Assert.Equal(1, await SelfieConcurrencyHarness.DispatchAsync(host, Ct));
        Assert.Equal(2, provider.StartCalls);
        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
    });

    /// <summary>
    /// A resposta que a chamada abandonada entrega depois do corte não é lida por ninguém: a tentativa e a conta ficam
    /// como o corte deixou, e só a leitura seguinte decide.
    /// </summary>
    [Fact]
    public Task TheLateAnswerOfAnAbandonedCallIsNeverApplied() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ReadFault = _ => release.Task;
        await CutDispatchAsync(host, () => provider.ReadCalls == 1);
        var afterTheCut = await VersionAsync(scenario);

        release.TrySetResult();
        await WaitUntilAsync(() => provider.ReadsFinished == 1);

        Assert.Equal("Capturing|has-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        Assert.Equal(afterTheCut, await VersionAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        provider.ReadFault = null;
        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(2, provider.ReadCalls);
        Assert.Equal("Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
    });

    /// <summary>
    /// Fornecedor lento de verdade costuma mandar os cabeçalhos e travar no corpo. Pelo SDK real, o corpo parado é
    /// cortado pelos mesmos 10 s do relógio do worker: a entrega volta para a fila, a tentativa só ganha o sinal e a conta
    /// fica pendente. O corte só vem com a leitura presa num corpo ainda aberto: um corpo que chegasse já descartado
    /// terminaria sozinho e passaria por corte.
    /// </summary>
    [Fact]
    public Task AResponseBodyThatStallsThroughTheRealSdkIsCutAtTheCallTimeout()
    {
        var stalledBodies = new ConcurrentQueue<StalledStream>();
        var transport = new SyntheticAwsTransport(_ => StalledBody(stalledBodies));
        return WithinTheWholeTestCeilingAsync(() => $"chamadas HTTP: {transport.Calls}; {string.Join(" / ", stalledBodies.Select(body => body.State))}", async () =>
        {
            var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
            var host = RealSdkHost(connection, transport);
            var scenario = new IdentityScenario(connection, host);
            var account = await scenario.CreateConfirmedAccountAsync();
            await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);

            await CutDispatchAsync(host, () => transport.Calls == 1 && stalledBodies.TryPeek(out var body) && body.IsStalled);

            Assert.Equal(1, transport.Calls);
            Assert.Single(stalledBodies);
            Assert.Equal("Pending|no-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
            Assert.Equal("Pending|ProviderUnavailable|1", await DeliveryAsync(scenario));
            await AssertNotReleasedAsync(scenario, host, account.UserId);
        });
    }

    /// <summary>
    /// O SDK real lê o corpo de forma síncrona. Se tudo antes da leitura completar sem ceder a thread, a chamada prende
    /// quem disparou o lote, e ninguém avança o relógio até o corte que a soltaria. A trava aqui só cede ao corte do
    /// relógio do teste, como o corpo parado só cede ao descarte que o corte provoca.
    /// </summary>
    [Fact]
    public Task ABatchThatBlocksSynchronouslyUntilTheCutStillReachesTheCut() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var blocked = false;
        var releasedByTheCut = false;

        await CutAsync(() =>
        {
            using var cut = new CancellationTokenSource(CallTimeout, _time);
            Volatile.Write(ref blocked, true);
            releasedByTheCut = cut.Token.WaitHandle.WaitOne(CallCeiling);
            return Task.CompletedTask;
        }, () => Volatile.Read(ref blocked));

        Assert.True(releasedByTheCut, "A trava síncrona só terminou pelo teto: o lote prendeu a thread que avança o relógio.");
    });

    [Fact]
    public async Task TheCeilingNamesTheHostDisposalWhenItHangsAfterAPassingBody()
    {
        var failure = await CeilingFailureDuringAHangingDisposalAsync(() => Task.CompletedTask);

        Assert.Contains($"etapa \"{HostDisposalStep}\"", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCeilingNamesTheHostDisposalAndKeepsTheBodyFailureWhenItHangsAfterAFailingBody()
    {
        var failure = await CeilingFailureDuringAHangingDisposalAsync(
            () => Task.FromException(new InvalidOperationException("falha-do-corpo")));

        Assert.Contains($"etapa \"{HostDisposalStep}\"", failure.Message, StringComparison.Ordinal);
        Assert.Contains("falha-do-corpo", failure.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task AnInnerCeilingKeepsTheStepWhereTheBodyStoppedEvenAfterTheDisposal()
    {
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => WithinTheWholeTestCeilingAsync(() =>
        {
            _step = "etapa-do-corpo";
            return Task.FromException(new TimeoutException("teto interno"));
        }));

        Assert.Contains("etapa \"etapa-do-corpo\"", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HostDisposalStep, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tentativa dura 3 minutos e o retry vem a cada 6 s no relógio do teste. Sem sessão, a última chamada é a de 2:30,
    /// e a entrega seguinte encerra: 26 inícios em 27 entregas. Com sessão, a leitura vai até o fim do prazo.
    /// </summary>
    [Theory]
    [InlineData(true, 26, 0, 27)]
    [InlineData(false, 1, 29, 31)]
    public Task AProviderThatStaysDownEndsTheAttemptExpiredWithoutCountingIt(bool startFails, int expectedStarts, int expectedReads, int expectedDeliveries) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
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
    });

    /// <summary>Em 2:31 restam 29 s: abrir a sessão daria à pessoa um Capturing que ela não tem tempo de usar.</summary>
    [Fact]
    public Task ASessionIsNotOpenedWithoutTimeLeftToCapture() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var logs = new CapturingLoggerProvider();
        var host = FakeProviderHost(connection, provider, logs: logs);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        var attemptId = (await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct)).Id!.Value;
        _time.Advance(TimeSpan.FromSeconds(151));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        var trace = Assert.Single(logs.Entries, entry => entry.Category == typeof(SelfieVerificationDispatcher).FullName);
        Assert.Equal(LogLevel.Information, trace.Level);
        Assert.Contains("too little time", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(account.UserId.ToString(), trace.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attemptId.ToString(), trace.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(trace.Exception);
        Assert.Equal(0, provider.StartCalls);
        Assert.Equal("Expired|no-reference|PROVIDER_UNAVAILABLE|closed", await AttemptAsync(scenario));
        Assert.Equal("Expired|-|1", await DeliveryAsync(scenario));
        var status = await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct);
        Assert.Equal(("Recapture", new SelfieAttemptStanding.Available(5, LastCycle: false)), (status.NextStep, status.Attempts));
        await AssertNotReleasedAsync(scenario, host, account.UserId);

        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
    });

    [Fact]
    public Task ASessionIsStillOpenedAtTheExactMinimumWindow() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        _time.Advance(TimeSpan.FromSeconds(150));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal("Capturing|has-reference|no-feedback|open", await AttemptAsync(scenario));
    });

    /// <summary>O corte vale só para abrir sessão: quem já capturou ainda precisa ter o resultado lido no fim do prazo.</summary>
    [Fact]
    public Task AnOpenSessionIsStillReadWithLessThanTheMinimumWindow() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(TimeSpan.FromSeconds(170));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(1, provider.ReadCalls);
        Assert.Equal("Passed|has-reference|no-feedback|closed", await AttemptAsync(scenario));
    });

    /// <summary>Queda em cada ponto do fluxo: a API distingue a instabilidade sem mudar o passo seguinte, e a volta limpa o sinal.</summary>
    [Theory]
    [InlineData(true, "Pending|no-reference", "Wait")]
    [InlineData(false, "Capturing|has-reference", "Capture")]
    public Task AnOutageIsSignaledOnTheOpenAttemptAndTheRecoveryClearsIt(bool startFails, string attempt, string nextStep) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { Answer = SelfieState.Processing };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
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
    });

    [Fact]
    public Task AnOutageReachesTheOwnerThroughTheApiWithoutTheProviderMessage() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { StartFault = _ => throw TransportFailure("IOException") };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
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
    });

    /// <summary>A cada 5 s a queda repetiria a escrita: só a primeira falha da sequência grava.</summary>
    [Fact]
    public Task RepeatedFailuresWriteTheSignalOnce() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { StartFault = Unavailable };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        var afterFirstFailure = await VersionAsync(scenario);

        _time.Advance(UntilNextTry);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(2, provider.StartCalls);
        Assert.Equal(afterFirstFailure, await VersionAsync(scenario));
    });

    /// <summary>
    /// A tentativa muda entre a leitura e a falha, com o lease ainda válido: o sinal não pisa na mudança, e a coluna que a
    /// consulta altera é a única prova de cada filtro.
    /// </summary>
    [Theory]
    [InlineData("version = version + 1", "Capturing|has-reference|no-feedback|open")]
    [InlineData("state = 'Expired', completed_at = expires_at", "Expired|has-reference|no-feedback|closed")]
    public Task AnAttemptChangedDuringTheCallDoesNotReceiveTheSignal(string change, string expected) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilNextTry);
        provider.ReadFault = async cancellationToken =>
        {
            await scenario.ScalarAsync($"update identity.selfie_verifications set {change} returning 1");
            await Unavailable(cancellationToken);
        };

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(1, provider.ReadCalls);
        Assert.Equal(expected, await AttemptAsync(scenario));
    });

    /// <summary>Worker que perdeu o lease não escreve na tentativa: quem assumiu a entrega é que decide.</summary>
    [Fact]
    public Task AFailureAfterTheLeaseExpiredDoesNotSignal() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
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
    });

    [Theory]
    [InlineData(SelfieState.Passed, 0)]
    [InlineData(SelfieState.RecaptureRequired, 1)]
    public Task AnAnswerAfterTheLeaseExpiredIsDiscardedAndReadAgainOnce(SelfieState answer, int expectedFailures) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { Answer = answer };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
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
    });

    [Theory]
    [InlineData(true, "IOException")]
    [InlineData(true, "TimeoutException")]
    [InlineData(false, "IOException")]
    [InlineData(false, "TimeoutException")]
    public Task ATransportFailureIsRequeuedKeepingTheAttemptAndCountingNothing(bool startFails, string failure) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var logs = new CapturingLoggerProvider();
        var host = FakeProviderHost(connection, provider, logs: logs);
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
    });

    /// <summary>
    /// Pelo SDK real no <see cref="WorkerHost"/> real, com relógio real: o reset de conexão chega como
    /// <see cref="IOException"/> e o timeout do <see cref="HttpClient"/> como <see cref="TimeoutException"/>. O worker
    /// segue vivo e a mesma tentativa é retomada 5 s depois.
    /// </summary>
    [Theory]
    [InlineData("IOException")]
    [InlineData("TimeoutException")]
    public Task ATransportFailureDoesNotStopTheWorkerAndTheAttemptIsRetried(string failure) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var transport = new SyntheticAwsTransport(failure == "IOException" ? ConnectionReset : HttpClientTimeout);
        var web = RealSdkHost(connection, transport);
        var scenario = new IdentityScenario(connection, web);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        var workerHost = BuildWorker(connection, services => services.AddSingleton<HttpClientFactory>(transport));
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
    });

    /// <summary>
    /// Erro de programação no fornecedor não entra na lista de transitórias: não reagenda, vira log de erro com o tipo,
    /// e a fila segue. A entrega fica presa só até o lease vencer, e o teto de tentativas a encerra.
    /// </summary>
    [Fact]
    public Task AnUnexpectedProviderErrorIsLoggedWithoutStoppingTheQueue() => WithinTheWholeTestCeilingAsync(async () =>
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
        var web = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, web);
        var first = await scenario.CreateConfirmedAccountAsync();
        var second = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, first.UserId, Ct);
        await SelfieConcurrencyHarness.RequestAsync(web, second.UserId, Ct);
        var logs = new CapturingLoggerProvider();
        var workerHost = BuildWorker(connection, services =>
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
    });

    /// <summary>Parar o worker com a chamada em voo é rotina: nem erro no log, nem falha do fornecedor no rastro.</summary>
    [Fact]
    public Task StoppingTheWorkerDuringAProviderCallLogsNoErrorAndNoProviderFailure() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StartFault = cancellationToken =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var web = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, web);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        var logs = new CapturingLoggerProvider();
        var workerHost = BuildWorker(connection, services =>
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
    });

    /// <summary>
    /// Com o fornecedor travado, a fila anda em série: cada tentativa aberta recebe uma chamada, na ordem do pedido, e
    /// nenhuma repete antes das outras. Quando ele volta, cada tentativa é servida na primeira chamada.
    /// </summary>
    [Fact]
    public Task AStalledProviderReachesEveryOpenAttemptInOrderAndTheFirstCallAfterRecoveryIsServed() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var requested = new List<Guid>();
        for (var attempt = 0; attempt < AttemptsInTheBacklog; attempt++)
        {
            var account = await scenario.CreateConfirmedAccountAsync();
            await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
            requested.Add((await SelfieConcurrencyHarness.CurrentAsync(host, account.UserId, Ct)).Id!.Value);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        provider.StartFault = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken);

        for (var attempt = 1; attempt <= AttemptsInTheBacklog; attempt++)
        {
            var expectedCalls = attempt;
            await CutDispatchAsync(host, () => provider.StartCalls == expectedCalls);
        }

        Assert.Equal(requested, provider.StartedAttempts);
        Assert.Equal(AttemptsInTheBacklog, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_verifications where state = 'Pending' and completed_at is null"));

        provider.StartFault = null;
        _time.Advance(UntilNextTry);
        for (var attempt = 0; attempt < AttemptsInTheBacklog; attempt++)
        {
            Assert.Equal(1, await SelfieConcurrencyHarness.DispatchAsync(host, Ct));
        }

        Assert.Equal(requested.Concat(requested), provider.StartedAttempts);
        Assert.Equal(AttemptsInTheBacklog, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_verifications where state = 'Capturing' and completed_at is null"));
    });

    /// <summary>
    /// Numa tentativa que já tem referência, o prazo de 3 minutos é o único limite em falha (a janela mínima de captura
    /// só encerra antes a tentativa sem referência): depois de uma sequência de falhas, a leitura no último segundo ainda
    /// decide, e no instante do prazo a tentativa fecha sem nenhuma chamada, mantendo o sinal de
    /// instabilidade que a primeira falha gravou.
    /// </summary>
    [Theory]
    [InlineData(179, 1, "Passed|has-reference|no-feedback|closed")]
    [InlineData(180, 0, "Expired|has-reference|PROVIDER_UNAVAILABLE|closed")]
    public Task AFailureStreakLeavesTheAttemptReadableUpToItsLastSecond(int secondsAfterTheRequest, int expectedReads, string expected) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        var requestedAt = _time.GetUtcNow();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        provider.ReadFault = Unavailable;
        for (var failure = 0; failure < FailuresBeforeTheLastRead; failure++)
        {
            _time.Advance(UntilNextTry);
            await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        }

        Assert.Equal(FailuresBeforeTheLastRead, provider.ReadCalls);
        provider.ReadFault = null;
        _time.SetUtcNow(requestedAt.AddSeconds(secondsAfterTheRequest));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(FailuresBeforeTheLastRead + expectedReads, provider.ReadCalls);
        Assert.Equal(expected, await AttemptAsync(scenario));
        Assert.Equal(0, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    });

    [Fact]
    public Task AfterAnOutageTheExpiredAttemptCostsNoCallAndTheLiveOneIsServed() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { StartFault = Unavailable };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var host = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, host);
        var older = await scenario.CreateConfirmedAccountAsync();
        var newer = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, older.UserId, Ct);
        var olderAttempt = (await SelfieConcurrencyHarness.CurrentAsync(host, older.UserId, Ct)).Id!.Value;
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(TimeSpan.FromSeconds(170));
        await SelfieConcurrencyHarness.RequestAsync(host, newer.UserId, Ct);
        var newerAttempt = (await SelfieConcurrencyHarness.CurrentAsync(host, newer.UserId, Ct)).Id!.Value;
        provider.StartFault = null;
        _time.Advance(TimeSpan.FromSeconds(11));

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(2, provider.StartCalls);
        Assert.Equal(newerAttempt, provider.StartedAttempts.Last());
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_verifications where id = $1 and state = 'Expired' and completed_at is not null",
            olderAttempt));
        Assert.Equal("Capturing", (await SelfieConcurrencyHarness.CurrentAsync(host, newer.UserId, Ct)).State);
        await AssertNotReleasedAsync(scenario, host, older.UserId);
    });

    /// <summary>
    /// Pelo SDK real: erro do serviço reagenda a entrega e só grava na tentativa o sinal de instabilidade, sem mudar o
    /// estado. O SDK repete 5xx e throttling até 3 chamadas HTTP,
    /// não repete <c>AccessDenied</c>, fica dentro do corte de 10 s, e nenhum log leva a mensagem do fornecedor. Os
    /// números prendem a política de retry do SDK: uma atualização que a mude quebra aqui de propósito.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableException", 3)]
    [InlineData(HttpStatusCode.InternalServerError, "InternalServerError", 3)]
    [InlineData(HttpStatusCode.BadRequest, "ThrottlingException", 3)]
    [InlineData(HttpStatusCode.BadRequest, "ProvisionedThroughputExceededException", 3)]
    [InlineData(HttpStatusCode.BadRequest, "AccessDeniedException", 1)]
    public Task AServiceErrorThroughTheRealSdkIsRequeuedWithinTheCallTimeout(HttpStatusCode status, string error, int expectedHttpCalls) => WithinTheWholeTestCeilingAsync(async () =>
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var transport = new SyntheticAwsTransport(_ => ServiceError(status, error));
        var logs = new CapturingLoggerProvider();
        var host = RealSdkHost(connection, transport, logs);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);

        var elapsed = await TimedDispatchAsync(host);

        Assert.True(elapsed < BelowTheCallTimeout, $"O lote levou {elapsed}.");
        Assert.Equal(expectedHttpCalls, transport.Calls);
        Assert.Equal("Pending|no-reference|PROVIDER_UNAVAILABLE|open", await AttemptAsync(scenario));
        Assert.Equal("Pending|ProviderUnavailable|1", await DeliveryAsync(scenario));
        await AssertNotReleasedAsync(scenario, host, account.UserId);
        var trace = Assert.Single(logs.Entries, entry => entry.Category == typeof(SelfieVerificationDispatcher).FullName);
        Assert.Null(trace.Exception);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(TransportCanary, StringComparison.Ordinal)
            || entry.Exception?.ToString().Contains(TransportCanary, StringComparison.Ordinal) == true);
    });

    /// <summary>
    /// Por que este desenho já não martela o fornecedor: uma chamada por vez, com 1 s de pausa entre lotes, mesmo com
    /// mais tentativas abertas do que o reagendamento de 5 s consegue espaçar.
    /// </summary>
    [Fact]
    public Task TheWorkerCallsAFailingProviderAtMostOncePerSecond() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time) { StartFault = Unavailable };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var web = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, web);
        for (var attempt = 0; attempt < AttemptsAboveTheRetrySpacing; attempt++)
        {
            var account = await scenario.CreateConfirmedAccountAsync();
            await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        }

        var workerHost = BuildWorker(connection, services =>
        {
            services.RemoveAll<ISelfieProvider>();
            services.AddSingleton<ISelfieProvider>(provider);
        });
        var lifetime = workerHost.Services.GetRequiredService<IHostApplicationLifetime>();

        await workerHost.StartAsync(Ct);
        await WaitWhileRunningAsync(lifetime, () => Task.FromResult(provider.StartCalls >= CallsToMeasureTheRate));
        await workerHost.StopAsync(Ct);

        var calls = provider.StartTimestamps.ToArray();
        var elapsed = Stopwatch.GetElapsedTime(calls[0], calls[^1]);
        Assert.True(elapsed >= TimeSpan.FromSeconds(calls.Length - 1), $"{calls.Length} chamadas em {elapsed}.");
    });

    /// <summary>
    /// A fila da selfie tem laço próprio: o e-mail sai enquanto a chamada ao fornecedor segue presa. A chamada ignora o
    /// cancelamento e só o teste a libera; o e-mail sai em frações de segundo, bem antes do corte de 10 s do worker.
    /// </summary>
    [Fact]
    public Task AStalledSelfieCallDoesNotHoldTheEmailQueue() => WithinTheWholeTestCeilingAsync(async () =>
    {
        var provider = new FaultySelfieProvider(_time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StartFault = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var web = FakeProviderHost(connection, provider);
        var scenario = new IdentityScenario(connection, web);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(web, account.UserId, Ct);
        var workerHost = BuildWorker(connection, services =>
        {
            services.RemoveAll<ISelfieProvider>();
            services.AddSingleton<ISelfieProvider>(provider);
            services.PostConfigure<OutboxDispatchOptions>(options => options.PollInterval = FastEmailPoll);
        });
        var lifetime = workerHost.Services.GetRequiredService<IHostApplicationLifetime>();

        await workerHost.StartAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(CallCeiling, Ct);
            await WaitWhileRunningAsync(lifetime, async () => await EmailAsync(scenario, "<>") == 0);
            var delivered = await EmailAsync(scenario, "=");
            using (var browser = new Browser(web))
            using (var registered = await IdentityScenario.RegisterAsync(browser, IdentityScenario.NewEmail()))
            {
                Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            }

            await WaitWhileRunningAsync(lifetime, async () => await EmailAsync(scenario, "=") > delivered);

            Assert.Equal("InFlight|-|1", await DeliveryAsync(scenario));
            Assert.Equal(1, provider.StartCalls);
        }
        finally
        {
            release.TrySetResult();
        }

        await workerHost.StopAsync(Ct);
    });

    private static Task<long> EmailAsync(IdentityScenario scenario, string comparison) => scenario.ScalarAsync(
        $"select count(*) from ops.outbox_deliveries where consumer = 'transactional-email' and status {comparison} 'Delivered'");

    private static Task<HttpResponseMessage> Stall(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => new HttpResponseMessage(), TaskScheduler.Default);

    /// <summary>Cada resposta ganha um corpo novo: nenhuma chamada, de teste nenhum, herda o descarte de outra.</summary>
    private static Task<HttpResponseMessage> StalledBody(ConcurrentQueue<StalledStream> bodies)
    {
        var body = new StalledStream();
        bodies.Enqueue(body);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.1");
        return Task.FromResult(response);
    }

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

    private static Task<HttpResponseMessage> ServiceError(HttpStatusCode status, string error)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent($$"""{"__type":"{{error}}","Message":"{{TransportCanary}}"}""", Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.1");
        return Task.FromResult(response);
    }

    private DuoraWebFactory RealSdkHost(string connection, SyntheticAwsTransport transport, CapturingLoggerProvider? logs = null) =>
        DisposedAtTheEnd(new DuoraWebFactory(connection, logs)
        {
            Time = _time,
            Settings = SelfieTestSettings.Enabled(ScriptedSelfieProvider.Policy),
            TestServices = services => services.AddSingleton<HttpClientFactory>(transport),
        });

    private DuoraWebFactory FakeProviderHost(string connection, ISelfieProvider provider, CapturingLoggerProvider? logs = null) =>
        DisposedAtTheEnd(SelfieConcurrencyHarness.Host(connection, _time, provider, logs: logs));

    private DuoraWebFactory DisposedAtTheEnd(DuoraWebFactory host)
    {
        _hostDisposals.Push(host.DisposeAsync);
        return host;
    }

    private IHost BuildWorker(string connection, Action<IServiceCollection> services, CapturingLoggerProvider? logs = null)
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

        var worker = builder.Build();
        _hostDisposals.Push(() =>
        {
            worker.Dispose();
            return ValueTask.CompletedTask;
        });
        return worker;
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

    /// <summary>
    /// Lote cuja chamada fica presa: espera a chamada chegar ao fornecedor, leva o relógio do teste até o corte e devolve
    /// o lote encerrado. Falha em vez de pendurar a suíte se o corte não vier. O lote começa em outra thread: o SDK real lê
    /// o corpo de forma síncrona, e quando tudo antes da leitura completava sem ceder a thread, a chamada prendia esta,
    /// que é a única que avança o relógio até o corte.
    /// </summary>
    private Task CutDispatchAsync(DuoraWebFactory host, Func<bool> called) =>
        CutAsync(() => SelfieConcurrencyHarness.DispatchAsync(host, Ct), called);

    private async Task CutAsync(Func<Task> dispatch, Func<bool> called)
    {
        _step = "esperando a chamada chegar ao fornecedor";
        var batch = Task.Run(dispatch, Ct);
        await WaitUntilAsync(called, batch);
        _step = "avançando o relógio até o corte, com os timers disparando nesta thread";
        _time.Advance(CallTimeout);
        _step = "esperando o lote encerrar depois do corte";
        await batch.WaitAsync(CallCeiling, Ct);
        _step = "seguindo depois do corte";
    }

    /// <summary>
    /// Limita o teste inteiro. Os tetos internos só agem sobre tarefas já devolvidas: um bloqueio síncrono no próprio
    /// caminho do teste, como um timer dentro do <c>Advance</c> ou a parada do host no descarte, só é alcançado rodando o
    /// corpo em outra tarefa. Ao estourar qualquer teto, a falha traz a etapa e o estado de <paramref name="state"/>.
    /// </summary>
    private Task WithinTheWholeTestCeilingAsync(Func<string> state, Func<Task> body) =>
        WithinCeilingAsync(running => running.WaitAsync(WholeTestCeiling, Ct), state, body);

    private Task WithinTheWholeTestCeilingAsync(Func<Task> body) => WithinTheWholeTestCeilingAsync(NoExtraState, body);

    private static string NoExtraState() => "sem contadores deste teste";

    private async Task WithinCeilingAsync(Func<Task, Task> waitForCeiling, Func<string> state, Func<Task> body)
    {
        var running = Task.Run(() => RunThenDisposeHostsAsync(body), Ct);
        try
        {
            await waitForCeiling(running);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException && !Ct.IsCancellationRequested)
        {
            throw CeilingFailure(running.IsCompleted, state(), exception);
        }
    }

    /// <summary>
    /// O descarte fica aqui, e não num <c>await using</c> do corpo, para a etapa valer em toda saída: corpo verde, assert
    /// que falhou ou teto interno que estourou.
    /// </summary>
    private async Task RunThenDisposeHostsAsync(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception failure)
        {
            _bodyFailure = new BodyFailure(_step, failure);
            throw;
        }
        finally
        {
            _step = HostDisposalStep;
            while (_hostDisposals.TryPop(out var dispose))
            {
                await dispose();
            }
        }
    }

    private TimeoutException CeilingFailure(bool finished, string state, Exception exception)
    {
        var failure = _bodyFailure;
        if (finished)
        {
            return new TimeoutException($"Estourou um teto interno na etapa \"{failure?.Step ?? _step}\" ({state}).", exception);
        }

        var earlier = failure is null
            ? string.Empty
            : $"; antes o corpo falhou na etapa \"{failure.Step}\" com {failure.Exception.GetType().Name}: {failure.Exception.Message}";
        return new TimeoutException(
            $"Estourou o teto de {WholeTestCeiling.TotalSeconds:F0} s do teste na etapa \"{_step}\" ({state}){earlier}.",
            failure?.Exception ?? exception);
    }

    /// <summary>Roda o teto de verdade, só que disparado no instante em que o descarte, que nunca termina, começa.</summary>
    private async Task<TimeoutException> CeilingFailureDuringAHangingDisposalAsync(Func<Task> body)
    {
        var disposalReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostDisposals.Push(() =>
        {
            disposalReached.TrySetResult();
            return new ValueTask(new TaskCompletionSource().Task);
        });

        return await Assert.ThrowsAsync<TimeoutException>(() => WithinCeilingAsync(
            async _ =>
            {
                await disposalReached.Task.WaitAsync(CallCeiling, Ct);
                throw new TimeoutException("Teto simulado.");
            },
            NoExtraState,
            body));
    }

    /// <summary>Espera, em tempo real e com teto, o que acontece em outra thread; falha na hora se o lote acabou antes.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, Task? batch = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(CallCeiling);
        while (!condition())
        {
            if (batch is { IsCompleted: true })
            {
                await batch;
                Assert.Fail("O lote terminou antes de a chamada chegar ao fornecedor.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
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

    private sealed record BodyFailure(string Step, Exception Exception);

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

    /// <summary>
    /// Corpo de resposta que nunca entrega um byte, como um socket parado: a leitura assíncrona só termina pelo
    /// cancelamento, e a síncrona só quando o corpo é descartado ou, sem descarte, pelo teto que solta a thread.
    /// </summary>
    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _syncReadStarted;
        private volatile bool _asyncReadStarted;

        public bool IsStalled => (_syncReadStarted || _asyncReadStarted) && !_closed.Task.IsCompleted;

        public string State =>
            $"leitura síncrona iniciada: {_syncReadStarted}; assíncrona iniciada: {_asyncReadStarted}; corpo descartado: {_closed.Task.IsCompleted}";

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _syncReadStarted = true;
            if (!_closed.Task.Wait(StalledReadCeiling))
            {
                throw new TimeoutException("O corpo parado não foi descartado dentro do teto.");
            }

            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _asyncReadStarted = true;
            await _closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    /// <summary>Fornecedor sintético cuja falha ou demora roda antes de cada resposta e pode ser trocada entre lotes.</summary>
    private sealed class FaultySelfieProvider(FakeTimeProvider time) : ISelfieProvider
    {
        private readonly ConcurrentQueue<Guid> _startedAttempts = new();
        private readonly ConcurrentQueue<long> _startTimestamps = new();
        private int _startCalls;
        private int _readCalls;
        private int _startsFinished;
        private int _readsFinished;

        public Func<CancellationToken, Task>? StartFault { get; set; }

        public Func<CancellationToken, Task>? ReadFault { get; set; }

        public SelfieState Answer { get; init; } = SelfieState.Passed;

        public int StartCalls => Volatile.Read(ref _startCalls);

        public IReadOnlyCollection<Guid> StartedAttempts => _startedAttempts;

        public IReadOnlyCollection<long> StartTimestamps => _startTimestamps;

        public int ReadCalls => Volatile.Read(ref _readCalls);

        /// <summary>
        /// Chamadas com a tarefa já concluída, inclusive as que o dispatcher abandonou no corte. Conta depois da conclusão,
        /// para o teste não pedir o lote seguinte enquanto a vaga ainda vê a tarefa em andamento.
        /// </summary>
        public int StartsFinished => Volatile.Read(ref _startsFinished);

        public int ReadsFinished => Volatile.Read(ref _readsFinished);

        public Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
        {
            var call = RunStartAsync(attemptId, cancellationToken);
            _ = call.ContinueWith(_ => Interlocked.Increment(ref _startsFinished), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return call;
        }

        public Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
        {
            var call = RunReadAsync(cancellationToken);
            _ = call.ContinueWith(_ => Interlocked.Increment(ref _readsFinished), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return call;
        }

        private async Task<SelfieSession> RunStartAsync(Guid attemptId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCalls);
            _startedAttempts.Enqueue(attemptId);
            _startTimestamps.Enqueue(Stopwatch.GetTimestamp());
            await (StartFault?.Invoke(cancellationToken) ?? Task.CompletedTask);
            return new SelfieSession(attemptId.ToString("D"), time.GetUtcNow().AddMinutes(3));
        }

        private async Task<SelfieResult> RunReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCalls);
            await (ReadFault?.Invoke(cancellationToken) ?? Task.CompletedTask);
            return new SelfieResult(Answer, Answer is SelfieState.Passed or SelfieState.Processing ? null : "CAPTURE_QUALITY", ScriptedSelfieProvider.Policy);
        }
    }
}
