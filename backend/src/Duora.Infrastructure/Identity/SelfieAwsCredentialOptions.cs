using System.Diagnostics.CodeAnalysis;
using Amazon.Runtime;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Chave do usuário IAM da selfie do processo: no web, <c>duora-selfie-web</c> (só sts:AssumeRole no papel de captura);
/// no worker, <c>duora-selfie-worker</c> (sessão, resultado e DetectFaces). Em produção chega só por arquivo de segredo
/// (<c>/run/secrets/SelfieVerification__AwsAccessKeyId</c> e <c>..__AwsSecretAccessKey</c>), lido pelo
/// <c>SecretsDirectory</c>. Os clientes AWS recebem estas credenciais explicitamente: a cadeia padrão do SDK
/// (variáveis de ambiente, perfis, IMDS) nunca é consultada.
/// </summary>
public sealed class SelfieAwsCredentialOptions
{
    public string? AwsAccessKeyId { get; set; }
    public string? AwsSecretAccessKey { get; set; }

    [MemberNotNullWhen(true, nameof(AwsAccessKeyId), nameof(AwsSecretAccessKey))]
    public bool IsComplete => !string.IsNullOrWhiteSpace(AwsAccessKeyId) && !string.IsNullOrWhiteSpace(AwsSecretAccessKey);

    public AWSCredentials ToAwsCredentials() => IsComplete
        ? new BasicAWSCredentials(AwsAccessKeyId.Trim(), AwsSecretAccessKey.Trim())
        : throw new InvalidOperationException("SelfieVerification AWS credentials are not configured.");
}
