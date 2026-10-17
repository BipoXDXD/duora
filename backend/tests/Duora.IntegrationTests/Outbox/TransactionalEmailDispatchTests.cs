using Duora.Infrastructure.Email;
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
            $"{EmailOutboxHarness.PublicOrigin}/confirmar-email#userId=",
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
        var stale = harness.StartWorker(["--Outbox:BatchSize=1", "--Outbox:LeaseDuration=00:02:00"], decorateSender: real => staleSender = new StalledSender(real), logs: staleLogs);
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
        var stale = harness.StartWorker(["--Outbox:BatchSize=1", "--Outbox:LeaseDuration=00:02:00"], decorateSender: real => staleSender = new StalledSender(real));

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
