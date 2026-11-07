using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Duora.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// POST /me/selfie-verifications/{id}/capture pela borda HTTP real: a credencial do arquivo de segredo assina o
/// sts:AssumeRole (nunca a cadeia padrão do SDK), o segredo canário não aparece em resposta nem em log, a falha da
/// AWS vira 503 sem a mensagem dela, outra conta recebe 404 sem dado alheio e sem chamar a AWS, e o limite por conta
/// segura N+1 chamadas mesmo trocando de IP. STS parado ou fora do ar vira o mesmo 503 sem mexer na tentativa, e quem
/// desconectou não conta como indisponibilidade. Transporte AWS sintético: nada sai para a rede. O corte de 10 s usa o
/// relógio real, então os casos de stall esperam esse tempo de verdade, sem CPU.
/// </summary>
public sealed class SelfieCaptureEndpointTests(PostgresFixture postgres)
{
    /// <summary>Abaixo do corte de 10 s: prova que a chamada ficou presa até ele, sem medir precisão de timer.</summary>
    private static readonly TimeSpan BelowTheAssumeRoleTimeout = TimeSpan.FromSeconds(9);

    /// <summary>Falha em vez de esperar os minutos que as tentativas do SDK levariam sem o corte.</summary>
    private static readonly TimeSpan CaptureCeiling = TimeSpan.FromSeconds(30);

    private const string UnavailableLog = "Selfie capture credentials unavailable";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AssumeRoleSuccess = """
        <AssumeRoleResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
          <AssumeRoleResult>
            <Credentials>
              <AccessKeyId>SYNTHETICTEMPKEYID</AccessKeyId>
              <SecretAccessKey>synthetic-temporary-secret</SecretAccessKey>
              <SessionToken>synthetic-session-token</SessionToken>
              <Expiration>2099-01-01T00:00:00Z</Expiration>
            </Credentials>
            <AssumedRoleUser>
              <AssumedRoleId>AROASYNTHETIC:selfie</AssumedRoleId>
              <Arn>arn:aws:sts::000000000000:assumed-role/synthetic/selfie</Arn>
            </AssumedRoleUser>
          </AssumeRoleResult>
          <ResponseMetadata><RequestId>synthetic</RequestId></ResponseMetadata>
        </AssumeRoleResponse>
        """;

    private const string AccessDenied = """
        <ErrorResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
          <Error><Type>Sender</Type><Code>AccessDenied</Code>
          <Message>User arn:aws:iam::000000000000:user/synthetic-leak is not authorized to perform sts:AssumeRole</Message></Error>
          <RequestId>synthetic</RequestId>
        </ErrorResponse>
        """;

    [Fact]
    public async Task CaptureIsSignedWithTheSecretFileKeyAndNeverEchoesTheBackendSecret()
    {
        var keyId = $"CANARYKEYID{Guid.NewGuid():N}"[..32].ToUpperInvariant();
        var secret = $"CANARY-{Guid.NewGuid()}";
        var transport = new RecordingAwsTransport(_ => RecordingAwsTransport.Xml(HttpStatusCode.OK, AssumeRoleSuccess));
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory(transport, logs, keyId, secret);
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(owner);

        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/capture", new { });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SYNTHETICTEMPKEYID", body, StringComparison.Ordinal);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("sts.us-east-1.amazonaws.com", call.Host);
        Assert.Contains($"Credential={keyId}/", call.Authorization, StringComparison.Ordinal);
        Assert.Contains("/us-east-1/sts/", call.Authorization, StringComparison.Ordinal);
        AssertNoLeak(secret, body, response, logs);
    }

    [Fact]
    public async Task AssumeRoleFailureIs503WithoutTheAwsMessageOrTheSecret()
    {
        var secret = $"CANARY-{Guid.NewGuid()}";
        var transport = new RecordingAwsTransport(_ => RecordingAwsTransport.Xml(HttpStatusCode.Forbidden, AccessDenied));
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory(transport, logs, "SYNTHETICACCESSKEYID", secret);
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(owner);

        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/capture", new { });

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "SELFIE_UNAVAILABLE");
        var body = problem.ToJsonString();
        Assert.DoesNotContain("synthetic-leak", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessDenied", body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, entry => entry.Text.Contains("AccessDenied", StringComparison.Ordinal)
            && entry.Exception is null);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains("synthetic-leak", StringComparison.Ordinal)
            || entry.Exception?.ToString().Contains("synthetic-leak", StringComparison.Ordinal) == true);
        AssertNoLeak(secret, body, response, logs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AStalledAssumeRoleIs503UnderOneDeadlineThatCoversTheSdkRetries(int resetsBeforeTheStall)
    {
        var calls = 0;
        var transport = new RecordingAwsTransport((_, cancellationToken) =>
            Interlocked.Increment(ref calls) <= resetsBeforeTheStall
                ? throw new HttpRequestException("reset", new IOException("reset"))
                : Stall(cancellationToken));
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory(transport, logs, "SYNTHETICACCESSKEYID", "synthetic");
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(owner);

        var watch = Stopwatch.StartNew();
        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/capture", new { })
            .WaitAsync(CaptureCeiling, Ct);
        var elapsed = watch.Elapsed;

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "SELFIE_UNAVAILABLE");
        Assert.InRange(elapsed, BelowTheAssumeRoleTimeout, CaptureCeiling);
        Assert.Equal(resetsBeforeTheStall + 1, transport.Calls.Count);
        var trace = Assert.Single(logs.Entries, entry => entry.Text.Contains(UnavailableLog, StringComparison.Ordinal));
        Assert.Contains("CanceledException", trace.Text, StringComparison.Ordinal);
        Assert.Null(trace.Exception);
        await AssertAttemptUntouchedAsync(scenario, factory, attempt, owner.UserId);
    }

    [Theory]
    [InlineData("IOException")]
    [InlineData("TimeoutException")]
    [InlineData("HttpRequestException")]
    public async Task ATransportFailureIs503WithoutItsMessageOrTheSecret(string failure)
    {
        var secret = $"CANARY-{Guid.NewGuid()}";
        var message = $"CANARY-transport-{Guid.NewGuid()}";
        var transport = new RecordingAwsTransport((_, _) => throw TransportFailure(failure, message));
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory(transport, logs, "SYNTHETICACCESSKEYID", secret);
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(owner);

        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/capture", new { })
            .WaitAsync(CaptureCeiling, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "SELFIE_UNAVAILABLE");
        var body = problem.ToJsonString();
        var trace = Assert.Single(logs.Entries, entry => entry.Text.Contains(UnavailableLog, StringComparison.Ordinal));
        Assert.Contains(failure, trace.Text, StringComparison.Ordinal);
        Assert.Null(trace.Exception);
        AssertNoLeak(secret, body, response, logs);
        AssertNoLeak(message, body, response, logs);
        await AssertAttemptUntouchedAsync(scenario, factory, attempt, owner.UserId);
    }

    [Fact]
    public async Task ACallerThatDisconnectsIsNotReportedAsUnavailable()
    {
        using var disconnect = new CancellationTokenSource();
        var transport = new RecordingAwsTransport(async (_, cancellationToken) =>
        {
            await disconnect.CancelAsync();
            return await Stall(cancellationToken);
        });
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory(transport, logs, "SYNTHETICACCESSKEYID", "synthetic", services:
            collection => collection.AddSingleton<IStartupFilter>(new CaptureDisconnectFilter(disconnect.Token)));
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(owner);

        var watch = Stopwatch.StartNew();
        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/capture", new { })
            .WaitAsync(CaptureCeiling, Ct);

        Assert.InRange(watch.Elapsed, TimeSpan.Zero, BelowTheAssumeRoleTimeout);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(transport.Calls);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(UnavailableLog, StringComparison.Ordinal));
        await AssertAttemptUntouchedAsync(scenario, factory, attempt, owner.UserId);
    }

    [Fact]
    public async Task OtherAccountGets404WithoutTheOwnersAttemptAndWithoutCallingAws()
    {
        var transport = new RecordingAwsTransport(_ => RecordingAwsTransport.Xml(HttpStatusCode.OK, AssumeRoleSuccess));
        await using var factory = Factory(transport, new CapturingLoggerProvider(), "SYNTHETICACCESSKEYID", "synthetic");
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var other = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(other);

        using var response = await browser.PostJsonAsync($"/api/v1/me/selfie-verifications/{attempt}/capture", new { });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(attempt.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.UserId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-reference", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETICTEMPKEYID", body, StringComparison.Ordinal);
        Assert.Empty(transport.Calls);
        Assert.Equal("Capturing", await scenario.ScalarTextAsync(
            "select state from identity.selfie_verifications where id = $1", attempt));
    }

    [Fact]
    public async Task AccountLimitRejectsTheExtraCaptureEvenFromAnotherAddress()
    {
        var transport = new RecordingAwsTransport(_ => RecordingAwsTransport.Xml(HttpStatusCode.OK, AssumeRoleSuccess));
        await using var factory = Factory(transport, new CapturingLoggerProvider(), "SYNTHETICACCESSKEYID", "synthetic",
            new() { ["RateLimiting:SelfieCapturePerAccount:PermitLimit"] = "3" }, RemoteAddressFromTestHeader.Register);
        var scenario = new IdentityScenario(postgres, factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        var neighbour = await scenario.CreateConfirmedAccountAsync();
        var attempt = await CapturingAttemptAsync(scenario, factory, owner.UserId);
        using var browser = await scenario.SignedInBrowserAsync(owner);
        var path = $"/api/v1/me/selfie-verifications/{attempt}/capture";
        for (var i = 1; i <= 3; i++)
        {
            using var allowed = await browser.PostJsonAsync(path, new { }, RemoteAddressFromTestHeader.From($"198.51.100.{i}"));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var limited = await browser.PostJsonAsync(path, new { }, RemoteAddressFromTestHeader.From("203.0.113.9"));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        Assert.Equal(3, transport.Calls.Count);
        using var other = await scenario.SignedInBrowserAsync(neighbour);
        using var neighbourResponse = await other.PostJsonAsync(path, new { }, RemoteAddressFromTestHeader.From("203.0.113.9"));
        Assert.Equal(HttpStatusCode.NotFound, neighbourResponse.StatusCode);
    }

    [Fact]
    public async Task WorkerRekognitionClientFromTheContainerSignsWithTheSecretFileKey()
    {
        var keyId = $"CANARYWORKER{Guid.NewGuid():N}"[..32].ToUpperInvariant();
        var transport = new RecordingAwsTransport(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"__type":"ValidationException","message":"synthetic"}""",
                System.Text.Encoding.UTF8, "application/x-amz-json-1.1"),
        });
        await using var factory = Factory(transport, new CapturingLoggerProvider(), keyId, $"CANARY-{Guid.NewGuid()}");
        var provider = factory.Services.GetRequiredService<ISelfieProvider>();

        await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => provider.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        var call = Assert.Single(transport.Calls);
        Assert.Equal("rekognition.us-east-1.amazonaws.com", call.Host);
        Assert.Contains($"Credential={keyId}/", call.Authorization, StringComparison.Ordinal);
    }

    private DuoraWebFactory Factory(RecordingAwsTransport transport, CapturingLoggerProvider logs, string keyId, string secret,
        Dictionary<string, string?>? extra = null, Action<IServiceCollection>? services = null)
    {
        var settings = SelfieTestSettings.Enabled();
        settings["SelfieVerification:AwsAccessKeyId"] = keyId;
        settings["SelfieVerification:AwsSecretAccessKey"] = secret;
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return new DuoraWebFactory(postgres.ConnectionString, logs)
        {
            Settings = settings,
            TestServices = collection =>
            {
                collection.AddSingleton<HttpClientFactory>(transport);
                services?.Invoke(collection);
            },
        };
    }

    private static async Task<Guid> CapturingAttemptAsync(IdentityScenario scenario, DuoraWebFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<Duora.Infrastructure.Identity.SelfieVerificationService>()
            .RequestAsync(userId, TestContext.Current.CancellationToken);
        var id = Assert.IsType<Duora.Infrastructure.Identity.SelfieRequestOutcome.Accepted>(outcome).Status.Id!.Value;
        await scenario.ScalarAsync("""
            update identity.selfie_verifications
            set state = 'Capturing', provider_reference = 'synthetic-reference-' || id::text, expires_at = now() + interval '3 minutes'
            where id = $1 returning 1
            """, id);
        return id;
    }

    private static async Task<HttpResponseMessage> Stall(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage();
    }

    private static Exception TransportFailure(string name, string message) => name switch
    {
        "IOException" => new HttpRequestException(message, new IOException(message)),
        "TimeoutException" => new TaskCanceledException(message, new TimeoutException(message)),
        "HttpRequestException" => new HttpRequestException(message),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Um 503 do /capture não reprova, não libera a conta e não encerra a tentativa: ela segue aberta para nova chamada.</summary>
    private static async Task AssertAttemptUntouchedAsync(IdentityScenario scenario, DuoraWebFactory factory, Guid attempt, Guid userId)
    {
        Assert.Equal("Capturing|open", await scenario.ScalarTextAsync("""
            select state || case when completed_at is null then '|open' else '|closed' end
            from identity.selfie_verifications where id = $1
            """, attempt));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", userId));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and status = 'PendingEligibility'", userId));
        Assert.False(await SelfieConcurrencyHarness.EligibleAsync(factory, userId, Ct));
    }

    private static void AssertNoLeak(string secret, string body, HttpResponseMessage response, CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers),
            header => header.Value.Any(value => value.Contains(secret, StringComparison.Ordinal)));
        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(secret, StringComparison.Ordinal)
            || entry.Exception?.ToString().Contains(secret, StringComparison.Ordinal) == true);
    }

    /// <summary>Simula a queda do cliente pelo <c>RequestAborted</c> que a aplicação enxerga, com a resposta ainda observável.</summary>
    private sealed class CaptureDisconnectFilter(CancellationToken aborted) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proceed) =>
            {
                if (context.Request.Path.Value?.EndsWith("/capture", StringComparison.Ordinal) == true)
                {
                    context.RequestAborted = aborted;
                }

                return proceed();
            });
            next(app);
        };
    }
}
