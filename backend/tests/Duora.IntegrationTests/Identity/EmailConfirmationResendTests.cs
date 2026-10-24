using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

public sealed class EmailConfirmationResendTests(PostgresFixture postgres)
{
    private const string Requests = "/api/v1/auth/email-confirmation-requests";

    [Fact]
    public async Task ResendPreservesPasswordConsentsAndPendingDeadlineAndInvalidatesTheLegacyProof()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var oldProof = await scenario.CreateLegacyConfirmationProofAsync(email);
        var before = await SnapshotAsync(scenario, oldProof.UserId);

        using var requested = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        var replacement = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        var after = await SnapshotAsync(scenario, oldProof.UserId);
        using var stale = await ConfirmAsync(browser, oldProof);
        using var confirmed = await ConfirmAsync(browser, replacement);

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal(before, after);
        Assert.Equal(oldProof.UserId, replacement.UserId);
        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("confirmed")]
    [InlineData("wrong-password")]
    [InlineData("expired")]
    public async Task UnavailableRequestsAreNeutralAndCannotIssueAProofOrCreateAnAccount(string kind)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Time = time };
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        if (kind == "confirmed")
        {
            email = (await scenario.CreateConfirmedAccountAsync()).Email;
        }
        else if (kind != "unknown")
        {
            using var registration = await IdentityScenario.RegisterAsync(browser, email);
            if (kind == "expired")
            {
                time.Advance(TimeSpan.FromHours(72));
            }
        }
        await browser.FetchCsrfTokenAsync();
        var accounts = await scenario.CountUsersAsync(email);
        var emails = await scenario.CountOutboxEventsAsync(email);
        var started = Stopwatch.StartNew();

        using var response = await browser.PostJsonAsync(Requests, new
        {
            email,
            password = kind == "wrong-password" ? "uma senha que não confere" : IdentityScenario.ValidPassword,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(response.Headers.Location);
        Assert.Equal(accounts, await scenario.CountUsersAsync(email));
        Assert.Equal(emails, await scenario.CountOutboxEventsAsync(email));
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task RegistrationAndResendShareThePersistentIssuanceBudgetAcrossRestarts()
    {
        var email = IdentityScenario.NewEmail();
        await using (var factory = new DuoraWebFactory(postgres.ConnectionString))
        {
            using var browser = new Browser(factory);
            using var registration = await IdentityScenario.RegisterAsync(browser, email);
        }
        for (var i = 0; i < 3; i++)
        {
            await using var factory = new DuoraWebFactory(postgres.ConnectionString);
            using var browser = new Browser(factory);
            await browser.FetchCsrfTokenAsync();
            using var response = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        await using var read = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, read);
        Assert.Equal(3, await scenario.CountOutboxEventsAsync(email));
        using var owner = new Browser(read);
        await owner.FetchCsrfTokenAsync();
        using var latest = await ConfirmAsync(owner, await scenario.ReadLatestLinkAsync(email, "EmailConfirmation"));
        Assert.Equal(HttpStatusCode.NoContent, latest.StatusCode);
    }

    [Fact]
    public async Task ConfirmationAndResendCannotIssueAfterConfirmationCommits()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var first = new Browser(factory);
        using var second = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(first, email);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        await second.FetchCsrfTokenAsync();
        var resendTask = second.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        var confirmationTask = ConfirmAsync(first, proof);
        await Task.WhenAll(resendTask, confirmationTask);
        using var resend = await resendTask;
        using var confirmation = await confirmationTask;
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        if (confirmation.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Equal(1, await scenario.CountOutboxEventsAsync(email));
        }
        else
        {
            await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
            Assert.Equal(2, await scenario.CountOutboxEventsAsync(email));
            using var latest = await ConfirmAsync(first, await scenario.ReadLatestLinkAsync(email, "EmailConfirmation"));
            Assert.Equal(HttpStatusCode.NoContent, latest.StatusCode);
        }
    }

    [Fact]
    public async Task ResendUsesTheRegistrationAccountLimitAndReturnsRetryAfter()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["RateLimiting:RegisterPerAccount:PermitLimit"] = "1" },
        };
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        using var response = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Browser browser, AccountLink proof) =>
        browser.PostJsonAsync("/api/v1/auth/confirm-email", new
        {
            userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword,
        });

    private static async Task<string> SnapshotAsync(IdentityScenario scenario, Guid id) =>
        await scenario.ScalarTextAsync("""
            select md5(concat(u.password_hash,'|',u.created_at::text,'|',
              (select jsonb_agg(to_jsonb(c) order by c.id)::text
               from identity.consent_records c where c.user_id=u.id)))
            from identity.users u where u.id=$1
            """, id);
}
