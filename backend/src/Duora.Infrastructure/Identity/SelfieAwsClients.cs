using Amazon.Rekognition;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Único lugar que cria os clientes AWS da selfie: sempre com a credencial do arquivo de segredo e a região aprovada,
/// nunca pela cadeia padrão do SDK (AWS_*, perfis, IMDS). O transporte só é trocado em teste.
/// </summary>
internal sealed class SelfieAwsClients(
    IOptions<SelfieAwsCredentialOptions> credentials, IOptions<SelfieProviderOptions> provider, HttpClientFactory? transport)
{
    /// <summary>
    /// Repetições do SDK por chamada (2 repetições = 3 chamadas HTTP, o que já se mede em 5xx e throttling). Fixadas
    /// aqui porque, sem isso, <c>AWS_MAX_ATTEMPTS</c>, <c>AWS_RETRY_MODE</c> ou o padrão de uma versão nova do pacote
    /// mudariam o teto em silêncio. O modo Standard espera com backoff entre as tentativas (até ~3 s por chamada) e
    /// consome a cota de repetições do SDK, compartilhada no processo: esgotada a cota, o SDK não repete. O web não
    /// repete o AssumeRole por fora; o worker repete o Rekognition a cada 5 s, dentro do corte de 10 s. Mexer nestes
    /// dois números muda a carga sobre o fornecedor: decisão do dono.
    /// </summary>
    private const int SecurityTokenServiceMaxRetries = 2;

    private const int RekognitionMaxRetries = 2;

    public IAmazonSecurityTokenService CreateSecurityTokenService() =>
        new AmazonSecurityTokenServiceClient(
            credentials.Value.ToAwsCredentials(),
            Configure(new AmazonSecurityTokenServiceConfig(), SecurityTokenServiceMaxRetries));

    public IAmazonRekognition CreateRekognition() =>
        new AmazonRekognitionClient(
            credentials.Value.ToAwsCredentials(), Configure(new AmazonRekognitionConfig(), RekognitionMaxRetries));

    private T Configure<T>(T config, int maxRetries)
        where T : ClientConfig
    {
        config.MaxErrorRetry = maxRetries;
        config.RetryMode = RequestRetryMode.Standard;
        config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(provider.Value.Region);
        if (transport is not null)
        {
            config.HttpClientFactory = transport;
        }

        return config;
    }
}
