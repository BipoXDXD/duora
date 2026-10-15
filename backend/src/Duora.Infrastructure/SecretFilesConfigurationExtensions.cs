using Microsoft.Extensions.Configuration;

namespace Duora.Infrastructure;

public static class SecretFilesConfigurationExtensions
{
    /// <summary>Chave que aponta o diretório de segredos; em produção, <c>SecretsDirectory=/run/secrets</c>.</summary>
    public const string SecretsDirectoryKey = "SecretsDirectory";

    /// <summary>
    /// Lê um segredo por arquivo do diretório configurado (Compose secrets). O nome do arquivo é a chave com
    /// <c>__</c> no lugar de <c>:</c>, como nas variáveis de ambiente: <c>ConnectionStrings__Duora</c>.
    /// Adicionado por último, o arquivo vence linha de comando e ambiente. Sem a chave, nada muda (desenvolvimento);
    /// com a chave e sem o diretório, o host não sobe.
    /// </summary>
    public static ConfigurationManager AddSecretFiles(this ConfigurationManager configuration)
    {
        var directory = configuration[SecretsDirectoryKey];
        if (string.IsNullOrWhiteSpace(directory))
        {
            return configuration;
        }

        configuration.AddKeyPerFile(directory, optional: false);
        return configuration;
    }
}
