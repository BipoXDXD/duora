using System.Net;
using Duora.Infrastructure.Email;
using Duora.IntegrationTests.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Outbox;

public sealed class ResendEmailFlowTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pt-BR", "Seu código de confirmação do Duora")]
    [InlineData("en-US", "Your Duora confirmation code")]
    public async Task RegistrationConfirmationLoginAndResetUseMailReceivedOverHttp(string language, string subject)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await using var sink = await ResendMailSink.StartAsync(Ct);
        var worker = Start(harness, sink);
        await using var factory = new DuoraWebFactory(harness.ConnectionString) { Time = harness.Time };
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        var email = IdentityScenario.NewEmail();
        using var registered = await browser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(email), new MutationHeaders(Extra: new Dictionary<string, string> { ["Accept-Language"] = language }));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        using var unconfirmed = await IdentityScenario.LoginAsync(browser, email, IdentityScenario.ValidPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmed.StatusCode);
        Assert.Equal(1, await worker.DispatchBatchAsync(Ct));
        var confirmation = Assert.Single(sink.Requests);
        Assert.Equal(subject, confirmation.Body.GetProperty("subject").GetString());
        Assert.Equal(email, confirmation.Body.GetProperty("to")[0].GetString());
        var proof = Link(confirmation, "/confirm-email");
        Assert.Matches("^[0-9]{8}$", proof.Token);
        using var confirmed = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new { userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        using var loggedIn = await IdentityScenario.LoginAsync(browser, email, IdentityScenario.ValidPassword);
        Assert.Equal(HttpStatusCode.NoContent, loggedIn.StatusCode);

        using var anonymous = new Browser(factory);
        await anonymous.FetchCsrfTokenAsync();
        using var requested = await anonymous.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email }, new MutationHeaders(Extra: new Dictionary<string, string> { ["Accept-Language"] = language }));
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal(1, await worker.DispatchBatchAsync(Ct));
        var reset = Link(sink.Requests.Last(), "/reset-password");
        const string NewPassword = "nova frase sintetica bastante longa";
        using var changed = await anonymous.PostJsonAsync("/api/v1/auth/password-resets", new { userId = reset.UserId, token = reset.Token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        using var oldSession = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var oldPassword = await IdentityScenario.LoginAsync(anonymous, email, IdentityScenario.ValidPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        using var newLogin = await IdentityScenario.LoginAsync(anonymous, email, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, newLogin.StatusCode);
        Assert.Equal(1, await worker.DispatchBatchAsync(Ct));
        Assert.Equal(3, sink.Requests.Count);
        Assert.Empty(harness.MailFiles());
        foreach (var mail in sink.Requests)
        {
            var row = await harness.ReadDeliveryAsync(Guid.ParseExact(mail.Key, "N"), Ct);
            Assert.Equal("Delivered", row.Status);
            Assert.Equal("{}", row.Payload);
        }
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task FailedProviderResponseRetriesIdenticalRequestAfterBackoff(int status)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await using var sink = await ResendMailSink.StartAsync(Ct);
        sink.Respond = _ => Task.FromResult(status);
        var id = await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "synthetic-token", Ct);
        var firstWorker = Start(harness, sink);
        await firstWorker.DispatchBatchAsync(Ct);
        var pending = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal("HttpRequestException", pending.LastError);
        Assert.Equal(0, await firstWorker.DispatchBatchAsync(Ct));
        sink.Respond = _ => Task.FromResult(200);
        harness.Time.Advance(pending.AvailableAt - harness.Time.GetUtcNow());
        var recoveredWorker = Start(harness, sink);
        await recoveredWorker.DispatchBatchAsync(Ct);
        var delivered = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal("Delivered", delivered.Status);
        Assert.Equal(2, delivered.Attempts);
        var requests = sink.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Key, requests[1].Key);
        Assert.Equal(requests[0].Body.GetRawText(), requests[1].Body.GetRawText());
    }

    [Fact]
    public async Task InterruptedWorkerLeavesLeaseRecoverableWithSameKey()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await using var sink = await ResendMailSink.StartAsync(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Respond = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 200;
        };
        var id = await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "synthetic-token", Ct);
        var worker = Start(harness, sink);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var dispatch = worker.DispatchBatchAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        var abandoned = await harness.ReadDeliveryAsync(id, Ct);
        Assert.NotNull(abandoned.LeaseExpiresAt);
        sink.Respond = _ => Task.FromResult(200);
        harness.Time.Advance(abandoned.LeaseExpiresAt.Value - harness.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        var replacement = Start(harness, sink);
        await replacement.DispatchBatchAsync(Ct);
        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(id, Ct)).Status);
        Assert.Equal(2, sink.Requests.Count);
        Assert.Single(sink.Requests.Select(r => r.Key).Distinct());
    }

    [Fact]
    public async Task ProviderTimeoutReschedulesInsteadOfAcknowledging()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await using var sink = await ResendMailSink.StartAsync(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Respond = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 200;
        };
        var id = await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "synthetic-token", Ct);
        var worker = Start(harness, sink);
        var dispatch = worker.DispatchBatchAsync(Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        harness.Time.Advance(TimeSpan.FromSeconds(31));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var pending = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(1, pending.Attempts);
        Assert.NotEqual("{}", pending.Payload);
        Assert.Single(sink.Requests);
    }

    [Fact]
    public async Task ConcurrentWorkersSendOneRequestForOneDelivery()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await using var sink = await ResendMailSink.StartAsync(Ct);
        var id = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "12345678", Ct);
        var first = Start(harness, sink);
        var second = Start(harness, sink);
        await Task.WhenAll(first.DispatchBatchAsync(Ct), second.DispatchBatchAsync(Ct));
        Assert.Single(sink.Requests);
        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(id, Ct)).Status);
    }

    private static TransactionalEmailDispatcher Start(EmailOutboxHarness harness, ResendMailSink sink) => harness.StartWorker(
        ["--Email:Provider=Resend", "--Email:Resend:ApiKey=synthetic-only", "--Email:Resend:From=Duora <sender@example.test>", "--Email:PickupDirectory="],
        configureServices: services => services.AddKeyedSingleton<HttpClient>("Resend", (_, _) => sink.CreateClient()));

    private static AccountLink Link(CapturedMail mail, string path)
    {
        var link = mail.Text.Split('\n', StringSplitOptions.TrimEntries).Single(line => line.StartsWith(EmailOutboxHarness.PublicOrigin + path + "#", StringComparison.Ordinal));
        var fields = QueryHelpers.ParseQuery(new Uri(link).Fragment[1..]);
        var token = fields["token"].ToString();
        if (token == "code")
        {
            token = System.Text.RegularExpressions.Regex.Match(mail.Text, @"(?<!\d)\d{8}(?!\d)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
        }
        return new AccountLink(Guid.Parse(fields["userId"].ToString()), token);
    }
}
