using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Outbox;

public sealed class OutboxReliabilityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(10, 30, 120, false)]
    [InlineData(10, 30, 599, false)]
    [InlineData(10, 30, 600, true)]
    [InlineData(2, 30, 120, true)]
    [InlineData(0, 30, 120, false)]
    public void BatchBudgetIncludesEverySendAndSafetyMargin(int batch, int timeout, int lease, bool valid)
    {
        var options = new OutboxDispatchOptions
        {
            BatchSize = batch,
            SendTimeout = TimeSpan.FromSeconds(timeout),
            LeaseDuration = TimeSpan.FromSeconds(lease),
        };
        Assert.Equal(valid, options.HasBatchLeaseBudget());
    }

    [Fact]
    public void BatchBudgetDoesNotOverflowForExtremeTimeout()
    {
        var options = new OutboxDispatchOptions { SendTimeout = TimeSpan.MaxValue };
        Assert.False(options.HasBatchLeaseBudget());
    }

    [Fact]
    public async Task UnsafeEmailConfigurationIsRejected()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        Assert.Throws<OptionsValidationException>(() => harness.StartWorker(["--Outbox:LeaseDuration=00:02:00"]));
    }

    [Fact]
    public async Task SlowFullBatchCannotBeReclaimedByCompetingWorker()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, $"token-{i}", Ct));
        }

        var competitor = harness.StartWorker();
        var calls = new List<string>();
        var slow = harness.StartWorker(decorateSender: inner => new CallbackSender(async (email, key, ct) =>
        {
            calls.Add(key);
            harness.Time.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal(0, await competitor.DispatchBatchAsync(ct));
            await inner.SendAsync(email, key, ct);
        }));

        Assert.Equal(10, await slow.DispatchBatchAsync(Ct));
        Assert.Equal(10, calls.Count);
        Assert.Equal(10, calls.Distinct().Count());
        Assert.Equal(10, harness.MailFiles().Length);
        foreach (var id in ids)
        {
            var row = await harness.ReadDeliveryAsync(id, Ct);
            Assert.Equal(("Delivered", 1, 1L), (row.Status, row.Attempts, row.FencingToken));
        }
    }

    [Fact]
    public async Task PausedWorkerDoesNotStartRemainingSendsAfterLosingLease()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        for (var i = 0; i < 3; i++)
        {
            await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, $"token-{i}", Ct);
        }

        var calls = 0;
        var stale = harness.StartWorker(decorateSender: inner => new CallbackSender(async (email, key, _) =>
        {
            calls++;
            harness.Time.Advance(TimeSpan.FromMinutes(11));
            await inner.SendAsync(email, key, Ct);
        }));
        Assert.Equal(3, await stale.DispatchBatchAsync(Ct));
        Assert.Equal(1, calls);
        Assert.Equal(3, await harness.StartWorker().DispatchBatchAsync(Ct));
        Assert.Equal(3, harness.MailFiles().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedWorkersReachTerminalFailureWithoutAnotherSend(bool sendBeforeInterruption)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        var keys = new List<string>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var worker = harness.StartWorker(["--Outbox:MaxAttempts=2"], decorateSender: inner =>
                new CallbackSender(async (email, key, ct) =>
                {
                    keys.Add(key);
                    if (sendBeforeInterruption)
                    {
                        await inner.SendAsync(email, key, ct);
                    }

                    shutdown.Cancel();
                    ct.ThrowIfCancellationRequested();
                }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.DispatchBatchAsync(shutdown.Token));
            var row = await harness.ReadDeliveryAsync(id, Ct);
            Assert.Equal(("InFlight", attempt), (row.Status, row.Attempts));
            // The final attempt is still owned: even a worker with the same cap must leave it alone.
            Assert.Equal(0, await harness.StartWorker(["--Outbox:MaxAttempts=2"]).DispatchBatchAsync(Ct));
            Assert.Equal("InFlight", (await harness.ReadDeliveryAsync(id, Ct)).Status);
            harness.Time.Advance(TimeSpan.FromMinutes(11));
        }

        var recovery = harness.StartWorker(["--Outbox:MaxAttempts=2"]);
        Assert.Equal(0, await recovery.DispatchBatchAsync(Ct));
        var failed = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("Failed", 2, 3L, "MaxAttemptsExceeded", "{}"),
            (failed.Status, failed.Attempts, failed.FencingToken, failed.LastError, failed.Payload));
        harness.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await recovery.DispatchBatchAsync(Ct));
        Assert.Single(keys.Distinct());
        Assert.Equal(sendBeforeInterruption ? 1 : 0, harness.MailFiles().Length);
    }

    [Fact]
    public async Task ExhaustedPendingDeliveryIsFailedAndStaleFencingCannotOverwriteIt()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        await using var dataSource = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(dataSource);
        var now = harness.Time.GetUtcNow();
        var claimed = Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct));
        Assert.True(await queue.RescheduleAsync(claimed, now, "IOException", now, Ct));
        Assert.Empty(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct));
        Assert.False(await queue.SettleAsync(claimed, OutboxDeliveryStatus.Delivered, null, now, Ct));
        Assert.False(await queue.RescheduleAsync(claimed, now, "IOException", now, Ct));
        Assert.False(await queue.CanSendAsync(claimed, now, Ct));
        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("Failed", 1, "{}"), (row.Status, row.Attempts, row.Payload));
    }

    [Fact]
    public async Task ExhaustionPreservesPayloadForOtherConsumerAndItsClaimContract()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var now = harness.Time.GetUtcNow();
        var ev = AccountEmailOutbox.Create(new AccountEmailRequested(
            AccountEmailKind.EmailConfirmation, "ana@example.com", Guid.CreateVersion7(), "token", "pt-BR"), now);
        ev.AddDelivery("other-consumer", "recipient", now.AddHours(1));
        await using (var db = PostgresFixture.CreateDbContext(harness.ConnectionString))
        {
            db.Add(ev);
            await db.SaveChangesAsync(Ct);
        }

        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct));
        now = now.AddMinutes(3);
        Assert.Empty(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct));
        var other = Assert.Single(await queue.ClaimAsync("other-consumer", 10, now, now.AddMinutes(2), Ct));
        Assert.Contains("token", other.Payload, StringComparison.Ordinal);
        Assert.True(await queue.SettleAsync(other, OutboxDeliveryStatus.Delivered, null, now, Ct));
        Assert.Equal("{}", (await harness.ReadDeliveryAsync(other.Id, Ct)).Payload);
    }

    [Fact]
    public async Task RepeatedProviderFailureNeverBecomesEligibleAgain()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        var calls = 0;
        var worker = harness.StartWorker(["--Outbox:MaxAttempts=2"], decorateSender: _ =>
            new CallbackSender((_, _, _) =>
            {
                calls++;
                return Task.FromException(new IOException("Simulated provider failure"));
            }));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(1, await worker.DispatchBatchAsync(Ct));
            harness.Time.Advance(TimeSpan.FromMinutes(16));
        }

        Assert.Equal(0, await worker.DispatchBatchAsync(Ct));
        Assert.Equal(2, calls);
        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("Failed", 2, "IOException", "{}"), (row.Status, row.Attempts, row.LastError, row.Payload));
        Assert.Empty(harness.MailFiles());
    }

    private sealed class CallbackSender(Func<TransactionalEmail, string, CancellationToken, Task> send) : ITransactionalEmailSender
    {
        public Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken) =>
            send(email, idempotencyKey, cancellationToken);
    }
}
