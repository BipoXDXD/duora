using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// O teto de repetições do SDK da AWS na selfie é decisão do código (STS e Rekognition: 2 repetições, 3 chamadas HTTP),
/// não do padrão do pacote nem do ambiente do processo (<c>AWS_MAX_ATTEMPTS</c>, <c>AWS_RETRY_MODE</c>). Sem rede: o SDK
/// real fala com um transporte sintético que responde 503. Mexer no ambiente é global ao processo, então a janela é
/// curta e passa por uma trava; os demais testes do SDK não leem essas variáveis.
/// </summary>
public sealed class SelfieAwsClientsRetryTests
{
    private const int ExpectedRetries = 2;

    private const int ExpectedHttpCalls = ExpectedRetries + 1;

    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sts", null, null)]
    [InlineData("rekognition", null, null)]
    [InlineData("sts", "4", null)]
    [InlineData("rekognition", "4", null)]
    [InlineData("sts", "1", "legacy")]
    [InlineData("rekognition", "1", "legacy")]
    [InlineData("sts", "4", "adaptive")]
    [InlineData("rekognition", "4", "adaptive")]
    public async Task TheRetryCeilingComesFromTheCodeAndNotFromTheEnvironment(string client, string? maxAttempts, string? retryMode)
    {
        var transport = new RecordingAwsTransport(_ => ServiceUnavailable(client));
        var clients = new SelfieAwsClients(
            Options.Create(new SelfieAwsCredentialOptions { AwsAccessKeyId = "SYNTHETICACCESSKEYID", AwsSecretAccessKey = "synthetic-secret-value" }),
            Options.Create(new SelfieProviderOptions()),
            transport);
        RequestRetryMode effectiveMode;
        int effectiveRetries;

        await EnvironmentGate.WaitAsync(Ct);
        var previousAttempts = Environment.GetEnvironmentVariable("AWS_MAX_ATTEMPTS");
        var previousMode = Environment.GetEnvironmentVariable("AWS_RETRY_MODE");
        try
        {
            Environment.SetEnvironmentVariable("AWS_MAX_ATTEMPTS", maxAttempts);
            Environment.SetEnvironmentVariable("AWS_RETRY_MODE", retryMode);
            ResetSdkEnvironmentCache();
            using var created = client == "sts" ? (AmazonServiceClient)clients.CreateSecurityTokenService() : (AmazonServiceClient)clients.CreateRekognition();
            effectiveRetries = created.Config.MaxErrorRetry;
            effectiveMode = created.Config.RetryMode;
            await Assert.ThrowsAnyAsync<AmazonServiceException>(() => CallAsync(created));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_MAX_ATTEMPTS", previousAttempts);
            Environment.SetEnvironmentVariable("AWS_RETRY_MODE", previousMode);
            ResetSdkEnvironmentCache();
            EnvironmentGate.Release();
        }

        Assert.Equal(ExpectedRetries, effectiveRetries);
        Assert.Equal(RequestRetryMode.Standard, effectiveMode);
        Assert.Equal(ExpectedHttpCalls, transport.Calls.Count);
    }

    /// <summary>
    /// O SDK lê <c>AWS_MAX_ATTEMPTS</c> e <c>AWS_RETRY_MODE</c> uma vez por processo e guarda o resultado; sem este reset,
    /// mudar o ambiente no meio do teste não chega ao cliente e o teste passaria sem provar nada. Falha alto se o SDK
    /// mudar o nome do tipo ou do método.
    /// </summary>
    private static void ResetSdkEnvironmentCache()
    {
        var factory = typeof(AmazonServiceClient).Assembly.GetTypes()
            .Single(type => type.Name == "FallbackInternalConfigurationFactory");
        var reset = factory.GetMethod("Reset", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FallbackInternalConfigurationFactory.Reset não existe mais no SDK.");
        reset.Invoke(null, null);
    }

    private static async Task CallAsync(AmazonServiceClient client)
    {
        if (client is IAmazonSecurityTokenService sts)
        {
            await sts.AssumeRoleAsync(new AssumeRoleRequest { RoleArn = "arn:aws:iam::000000000000:role/synthetic", RoleSessionName = "synthetic" }, Ct);
        }
        else
        {
            await ((IAmazonRekognition)client).CreateFaceLivenessSessionAsync(new CreateFaceLivenessSessionRequest { ClientRequestToken = "synthetic" }, Ct);
        }
    }

    private static HttpResponseMessage ServiceUnavailable(string client)
    {
        var response = client == "sts"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "<ErrorResponse><Error><Type>Receiver</Type><Code>ServiceUnavailable</Code><Message>synthetic</Message></Error><RequestId>r</RequestId></ErrorResponse>",
                    Encoding.UTF8,
                    new MediaTypeHeaderValue("text/xml")),
            }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"__type":"ServiceUnavailableException","Message":"synthetic"}""", Encoding.UTF8, new MediaTypeHeaderValue("application/x-amz-json-1.1")),
            };
        return response;
    }
}
