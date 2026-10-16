using System.Diagnostics;
using Duora.Infrastructure.Identity;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// Worker que abre a verificação no fornecedor a partir da outbox (ADR-0024, decisão 1; TM-39). T-17: fornecedor
/// lento ou falhando não trava o worker nem libera a conta, que continua <c>PendingEligibility</c>.
/// </summary>
public sealed class AgeVerificationProviderDispatchTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SucceedingProviderStoresItsReferenceAndFlow()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var dispatcher = harness.StartWorker();

        var claimed = await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal(1, claimed);
        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Delivered", delivery.Status);
        Assert.Equal("{}", delivery.Payload);
        var verification = await harness.ReadVerificationAsync(pending.UserId, Ct);
        Assert.Equal("Pending", verification.State);
        Assert.Equal($"{FakeAgeEligibilityProvider.ReferenceFor(pending.VerificationId)}", verification.ProviderReference);
        Assert.Equal(
            $"{AgeVerificationScenario.FakeFlowOrigin}/flow/{FakeAgeEligibilityProvider.ReferenceFor(pending.VerificationId)}",
            verification.FlowUrl);
        Assert.Equal(1, verification.Version);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }

    [Fact]
    public async Task TaskWrittenWithoutTheSessionFieldIsStillOpened()
    {
        // Leitor tolerante: a release seguinte para de gravar o loginSessionId, e este worker já aceita a ausência.
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        await harness.ExecuteAsync(
            """
            update ops.outbox_events
            set payload = jsonb_build_object('verificationId', resource_id)
            where resource_id = $1
            """,
            Ct,
            pending.VerificationId);
        var dispatcher = harness.StartWorker();

        var claimed = await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal(1, claimed);
        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(pending.DeliveryId, Ct)).Status);
        var verification = await harness.ReadVerificationAsync(pending.UserId, Ct);
        Assert.Equal($"{FakeAgeEligibilityProvider.ReferenceFor(pending.VerificationId)}", verification.ProviderReference);
    }

    [Fact]
    [Trait("Acceptance", "T-17")]
    public async Task SlowProviderTimesOutAndTheAccountStaysPending()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct, realClock: true);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var dispatcher = harness.StartWorker(
            "Slow",
            "--AgeVerification:Fake:SlowDelay=00:01:00",
            "--AgeVerification:Dispatch:SendTimeout=00:00:00.300",
            "--AgeVerification:Dispatch:LeaseDuration=00:00:10");

        var watch = Stopwatch.StartNew();
        await dispatcher.DispatchBatchAsync(Ct);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"O worker esperou {watch.Elapsed} pelo fornecedor.");
        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal(1, delivery.Attempts);
        Assert.NotNull(delivery.LastError);
        var verification = await harness.ReadVerificationAsync(pending.UserId, Ct);
        Assert.Equal("Pending", verification.State);
        Assert.Null(verification.ProviderReference);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }

    [Fact]
    [Trait("Acceptance", "T-17")]
    public async Task FailingProviderIsRetriedAfterTheBackoffAndTheAccountStaysPending()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var dispatcher = harness.StartWorker("Fail");

        var failedAt = harness.Time.GetUtcNow();
        await dispatcher.DispatchBatchAsync(Ct);
        var claimedBeforeBackoff = await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal(0, claimedBeforeBackoff);
        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal("HttpRequestException", delivery.LastError);
        Assert.InRange(delivery.AvailableAt, failedAt.AddSeconds(15), failedAt.AddSeconds(30));
        Assert.Null((await harness.ReadVerificationAsync(pending.UserId, Ct)).ProviderReference);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }

    [Fact]
    [Trait("Acceptance", "T-17")]
    public async Task ProviderThatKeepsFailingEndsInTheReviewQueueAndTheAccountStaysPending()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var dispatcher = harness.StartWorker("Fail", "--AgeVerification:Dispatch:MaxAttempts=2");

        await dispatcher.DispatchBatchAsync(Ct);
        harness.FakeTime.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal(2, delivery.Attempts);
        var verification = await harness.ReadVerificationAsync(pending.UserId, Ct);
        Assert.Equal("Pending", verification.State);
        Assert.Null(verification.ProviderReference);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }

    [Fact]
    public async Task FlowOutsideTheAllowlistIsRefusedAndNotStored()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var dispatcher = harness.StartWorker("Succeed", "--AgeVerification:Fake:FlowBaseUrl=https://phishing.example/flow");

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal("DisallowedFlowOrigin", delivery.LastError);
        var verification = await harness.ReadVerificationAsync(pending.UserId, Ct);
        Assert.Null(verification.ProviderReference);
        Assert.Null(verification.FlowUrl);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Expired")]
    [InlineData("ManualReview")]
    public async Task VerificationNoLongerPendingIsNotSentToTheProvider(string state)
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        await harness.ExecuteAsync(
            "update identity.age_verifications set state = $2 where id = $1",
            Ct,
            pending.VerificationId,
            state);
        // Se o worker chamasse o fornecedor, a entrega falharia em vez de terminar entregue.
        var dispatcher = harness.StartWorker("Fail");

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Delivered", delivery.Status);
        Assert.Null(delivery.LastError);
        Assert.Null((await harness.ReadVerificationAsync(pending.UserId, Ct)).ProviderReference);
    }

    [Fact]
    public async Task VerificationAlreadyOpenedKeepsItsReference()
    {
        // Um worker anterior abriu no fornecedor e perdeu o lease antes de encerrar a entrega.
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        await harness.ExecuteAsync(
            "update identity.age_verifications set provider_reference = 'ref-anterior', provider_flow_url = $2 where id = $1",
            Ct,
            pending.VerificationId,
            AgeVerificationScenario.FakeFlowOrigin + "/flow/ref-anterior");
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(pending.DeliveryId, Ct)).Status);
        Assert.Equal("ref-anterior", (await harness.ReadVerificationAsync(pending.UserId, Ct)).ProviderReference);
    }

    [Fact]
    public async Task TaskPastTheRequestLifetimeExpiresWithoutCallingTheProvider()
    {
        await using var harness = await AgeVerificationOutboxHarness.CreateAsync(postgres, Ct);
        var pending = await harness.CreatePendingVerificationAsync(Ct);
        var dispatcher = harness.StartWorker("Fail");
        harness.FakeTime.Advance(TimeSpan.FromHours(24));

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(pending.DeliveryId, Ct);
        Assert.Equal("Expired", delivery.Status);
        Assert.Equal("PendingEligibility", await harness.AccountStatusAsync(pending.UserId, Ct));
    }
}
