using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Outbox;
using Npgsql;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>PostgreSQL real, relógio controlado e fornecedor sintético: INV-01/06/08, T-08/T-17.</summary>
public sealed class AgeVerificationReliabilityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCallsExhaustWithoutAnotherCallAndKeepDomainCompensation(bool afterExternalEffect)
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var calls = new List<string>();
        var effects = new HashSet<string>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var provider = new CallbackProvider(async (request, token) =>
            {
                calls.Add(request.IdempotencyKey);
                if (afterExternalEffect)
                {
                    effects.Add(request.IdempotencyKey);
                }

                await stopped.CancelAsync();
                token.ThrowIfCancellationRequested();
                return Session(request);
            });
            var worker = harness.StartWorkerWithProvider(provider, "Succeed", "--AgeVerification:Dispatch:MaxAttempts=2");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.DispatchBatchAsync(stopped.Token));
            var interrupted = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
            Assert.Equal(("InFlight", attempt), (interrupted.Status, interrupted.Attempts));
            harness.FakeTime.Advance(TimeSpan.FromMinutes(3));
        }

        var finalWorker = harness.StartWorkerWithProvider(new CallbackProvider((request, _) =>
        {
            calls.Add(request.IdempotencyKey);
            return Task.FromResult(Session(request));
        }), "Succeed", "--AgeVerification:Dispatch:MaxAttempts=2");
        Assert.Equal(0, await finalWorker.DispatchBatchAsync(Ct));
        Assert.Equal(0, await finalWorker.DispatchBatchAsync(Ct));
        var row = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal(("Failed", 2, "MaxAttemptsExceeded", "{}"), (row.Status, row.Attempts, row.LastError, row.Payload));
        Assert.Equal(2, calls.Count);
        Assert.Single(calls.Distinct());
        Assert.Equal(afterExternalEffect ? 1 : 0, effects.Count);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
        Assert.Equal("Pending", (await harness.ReadVerificationAsync(pending.UserId, Ct)).State);

        // O claim não faz compensação parcial: o caso de uso expira a antiga e cria outra atomicamente.
        await using var db = PostgresFixture.CreateDbContext(harness.ConnectionString);
        var service = new AgeVerificationService(db, new AgeVerificationOptions
        {
            Provider = AgeVerificationProviderKind.Fake,
        }, "fake", harness.Time);
        Assert.IsType<AgeVerificationRequestResult.Accepted>(await service.RequestAsync(pending.UserId, Guid.NewGuid(), Ct));
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var command = source.CreateCommand("select state from identity.age_verifications where id = @id");
        command.Parameters.AddWithValue("id", pending.VerificationId);
        Assert.Equal("Expired", await command.ExecuteScalarAsync(Ct));
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }

    [Fact]
    public async Task SlowBatchRenewsWaitingItemsAgainstACompetingWorker()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = new List<PendingVerification>();
        for (var i = 0; i < 10; i++)
        {
            pending.Add(await harness.CreatePendingVerificationAsync(Ct));
        }

        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var competitor = new OutboxDeliveryQueue(source);
        var calls = 0;
        var provider = new CallbackProvider(async (request, _) =>
        {
            calls++;
            harness.FakeTime.Advance(TimeSpan.FromSeconds(9));
            var now = harness.Time.GetUtcNow();
            var result = await competitor.ClaimWithAttemptLimitAsync(AgeVerificationOutbox.Consumer, 10, now, now.AddSeconds(20), 8, Ct);
            Assert.Empty(result.Claimed);
            Assert.Empty(result.Exhausted);
            return Session(request);
        });
        // Igualdade na margem é aceita; 90 s de lote ultrapassam várias vezes o lease de 20 s.
        var worker = harness.StartWorkerWithProvider(provider, "Succeed", "--AgeVerification:Dispatch:LeaseDuration=00:00:20");
        Assert.Equal(10, await worker.DispatchBatchAsync(Ct));
        Assert.Equal(10, calls);
        foreach (var item in pending)
        {
            Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(item.DeliveryId, Ct)).Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseBeyondLeaseSkipsWaitingCallAndRejectsStaleSettlement(bool reclaim)
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var first = await harness.CreatePendingVerificationAsync(Ct);
        harness.FakeTime.Advance(TimeSpan.FromSeconds(1));
        var second = await harness.CreatePendingVerificationAsync(Ct);
        await using var source = NpgsqlDataSource.Create(harness.ConnectionString);
        var queue = new OutboxDeliveryQueue(source);
        var calls = 0;
        var provider = new CallbackProvider(async (request, _) =>
        {
            calls++;
            // Pausa do processo após efeito externo; adaptador retorna apesar de cancelamento do timeout.
            harness.FakeTime.Advance(TimeSpan.FromMinutes(3));
            if (reclaim)
            {
                var now = harness.Time.GetUtcNow();
                Assert.Equal(2, (await queue.ClaimWithAttemptLimitAsync(AgeVerificationOutbox.Consumer, 10, now, now.AddMinutes(2), 8, Ct)).Claimed.Count);
            }

            return Session(request);
        });
        await harness.StartWorkerWithProvider(provider).DispatchBatchAsync(Ct);
        Assert.Equal(1, calls);
        Assert.Null((await harness.ReadVerificationAsync(second.UserId, Ct)).ProviderReference);
        Assert.Equal("InFlight", (await harness.ReadDeliveryAsync(first.DeliveryId, Ct)).Status);
        Assert.Equal(reclaim ? 2 : 1, (await harness.ReadDeliveryAsync(second.DeliveryId, Ct)).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedPoisonPayloadIsNeverReadOrSent(bool expired)
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        await harness.ExecuteAsync("update ops.outbox_deliveries set attempts = 2 where id = $1", Ct, pending.DeliveryId);
        await harness.ExecuteAsync("update ops.outbox_events set payload = '{\"verificationId\":123}'::jsonb where resource_id = $1", Ct, pending.VerificationId);
        if (expired)
        {
            harness.FakeTime.Advance(TimeSpan.FromHours(24));
        }

        var calls = 0;
        var worker = harness.StartWorkerWithProvider(new CallbackProvider((request, _) =>
        {
            calls++;
            return Task.FromResult(Session(request));
        }), "Succeed", "--AgeVerification:Dispatch:MaxAttempts=2");
        Assert.Equal(0, await worker.DispatchBatchAsync(Ct));
        var row = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal(expired ? "Expired" : "Failed", row.Status);
        Assert.Equal(expired ? null : "MaxAttemptsExceeded", row.LastError);
        Assert.Equal(2, row.Attempts);
        Assert.Equal("{}", row.Payload);
        Assert.Equal(0, calls);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }

    private static AgeCheckSession Session(AgeCheckStart request) =>
        new(request.IdempotencyKey, new Uri($"{AgeVerificationScenario.FakeFlowOrigin}/flow/{request.IdempotencyKey}"));

    private sealed class CallbackProvider(Func<AgeCheckStart, CancellationToken, Task<AgeCheckSession>> start) : IAgeEligibilityProvider
    {
        public string Name => "fake";

        public Task<AgeCheckSession> StartAsync(AgeCheckStart request, CancellationToken cancellationToken) => start(request, cancellationToken);
    }
}
