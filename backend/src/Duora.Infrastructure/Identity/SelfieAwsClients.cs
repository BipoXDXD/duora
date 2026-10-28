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
    public IAmazonSecurityTokenService CreateSecurityTokenService() =>
        new AmazonSecurityTokenServiceClient(credentials.Value.ToAwsCredentials(), Configure(new AmazonSecurityTokenServiceConfig()));

    public IAmazonRekognition CreateRekognition() =>
        new AmazonRekognitionClient(credentials.Value.ToAwsCredentials(), Configure(new AmazonRekognitionConfig()));

    private T Configure<T>(T config)
        where T : ClientConfig
    {
        config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(provider.Value.Region);
        if (transport is not null)
        {
            config.HttpClientFactory = transport;
        }

        return config;
    }
}
