using System.Net;
using Amazon.Runtime;
using Duora.Core.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// POST /me/selfie-verifications/{id}/capture pela borda HTTP real: a credencial do arquivo de segredo assina o
/// sts:AssumeRole (nunca a cadeia padrão do SDK), o segredo canário não aparece em resposta nem em log, a falha da
/// AWS vira 503 sem a mensagem dela, outra conta recebe 404 sem dado alheio e sem chamar a AWS, e o limite por conta
/// segura N+1 chamadas mesmo trocando de IP. Transporte AWS sintético: nada sai para a rede.
/// </summary>
public sealed class SelfieCaptureEndpointTests(PostgresFixture postgres)
{
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

    private static void AssertNoLeak(string secret, string body, HttpResponseMessage response, CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers),
            header => header.Value.Any(value => value.Contains(secret, StringComparison.Ordinal)));
        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(secret, StringComparison.Ordinal)
            || entry.Exception?.ToString().Contains(secret, StringComparison.Ordinal) == true);
    }
}
