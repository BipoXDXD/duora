using System.Net;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieRequestSecurityTests(PostgresFixture postgres)
{
    private static Dictionary<string, string?> Settings => SelfieTestSettings.Enabled();

    [Theory]
    [InlineData("age", 25)]
    [InlineData("score", 100)]
    [InlineData("isAdult", true)]
    public async Task BrowserCannotSupplyVerificationResults(string field, object value)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Settings = Settings };
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var response = await browser.PostJsonAsync("/api/v1/me/selfie-verifications", new Dictionary<string, object> { [field] = value });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task ConcurrentRequestsReuseOneAttemptAndOtherAccountCannotGetCapture()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Settings = Settings };
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var other = await scenario.CreateConfirmedAccountAsync();
        var ct = TestContext.Current.CancellationToken;
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var attempts = await Task.WhenAll(
            first.ServiceProvider.GetRequiredService<SelfieVerificationService>().RequestAsync(owner.UserId, ct),
            second.ServiceProvider.GetRequiredService<SelfieVerificationService>().RequestAsync(owner.UserId, ct));
        var firstStatus = Assert.IsType<SelfieRequestOutcome.Accepted>(attempts[0]).Status;
        Assert.Equal(firstStatus.Id, Assert.IsType<SelfieRequestOutcome.Accepted>(attempts[1]).Status.Id);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", owner.UserId));
        // A valid-looking capture reference must still be restricted to its owner, without calling STS.
        await scenario.ScalarAsync("update identity.selfie_verifications set state = 'Capturing', provider_reference = $2 where id = $1 returning 1", firstStatus.Id!, Guid.NewGuid().ToString());
        var credentials = second.ServiceProvider.GetRequiredService<SelfieCaptureCredentialService>();
        Assert.Null(await credentials.CreateAsync(other.UserId, firstStatus.Id!.Value, ct));
        using var browser = await scenario.SignedInBrowserAsync(other);
        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{firstStatus.Id}/complete", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PersistentHourlyBudgetRejectsSixthAttempt()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Settings = Settings, Time = time };
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 5; i++)
        {
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<SelfieVerificationService>();
            var result = Assert.IsType<SelfieRequestOutcome.Accepted>(await service.RequestAsync(owner.UserId, ct)).Status;
            await scenario.ScalarAsync("update identity.selfie_verifications set state = 'Inconclusive' where id = $1 returning 1", result.Id!);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        using var final = factory.Services.CreateScope();
        Assert.IsType<SelfieRequestOutcome.Unavailable>(await final.ServiceProvider.GetRequiredService<SelfieVerificationService>().RequestAsync(owner.UserId, ct));
        Assert.Equal(5, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", owner.UserId));
    }
}
