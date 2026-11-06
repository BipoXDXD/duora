using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// Consumidor da outbox de e-mail (spec 10.6) contra PostgreSQL real: entrega única, retry com backoff,
/// concorrência entre workers, fencing do lease, deadline e tentativas esgotadas.
/// </summary>
public sealed class TransactionalEmailDispatchTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PendingEmailIsWrittenOnceAndMarkedDelivered()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        var dispatcher = harness.StartWorker();

        var claimed = await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal(1, claimed);
        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Delivered", delivery.Status);
        Assert.Equal(1, delivery.Attempts);
        var file = Assert.Single(harness.MailFiles());
        Assert.Contains(
            $"{EmailOutboxHarness.PublicOrigin}/confirm-email#userId=",
            await File.ReadAllTextAsync(file, Ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliveredEventPayloadIsScrubbed()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "tok-1", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("{}", delivery.Payload);
    }

    [Fact]
    public async Task FailedSendIsRetriedAfterBackoffAndDeliveredOnce()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        var dispatcher = harness.StartWorker(decorateSender: real => new FailingSender(real, failures: 1));

        var failedAt = harness.Time.GetUtcNow();
        await dispatcher.DispatchBatchAsync(Ct);
        var afterFailure = await harness.ReadDeliveryAsync(deliveryId, Ct);
        var claimedBeforeBackoff = await dispatcher.DispatchBatchAsync(Ct);
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Pending", afterFailure.Status);
        Assert.Equal("IOException", afterFailure.LastError);
        Assert.InRange(afterFailure.AvailableAt, failedAt.AddSeconds(15), failedAt.AddSeconds(30));
        Assert.Equal(0, claimedBeforeBackoff);
        var delivered = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Delivered", delivered.Status);
        Assert.Equal(2, delivered.Attempts);
        Assert.Single(harness.MailFiles());
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task SecurityNoticeRetryPreservesTheCapturedLocale(string locale)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.PasswordResetNotice, token: null, locale, Ct);
        var original = (await harness.ReadDeliveryAsync(id, Ct)).Payload;
        var dispatcher = harness.StartWorker(decorateSender: real => new FailingSender(real, failures: 1));
        await dispatcher.DispatchBatchAsync(Ct);
        var pending = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(original, pending.Payload);
        using var document = System.Text.Json.JsonDocument.Parse(pending.Payload);
        Assert.Equal(locale, document.RootElement.GetProperty("language").GetString());
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchBatchAsync(Ct);
        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(id, Ct)).Status);
        Assert.Single(harness.MailFiles());
    }

    [Fact]
    public async Task TwoConcurrentWorkersDeliverEachEmailExactlyOnce()
    {
        const int EmailCount = 20;
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryIds = new List<Guid>();
        for (var i = 0; i < EmailCount; i++)
        {
            deliveryIds.Add(await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, $"tok-{i}", Ct));
        }

        string[] smallBatches = ["--Outbox:BatchSize=2"];
        var first = harness.StartWorker(smallBatches);
        var second = harness.StartWorker(smallBatches);

        var claimedPerWorker = await Task.WhenAll(DrainAsync(first), DrainAsync(second));

        Assert.Equal(EmailCount, claimedPerWorker.Sum());
        Assert.Equal(EmailCount, harness.MailFiles().Length);
        foreach (var deliveryId in deliveryIds)
        {
            var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
            Assert.Equal(("Delivered", 1, 1L), (delivery.Status, delivery.Attempts, delivery.FencingToken));
        }
    }

    [Fact]
    public async Task WorkerThatLostTheLeaseToAnotherCannotSettleTheDelivery()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        StalledSender? staleSender = null;
        StalledSender? freshSender = null;
        var staleLogs = new CapturingLoggerProvider();
        var stale = harness.StartWorker(decorateSender: real => staleSender = new StalledSender(real), logs: staleLogs);
        var fresh = harness.StartWorker(decorateSender: real => freshSender = new StalledSender(real));

        var staleRun = stale.DispatchBatchAsync(Ct);
        await staleSender!.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        harness.Time.Advance(TimeSpan.FromMinutes(3));
        var freshRun = fresh.DispatchBatchAsync(Ct);
        await freshSender!.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        staleSender.Release();
        await staleRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var whileFreshHoldsIt = await harness.ReadDeliveryAsync(deliveryId, Ct);
        freshSender.Release();
        await freshRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(("InFlight", 2L), (whileFreshHoldsIt.Status, whileFreshHoldsIt.FencingToken));
        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal(("Delivered", 2, 2L), (delivery.Status, delivery.Attempts, delivery.FencingToken));
        Assert.Single(harness.MailFiles());
        Assert.Contains(staleLogs.Entries, entry => entry.Level == LogLevel.Warning && entry.Text.Contains("lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerWhoseLeaseExpiredCannotSettleEvenIfNobodyTookOver()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        StalledSender? staleSender = null;
        var stale = harness.StartWorker(decorateSender: real => staleSender = new StalledSender(real));

        var staleRun = stale.DispatchBatchAsync(Ct);
        await staleSender!.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        harness.Time.Advance(TimeSpan.FromMinutes(3));
        staleSender.Release();
        await staleRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var afterStale = await harness.ReadDeliveryAsync(deliveryId, Ct);
        await harness.StartWorker().DispatchBatchAsync(Ct);

        Assert.Equal(("InFlight", 1L), (afterStale.Status, afterStale.FencingToken));
        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal(("Delivered", 2L), (delivery.Status, delivery.FencingToken));
        Assert.Single(harness.MailFiles());
    }

    [Fact]
    public async Task DeliveryPastItsDeadlineExpiresWithoutSending()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "tok-1", Ct);
        var dispatcher = harness.StartWorker();
        harness.Time.Advance(AccountEmailOutbox.DeliveryWindow(AccountEmailKind.PasswordReset));

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Expired", delivery.Status);
        Assert.Equal("{}", delivery.Payload);
        Assert.Empty(harness.MailFiles());
    }

    [Fact]
    public async Task DeliveryFailsForReviewAfterTheLastAttempt()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        var dispatcher = harness.StartWorker(
            ["--Outbox:MaxAttempts=2"],
            decorateSender: real => new FailingSender(real, failures: int.MaxValue));

        await dispatcher.DispatchBatchAsync(Ct);
        harness.Time.Advance(TimeSpan.FromMinutes(20));
        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal(("Failed", 2, "IOException"), (delivery.Status, delivery.Attempts, delivery.LastError));
        Assert.Equal("{}", delivery.Payload);
        Assert.Empty(harness.MailFiles());
    }

    /// <summary>
    /// Nenhum payload lido hoje faz a composição lançar; este defeito fica no lugar de um erro de template ou de
    /// configuração. Sem a origem, todo e-mail com link lança ao montar o link, e o e-mail sem link sai normalmente.
    /// </summary>
    private static AccountEmails EmailsThatCannotComposeALink()
    {
        Assert.True(EmailAddress.TryParse("suporte@duora.test", out var supportContact));
        return new AccountEmails((PublicOrigin)RuntimeHelpers.GetUninitializedObject(typeof(PublicOrigin)), supportContact);
    }

    [Fact]
    public async Task AnEmailThatBlowsUpWhenComposedFailsAloneAndItsNeighboursAreSent()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var token = $"CANARY-{Guid.NewGuid():N}";
        var poisoned = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, token, Ct);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var neighbours = new[]
        {
            await harness.EnqueueAsync(AccountEmailKind.PasswordResetUnavailable, token: null, Ct),
            await harness.EnqueueAsync(AccountEmailKind.PasswordResetUnavailable, token: null, Ct),
        };
        var logs = new CapturingLoggerProvider();
        var dispatcher = harness.StartWorker(
            ["--Outbox:MaxAttempts=2"],
            logs: logs,
            configureServices: services => services.AddSingleton(EmailsThatCannotComposeALink()));

        Assert.Equal(3, await dispatcher.DispatchBatchAsync(Ct));

        var delivery = await harness.ReadDeliveryAsync(poisoned, Ct);
        Assert.Equal(("Pending", 1, nameof(ArgumentNullException)), (delivery.Status, delivery.Attempts, delivery.LastError));
        await AssertDeliveredOnTheirFirstAttemptAsync(harness, neighbours);

        harness.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(Ct));

        delivery = await harness.ReadDeliveryAsync(poisoned, Ct);
        Assert.Equal(("Failed", 2, nameof(ArgumentNullException)), (delivery.Status, delivery.Attempts, delivery.LastError));
        await AssertDeliveredOnTheirFirstAttemptAsync(harness, neighbours);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(token, StringComparison.Ordinal));
        var failures = FailuresOf(logs);
        Assert.Equal([LogLevel.Warning, LogLevel.Error], failures.Select(entry => entry.Level));
        Assert.All(failures, entry => AssertNamesTheStepAndTheFrames(entry, "Composition", nameof(PublicOrigin)));
    }

    [Fact]
    public async Task AFailedSendNamesTheStepAndTheFramesButNotTheProviderMessage()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        var logs = new CapturingLoggerProvider();
        var dispatcher = harness.StartWorker(logs: logs, decorateSender: _ => new SenderThatFails(new IOException(ProviderCanary)));

        await dispatcher.DispatchBatchAsync(Ct);

        var failure = Assert.Single(FailuresOf(logs));
        AssertNamesTheStepAndTheFrames(failure, "Send", nameof(SenderThatFails));
        Assert.DoesNotContain(ProviderCanary, failure.Text, StringComparison.Ordinal);
    }

    /// <summary>Leituras do relógio numa entrega com o envio bem-sucedido: o claim, o deadline, a renovação e o Settle.</summary>
    [Theory]
    [InlineData(3, "LeaseRenewal", 0)]
    [InlineData(4, "Settlement", 1)]
    public async Task AFailureAroundTheSendNamesItsStep(int explodingRead, string step, int mailFiles)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        var clock = new ScriptedClock(harness.Time);
        var logs = new CapturingLoggerProvider();
        var dispatcher = harness.StartWorker(logs: logs, configureServices: services => services.AddSingleton<TimeProvider>(clock));
        clock.ExplodeOnRead(explodingRead);

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal(("Pending", 1, nameof(ScriptedClockException)), (delivery.Status, delivery.Attempts, delivery.LastError));
        Assert.Equal(mailFiles, harness.MailFiles().Length);
        var failure = Assert.Single(FailuresOf(logs));
        AssertNamesTheStepAndTheFrames(failure, step, nameof(ScriptedClock));
        Assert.DoesNotContain(ScriptedClock.MessageCanary, failure.Text, StringComparison.Ordinal);
    }

    [Fact]
    [SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "O comportamento testado é justamente o desse tipo reservado.")]
    public async Task OutOfMemoryIsNotTreatedAsAFailureOfTheDelivery()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        var dispatcher = harness.StartWorker(decorateSender: _ => new SenderThatFails(new OutOfMemoryException()));

        await Assert.ThrowsAsync<OutOfMemoryException>(() => dispatcher.DispatchBatchAsync(Ct));

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal(("InFlight", 1, (string?)null), (delivery.Status, delivery.Attempts, delivery.LastError));
    }

    private const string ProviderCanary = "CANARY-mensagem-do-provedor";

    private static CapturedLog[] FailuresOf(CapturingLoggerProvider logs) =>
    [
        .. logs.Entries.Where(entry => entry.Category == typeof(TransactionalEmailDispatcher).FullName && entry.Level >= LogLevel.Warning),
    ];

    private static void AssertNamesTheStepAndTheFrames(CapturedLog entry, string step, string frame)
    {
        Assert.Contains($"em {step} ", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Frames: ", entry.Text, StringComparison.Ordinal);
        Assert.Contains(frame, entry.Text[entry.Text.IndexOf("Frames: ", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", entry.Text, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    private sealed class SenderThatFails(Exception failure) : ITransactionalEmailSender
    {
        public Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken) =>
            throw failure;
    }

    [Fact]
    public async Task AStopDuringTheSendLeavesTheDeliveryInFlightWithoutAFailureTrace()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", Ct);
        using var stopping = new CancellationTokenSource();
        var logs = new CapturingLoggerProvider();
        var dispatcher = harness.StartWorker(logs: logs, decorateSender: _ => new SenderStoppedMidway(stopping));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchBatchAsync(stopping.Token));

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal(("InFlight", 1, (string?)null), (delivery.Status, delivery.Attempts, delivery.LastError));
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Category == typeof(TransactionalEmailDispatcher).FullName && entry.Level >= LogLevel.Warning);
    }

    private sealed class SenderStoppedMidway(CancellationTokenSource stopping) : ITransactionalEmailSender
    {
        public async Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken)
        {
            await stopping.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task AssertDeliveredOnTheirFirstAttemptAsync(EmailOutboxHarness harness, Guid[] deliveries)
    {
        foreach (var id in deliveries)
        {
            var delivery = await harness.ReadDeliveryAsync(id, Ct);
            Assert.Equal(("Delivered", 1), (delivery.Status, delivery.Attempts));
        }

        Assert.Equal(deliveries.Length, harness.MailFiles().Length);
    }

    [Fact]
    public async Task TokenNeverAppearsInTheWorkerLogs()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var canary = $"CANARY-{Guid.NewGuid():N}";
        await harness.EnqueueAsync(AccountEmailKind.PasswordReset, canary, Ct);
        await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, canary, Ct);
        var logs = new CapturingLoggerProvider();
        var dispatcher = harness.StartWorker(
            decorateSender: real => new FailingSender(real, failures: 1),
            logs: logs);

        await dispatcher.DispatchBatchAsync(Ct);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal(2, harness.MailFiles().Length);
        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(canary, StringComparison.Ordinal));
        Assert.DoesNotContain(harness.MailFiles(), file => file.Contains(canary, StringComparison.Ordinal));
    }

    private static async Task<int> DrainAsync(TransactionalEmailDispatcher dispatcher)
    {
        var total = 0;
        int claimed;
        while ((claimed = await dispatcher.DispatchBatchAsync(Ct)) > 0)
        {
            total += claimed;
        }

        return total;
    }
}
