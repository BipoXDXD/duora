using Duora.Core;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>Fornecedores de verificação etária que o host sabe montar.</summary>
public enum AgeVerificationProviderKind
{
    /// <summary>Sem fornecedor: API-P05 responde 503 e a conta continua pendente (TM-40).</summary>
    None,

    /// <summary>Fornecedor simulado, só em Development e Testing (ADR-0024).</summary>
    Fake,
}

/// <summary>Modos roteirizados do fornecedor simulado, para exercitar timeout, falha e callback repetido.</summary>
public enum FakeAgeProviderMode
{
    Succeed,
    Slow,
    Fail,
    DuplicateCallback,
}

/// <summary>Parâmetros da verificação etária (seção <c>AgeVerification</c>; ADR-0024).</summary>
internal sealed class AgeVerificationOptions
{
    public const string Section = "AgeVerification";

    public AgeVerificationProviderKind Provider { get; set; } = AgeVerificationProviderKind.None;

    /// <summary>Versão da política etária gravada em cada solicitação (TM-37).</summary>
    public string PolicyVersion { get; set; } = "synthetic-2026-10";

    /// <summary>Quanto tempo uma solicitação sem resultado fica aberta antes de a pessoa poder pedir outra.</summary>
    public TimeSpan RequestLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Origens (https, sem caminho) aceitas para a URL do fluxo que o fornecedor devolve.</summary>
    public List<string> AllowedFlowOrigins { get; set; } = [];

    public FakeAgeProviderOptions Fake { get; set; } = new();

    public bool IsEnabled => Provider != AgeVerificationProviderKind.None;

    public AgeVerificationPolicy ToPolicy() => new(PolicyVersion, RequestLifetime);

    /// <summary>A URL do fluxo pode ir ao navegador: https, sem credencial e com origem na allowlist.</summary>
    public bool IsAllowedFlow(Uri flowUrl) =>
        flowUrl.IsAbsoluteUri
        && flowUrl.Scheme == Uri.UriSchemeHttps
        && flowUrl.UserInfo.Length == 0
        && AllowedFlowOrigins.Any(origin =>
            PublicOrigin.TryParse(origin, out var allowed)
            && string.Equals(allowed.ToString(), flowUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));

    /// <summary>Primeiro problema de configuração, com a chave a corrigir; nulo quando está tudo certo.</summary>
    public string? FindProblem(bool isDevelopmentOrTesting)
    {
        if (!Enum.IsDefined(Provider))
        {
            return $"{Section}:Provider desconhecido.";
        }

        if (Provider == AgeVerificationProviderKind.Fake && !isDevelopmentOrTesting)
        {
            return $"{Section}:Provider=Fake só é aceito em Development ou Testing (ADR-0024).";
        }

        if (!IsPolicyVersionValid())
        {
            return $"{Section}:PolicyVersion fora do formato (até 32 letras, dígitos, '.', '-', '_').";
        }

        if (RequestLifetime <= TimeSpan.Zero)
        {
            return $"{Section}:RequestLifetime precisa ser positivo.";
        }

        if (IsEnabled && (AllowedFlowOrigins.Count == 0 || !AllowedFlowOrigins.All(o => PublicOrigin.TryParse(o, out _))))
        {
            return $"{Section}:AllowedFlowOrigins precisa listar origens https sem caminho.";
        }

        return Provider == AgeVerificationProviderKind.Fake ? Fake.FindProblem() : null;
    }

    private bool IsPolicyVersionValid()
    {
        try
        {
            _ = new AgeVerificationPolicy(PolicyVersion, TimeSpan.FromDays(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Fornecedor simulado (seção <c>AgeVerification:Fake</c>). Nunca recebe dado real.</summary>
internal sealed class FakeAgeProviderOptions
{
    public FakeAgeProviderMode Mode { get; set; } = FakeAgeProviderMode.Succeed;

    /// <summary>Base da URL do fluxo simulado; a referência vai no último segmento.</summary>
    public string FlowBaseUrl { get; set; } = "https://age-check.fake.invalid/flow";

    /// <summary>Quanto o modo <c>Slow</c> demora para responder; acima do timeout do worker.</summary>
    public TimeSpan SlowDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Segredos HMAC dos callbacks: um ou dois, para rotação sem janela de falha (configura-se o novo como segundo,
    /// troca-se no fornecedor, remove-se o antigo). Vêm de user-secrets ou do cofre, nunca do repositório.
    /// </summary>
    public List<string> SigningSecrets { get; set; } = [];

    /// <summary>Segredo com menos que isto é recusado no start (256 bits em hex ou texto equivalente).</summary>
    public const int MinSecretLength = 32;

    public const int MaxSecrets = 2;

    public string? FindProblem()
    {
        if (!Enum.IsDefined(Mode))
        {
            return $"{AgeVerificationOptions.Section}:Fake:Mode desconhecido.";
        }

        if (!Uri.TryCreate(FlowBaseUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return $"{AgeVerificationOptions.Section}:Fake:FlowBaseUrl precisa ser https.";
        }

        return SigningSecrets.Count is >= 1 and <= MaxSecrets && SigningSecrets.All(s => s.Length >= MinSecretLength)
            ? null
            : $"{AgeVerificationOptions.Section}:Fake:SigningSecrets precisa de 1 ou 2 segredos com ao menos {MinSecretLength} caracteres.";
    }
}
