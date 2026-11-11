using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
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
/// O teto de repetições do SDK da AWS na selfie é decisão do código (STS e Rekognition: 2 repetições, 3 chamadas
/// HTTP), não do padrão do pacote nem do ambiente do processo (<c>AWS_MAX_ATTEMPTS</c>, <c>AWS_RETRY_MODE</c>). Sem
/// rede: o SDK real fala com um transporte sintético que responde 503. Mexer no ambiente é global ao processo; os casos
/// desta classe rodam em série (xUnit) e nenhum outro teste lê essas duas variáveis, por isso não há trava.
/// </summary>
public sealed class SelfieAwsClientsRetryTests
{
    private const int ExpectedRetries = 2;

    private const int ExpectedHttpCalls = ExpectedRetries + 1;

    private const string MaxAttemptsVariable = "AWS_MAX_ATTEMPTS";

    private const string RetryModeVariable = "AWS_RETRY_MODE";

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
    public async Task TheRetryCeilingComesFromTheCodeAndNotFromTheEnvironment(
        string client, string? maxAttempts, string? retryMode)
    {
        var transport = new RecordingAwsTransport(_ => ServiceUnavailable(client));
        var clients = new SelfieAwsClients(
            Options.Create(new SelfieAwsCredentialOptions
            {
                AwsAccessKeyId = "SYNTHETICACCESSKEYID",
                AwsSecretAccessKey = "synthetic-secret-value",
            }),
            Options.Create(new SelfieProviderOptions()),
            transport);
        RequestRetryMode effectiveMode;
        int effectiveRetries;
        int sdkDefaultRetries;

        var previousAttempts = Environment.GetEnvironmentVariable(MaxAttemptsVariable);
        var previousMode = Environment.GetEnvironmentVariable(RetryModeVariable);
        try
        {
            Environment.SetEnvironmentVariable(MaxAttemptsVariable, maxAttempts);
            Environment.SetEnvironmentVariable(RetryModeVariable, retryMode);
            ResetSdkEnvironmentCache();
            sdkDefaultRetries = new AmazonRekognitionConfig().MaxErrorRetry;
            using var created = Create(clients, client);
            effectiveRetries = created.Config.MaxErrorRetry;
            effectiveMode = created.Config.RetryMode;
            await Assert.ThrowsAnyAsync<AmazonServiceException>(() => CallAsync(created));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxAttemptsVariable, previousAttempts);
            Environment.SetEnvironmentVariable(RetryModeVariable, previousMode);
            ResetSdkEnvironmentCache();
        }

        if (maxAttempts is not null)
        {
            // Canário do reset: um config sem override precisa enxergar o ambiente; senão o caso não prova nada.
            Assert.Equal(int.Parse(maxAttempts, CultureInfo.InvariantCulture) - 1, sdkDefaultRetries);
        }

        Assert.Equal(ExpectedRetries, effectiveRetries);
        Assert.Equal(RequestRetryMode.Standard, effectiveMode);
        Assert.Equal(ExpectedHttpCalls, transport.Calls.Count);
    }

    private static AmazonServiceClient Create(SelfieAwsClients clients, string client) =>
        client == "sts"
            ? (AmazonServiceClient)clients.CreateSecurityTokenService()
            : (AmazonServiceClient)clients.CreateRekognition();

    /// <summary>
    /// O SDK lê <c>AWS_MAX_ATTEMPTS</c> e <c>AWS_RETRY_MODE</c> uma vez por processo e guarda o resultado; sem este
    /// reset, mudar o ambiente no meio do teste não chega ao cliente e o teste passaria sem provar nada. Falha alto se
    /// o SDK mudar o nome do tipo ou do método; o canário no teste pega o reset que vira no-op.
    /// </summary>
    private static void ResetSdkEnvironmentCache()
    {
        var factory = typeof(AmazonServiceClient).Assembly.GetTypes()
            .Single(type => type.Name == "FallbackInternalConfigurationFactory");
        var reset = factory.GetMethod("Reset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "FallbackInternalConfigurationFactory.Reset não existe mais no SDK.");
        reset.Invoke(null, null);
    }

    private static async Task CallAsync(AmazonServiceClient client)
    {
        if (client is IAmazonSecurityTokenService sts)
        {
            var request = new AssumeRoleRequest
            {
                RoleArn = "arn:aws:iam::000000000000:role/synthetic",
                RoleSessionName = "synthetic",
            };
            await sts.AssumeRoleAsync(request, Ct);
        }
        else
        {
            var request = new CreateFaceLivenessSessionRequest { ClientRequestToken = "synthetic" };
            await ((IAmazonRekognition)client).CreateFaceLivenessSessionAsync(request, Ct);
        }
    }

    private static HttpResponseMessage ServiceUnavailable(string client) =>
        client == "sts"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "<ErrorResponse><Error><Type>Receiver</Type><Code>ServiceUnavailable</Code>"
                    + "<Message>synthetic</Message></Error><RequestId>r</RequestId></ErrorResponse>",
                    Encoding.UTF8,
                    new MediaTypeHeaderValue("text/xml")),
            }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """{"__type":"ServiceUnavailableException","Message":"synthetic"}""",
                    Encoding.UTF8,
                    new MediaTypeHeaderValue("application/x-amz-json-1.1")),
            };
}
