using System.Buffers.Text;
using System.Net;
using System.Text;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

public sealed class EmailConfirmationCodeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RestartDoesNotResetTheIssuanceBudget()
    {
        var email = IdentityScenario.NewEmail();
        for (var i = 0; i < 4; i++)
        {
            await using var factory = new DuoraWebFactory(postgres.ConnectionString);
            using var browser = new Browser(factory);
            using var response = await IdentityScenario.RegisterAsync(browser, email);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await using var read = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, read);
        Assert.Equal(3, await scenario.ScalarAsync("select count(*) from ops.outbox_events where payload->>'to' = $1", email));
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public async Task CodeExpiresAtFifteenMinutesEvenWhenRegistrationIsStillPending(int minutes, bool accepted)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Time = time };
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        Assert.Matches("^[0-9]{8}$", proof.Token);
        time.Advance(TimeSpan.FromMinutes(minutes));

        using var confirmation = await browser.PostJsonAsync("/api/v1/auth/confirm-email",
            new { userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword });

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmation.StatusCode);
        }
        else
        {
            await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        }
    }

    [Fact]
    public async Task LegacyRegistrationWithoutCodeStillAcceptsItsOriginalToken()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        await scenario.ScalarAsync(
            "delete from identity.user_tokens where user_id = $1 and login_provider = 'Duora.EmailConfirmation' returning 1",
            proof.UserId);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DuoraUser>>();
        var user = await users.FindByIdAsync(proof.UserId.ToString("D"));
        Assert.NotNull(user);
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));

        using var confirmation = await browser.PostJsonAsync("/api/v1/auth/confirm-email",
            new { userId = proof.UserId, token, password = IdentityScenario.ValidPassword });

        Assert.Equal(HttpStatusCode.NoContent, confirmation.StatusCode);
    }

    [Fact]
    public async Task ParallelRedemptionsOnlyConfirmOnce()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        using var first = new Browser(factory);
        using var second = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registration = await IdentityScenario.RegisterAsync(first, email);
        await second.FetchCsrfTokenAsync();
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        var body = new { userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword };

        var responses = await Task.WhenAll(
            first.PostJsonAsync("/api/v1/auth/confirm-email", body),
            second.PostJsonAsync("/api/v1/auth/confirm-email", body));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConfirmationConsumesCodeAndRemovesItsHash()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.user_tokens where user_id = $1 and login_provider = 'Duora.EmailConfirmation' and strpos(value, $2) = 0",
            proof.UserId, proof.Token));

        using var confirmation = await browser.PostJsonAsync("/api/v1/auth/confirm-email",
            new { userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword });
        using var replay = await browser.PostJsonAsync("/api/v1/auth/confirm-email",
            new { userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword });

        Assert.Equal(HttpStatusCode.NoContent, confirmation.StatusCode);
        await ProblemAssert.IsProblemAsync(replay, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(0, await scenario.ScalarAsync(
            "select count(*) from identity.user_tokens where user_id = $1 and login_provider = 'Duora.EmailConfirmation'",
            proof.UserId));
    }
}
