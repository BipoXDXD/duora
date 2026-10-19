using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Outbox;

public sealed class OutboxReliabilityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(10, 60, 120, true)]
    [InlineData(10, 61, 120, false)]
    [InlineData(100, 30, 120, true)]
    [InlineData(1, 0, 120, false)]
    public void SendBudgetIsHalfTheLeaseWhateverTheBatchSize(int batch, int timeout, int lease, bool valid)
    {
        var options = new OutboxDispatchOptions
        {
            BatchSize = batch,
            SendTimeout = TimeSpan.FromSeconds(timeout),
            LeaseDuration = TimeSpan.FromSeconds(lease),
        };
        Assert.Equal(valid, options.HasSendLeaseBudget());
    }

    [Fact]
    public void SendBudgetDoesNotOverflowForExtremeTimeout()
    {
        var options = new OutboxDispatchOptions { SendTimeout = TimeSpan.MaxValue };
        Assert.False(options.HasSendLeaseBudget());
    }

    [Theory]
    [InlineData("--Outbox:SendTimeout=00:01:01")]
    [InlineData("--Outbox:LeaseDuration=00:00:59")]
    public async Task UnsafeEmailConfigurationIsRejected(string unsafeSetting)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        Assert.Throws<OptionsValidationException>(() => harness.StartWorker([unsafeSetting]));
    }

    [Fact]
    public async Task FullBatchWithTwoMinuteLeaseIsAcceptedBecauseTheLeaseIsRenewedPerSend()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var worker = harness.StartWorker(["--Outbox:BatchSize=100", "--Outbox:LeaseDuration=00:02:00", "--Outbox:SendTimeout=00:01:00"]);
        Assert.Equal(0, await worker.DispatchBatchAsync(Ct));
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
        var logs = new CapturingLoggerProvider();
        var stale = harness.StartWorker(decorateSender: inner => new CallbackSender(async (email, key, _) =>
        {
            calls++;
            harness.Time.Advance(TimeSpan.FromMinutes(3));
            await inner.SendAsync(email, key, Ct);
        }), logs: logs);
        Assert.Equal(3, await stale.DispatchBatchAsync(Ct));
        Assert.Equal(1, calls);
        // Pulada antes do envio e conclusão recusada depois do envio são casos distintos, com logs distintos.
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Warning
            && e.Text.Contains("não foi enviada: o heartbeat não renovou a posse", StringComparison.Ordinal)));
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning
            && e.Text.Contains("não foi encerrada por este worker", StringComparison.Ordinal));
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
        var claimed = Assert.Single((await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct)).Claimed);
        Assert.True(await queue.RescheduleAsync(claimed, now, "IOException", now, Ct));
        var exhaustion = await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct);
        Assert.Empty(exhaustion.Claimed);
        Assert.Equal([new ExhaustedDelivery(id, 1, OutboxDeliveryStatus.Failed)], exhaustion.Exhausted);
        Assert.False(await queue.SettleAsync(claimed, OutboxDeliveryStatus.Delivered, null, now, Ct));
        Assert.False(await queue.RescheduleAsync(claimed, now, "IOException", now, Ct));
        Assert.Empty(await queue.RenewLeasesAsync([claimed], TimeSpan.FromMinutes(2), harness.Time, Ct));
        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("Failed", 1, "{}"), (row.Status, row.Attempts, row.Payload));
    }

    [Fact]
    public async Task ExhaustedClaimIsLoggedAsErrorWithDeliveryConsumerAndAttempts()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var now = harness.Time.GetUtcNow();
        Assert.Single(await new OutboxDeliveryQueue(source).ClaimAsync(
            AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), Ct));
        harness.Time.Advance(TimeSpan.FromMinutes(3));
        var logs = new CapturingLoggerProvider();

        Assert.Equal(0, await harness.StartWorker(["--Outbox:MaxAttempts=1"], logs: logs).DispatchBatchAsync(Ct));

        var entry = Assert.Single(logs.Entries, e =>
            e.Category == typeof(TransactionalEmailDispatcher).FullName && e.Level == LogLevel.Error);
        Assert.Contains(id.ToString(), entry.Text, StringComparison.Ordinal);
        Assert.Contains($"consumidor {AccountEmailOutbox.Consumer}", entry.Text, StringComparison.Ordinal);
        Assert.Contains("após 1 tentativa(s)", entry.Text, StringComparison.Ordinal);
        Assert.Equal("Failed", (await harness.ReadDeliveryAsync(id, Ct)).Status);
    }

    [Fact]
    public async Task ExhaustedDeliveryPastItsDeadlineExpiresInsteadOfFailing()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "token", Ct);
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var now = harness.Time.GetUtcNow();
        Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), Ct));
        now += AccountEmailOutbox.DeliveryWindow(AccountEmailKind.PasswordReset);

        var result = await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), 1, Ct);

        Assert.Equal([new ExhaustedDelivery(id, 1, OutboxDeliveryStatus.Expired)], result.Exhausted);
        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("Expired", 1, 2L, null, "{}"), (row.Status, row.Attempts, row.FencingToken, row.LastError, row.Payload));
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
        Assert.Single((await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct)).Claimed);
        now = now.AddMinutes(3);
        Assert.Empty((await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 10, now, now.AddMinutes(2), 1, Ct)).Claimed);
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

    [Fact]
    public async Task HeartbeatExtendsTheLeaseFromTheCurrentInstant()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var now = harness.Time.GetUtcNow();
        var delivery = Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), Ct));
        harness.Time.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal([id], await queue.RenewLeasesAsync([delivery], TimeSpan.FromMinutes(2), harness.Time, Ct));

        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("InFlight", 1, 1L), (row.Status, row.Attempts, row.FencingToken));
        Assert.Equal(now.AddSeconds(90).AddMinutes(2), row.LeaseExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task HeartbeatAfterTheLeaseExpiredDoesNotRenewIt()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var now = harness.Time.GetUtcNow();
        var delivery = Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), Ct));
        harness.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(await queue.RenewLeasesAsync([delivery], TimeSpan.FromMinutes(2), harness.Time, Ct));

        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(now.AddMinutes(2), row.LeaseExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task HeartbeatRenewsOnlyTheDeliveriesStillOwned()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var kept = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token-1", Ct);
        var lost = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token-2", Ct);
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var now = harness.Time.GetUtcNow();
        var batch = await queue.ClaimAsync(AccountEmailOutbox.Consumer, 2, now, now.AddMinutes(2), Ct);
        var stolen = batch.Single(d => d.Id == lost);
        Assert.True(await queue.RescheduleAsync(stolen, now, "IOException", now, Ct));
        Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), Ct));

        Assert.Equal([kept], await queue.RenewLeasesAsync(batch, TimeSpan.FromMinutes(2), harness.Time, Ct));
    }

    [Theory]
    [InlineData(119, true)]
    [InlineData(120, false)]
    public async Task HeartbeatReadsTheClockAfterWaitingForTheConnectionPool(int elapsedSeconds, bool renewed)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        var settings = new NpgsqlConnectionStringBuilder(harness.ConnectionString) { MaxPoolSize = 1 };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var now = harness.Time.GetUtcNow();
        var delivery = Assert.Single(await queue.ClaimAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), Ct));

        Task<IReadOnlySet<Guid>> heartbeat;
        await using (var occupied = await source.OpenConnectionAsync(Ct))
        {
            heartbeat = queue.RenewLeasesAsync([delivery], TimeSpan.FromMinutes(2), harness.Time, Ct);
            Assert.False(heartbeat.IsCompleted);
            harness.Time.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        }

        Assert.Equal(renewed, (await heartbeat).Contains(delivery.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFinalDeliveriesScrubPayloadWithAFreshSnapshot(bool exhaustFirst)
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
        var first = Assert.Single((await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 1, now,
            now.AddMinutes(exhaustFirst ? 2 : 10), 1, Ct)).Claimed);
        var second = Assert.Single(await queue.ClaimAsync("other-consumer", 1, now, now.AddMinutes(10), Ct));
        now = now.AddMinutes(3);

        var application = $"outbox-review-{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(harness.ConnectionString) { ApplicationName = application };
        await using var competingSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var competitor = new OutboxDeliveryQueue(competingSource);
        await using var holder = await source.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var takeLock = new NpgsqlCommand(
            "select id from ops.outbox_deliveries where id = @id for update", holder, transaction))
        {
            takeLock.Parameters.AddWithValue("id", second.Id);
            await takeLock.ExecuteNonQueryAsync(Ct);
        }

        // O UPDATE do segundo começa ANTES do encerramento do primeiro, mas só pode prosseguir
        // depois. Um CTE único mantém o snapshot antigo e deixa ambos finais com o token no payload.
        var settling = competitor.SettleAsync(second, OutboxDeliveryStatus.Delivered, null, now, Ct);
        try
        {
            await WaitForDatabaseLockAsync(source, application);
            if (exhaustFirst)
            {
                Assert.Empty((await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(10), 1, Ct)).Claimed);
            }
            else
            {
                Assert.True(await queue.SettleAsync(first, OutboxDeliveryStatus.Delivered, null, now, Ct));
            }

            Assert.Contains("token", (await harness.ReadDeliveryAsync(first.Id, Ct)).Payload, StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }

        Assert.True(await settling);
        var completed = await harness.ReadDeliveryAsync(first.Id, Ct);
        Assert.Equal(exhaustFirst ? "Failed" : "Delivered", completed.Status);
        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(second.Id, Ct)).Status);
        Assert.Equal("{}", completed.Payload);
    }

    [Fact]
    public async Task SiblingSettlesSerializeOnTheEventLockSoTheLastOneScrubsThePayload()
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
        var firstReachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // O primeiro para depois de encerrar a entrega e de conferir as irmãs (ainda aberta), antes do commit.
        var firstQueue = new OutboxDeliveryQueue(source)
        {
            BeforeSettleCommit = async ct =>
            {
                firstReachedCommit.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            },
        };
        var application = $"outbox-sibling-{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(harness.ConnectionString) { ApplicationName = application };
        await using var secondSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var secondQueue = new OutboxDeliveryQueue(secondSource);
        var first = Assert.Single(await firstQueue.ClaimAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(10), Ct));
        var second = Assert.Single(await secondQueue.ClaimAsync("other-consumer", 1, now, now.AddMinutes(10), Ct));

        var settlingFirst = firstQueue.SettleAsync(first, OutboxDeliveryStatus.Delivered, null, now, Ct);
        await firstReachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // Com o lock do evento, o segundo espera o commit do primeiro e confere as irmãs num snapshot novo. Sem o
        // lock, ele termina já, vendo a irmã ainda aberta, e o primeiro também não limpou: o payload ficaria.
        var settlingSecond = secondQueue.SettleAsync(second, OutboxDeliveryStatus.Delivered, null, now, Ct);
        await WaitUntilWaitingOnLockOrDoneAsync(source, application, settlingSecond);
        releaseFirst.SetResult();

        Assert.True(await settlingFirst);
        Assert.True(await settlingSecond);
        Assert.Equal("{}", (await harness.ReadDeliveryAsync(first.Id, Ct)).Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringScrubRollsBackTerminalTransition(bool exhaust)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "token", Ct);
        var now = harness.Time.GetUtcNow();
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var delivery = Assert.Single((await queue.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 1, now, now.AddMinutes(2), 1, Ct)).Claimed);
        await using var holder = await source.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var takeLock = new NpgsqlCommand(
            """
            select e.id from ops.outbox_events e join ops.outbox_deliveries d on d.outbox_event_id = e.id
            where d.id = @id for no key update of e
            """, holder, transaction))
        {
            takeLock.Parameters.AddWithValue("id", id);
            await takeLock.ExecuteNonQueryAsync(Ct);
        }

        var application = $"outbox-cancel-{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(harness.ConnectionString) { ApplicationName = application };
        await using var competingSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var competitor = new OutboxDeliveryQueue(competingSource);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task finishing = exhaust
            ? competitor.ClaimWithAttemptLimitAsync(AccountEmailOutbox.Consumer, 1, now.AddMinutes(3), now.AddMinutes(13), 1, cancelled.Token)
            : competitor.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, now, cancelled.Token);
        try
        {
            await WaitForDatabaseLockAsync(source, application);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishing);
        }
        finally
        {
            await cancelled.CancelAsync();
            await transaction.RollbackAsync(Ct);
        }

        var row = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal(("InFlight", 1, 1L), (row.Status, row.Attempts, row.FencingToken));
        Assert.Contains("token", row.Payload, StringComparison.Ordinal);
    }

    private static async Task WaitForDatabaseLockAsync(NpgsqlDataSource source, string application)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var command = source.CreateCommand(
            "select exists (select 1 from pg_stat_activity where application_name = @application and wait_event_type = 'Lock')");
        command.Parameters.AddWithValue("application", application);
        while (!(bool)(await command.ExecuteScalarAsync(deadline.Token))!)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private static async Task WaitUntilWaitingOnLockOrDoneAsync(NpgsqlDataSource source, string application, Task work)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var command = source.CreateCommand(
            "select exists (select 1 from pg_stat_activity where application_name = @application and wait_event_type = 'Lock')");
        command.Parameters.AddWithValue("application", application);
        while (!work.IsCompleted && !(bool)(await command.ExecuteScalarAsync(deadline.Token))!)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private sealed class CallbackSender(Func<TransactionalEmail, string, CancellationToken, Task> send) : ITransactionalEmailSender
    {
        public Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken) =>
            send(email, idempotencyKey, cancellationToken);
    }
}
