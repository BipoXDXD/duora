namespace Duora.Core.Identity;

/// <summary>
/// Port do fornecedor de verificação etária (spec 12.2 e 16.3). Só o worker o chama, a partir da outbox, com timeout
/// e retry (ADR-0024). O resultado nunca volta por aqui: chega pelo webhook autenticado do fornecedor. O adaptador
/// isola o protocolo do fornecedor; a regra de elegibilidade fica em <see cref="AgeEligibility"/>.
/// </summary>
public interface IAgeEligibilityProvider
{
    /// <summary>Nome estável do fornecedor: vai para <c>age_verifications.provider</c> e para <c>/webhooks/{provider}</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Abre a verificação no fornecedor. Precisa ser idempotente pela <see cref="AgeCheckStart.IdempotencyKey"/>:
    /// o worker repete a chamada depois de timeout, queda ou lease vencido.
    /// </summary>
    Task<AgeCheckSession> StartAsync(AgeCheckStart request, CancellationToken cancellationToken);
}

/// <summary>
/// O que o adaptador recebe para abrir a verificação. Sem e-mail, nome nem data de nascimento: o fornecedor recebe
/// referências opacas. <c>LoginSessionId</c> é a sessão em que a pessoa pediu a verificação; nenhum comportamento
/// depende dela por ora, mas o contrato já a carrega enquanto a leitura de "a cada acesso" do ECA Digital está
/// pendente (ADR-0024, decisão 3). O adaptador não a repassa ao fornecedor sem motivo.
/// </summary>
public sealed record AgeCheckStart
{
    public AgeCheckStart(Guid verificationId, string requestNonce, Guid loginSessionId, Uri returnUrl, string policyVersion)
    {
        if (verificationId == Guid.Empty)
        {
            throw new ArgumentException("Id da solicitação vazio.", nameof(verificationId));
        }

        if (loginSessionId == Guid.Empty)
        {
            throw new ArgumentException("Id da sessão vazio.", nameof(loginSessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestNonce);
        ArgumentNullException.ThrowIfNull(returnUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        VerificationId = verificationId;
        RequestNonce = requestNonce;
        LoginSessionId = loginSessionId;
        ReturnUrl = returnUrl;
        PolicyVersion = policyVersion;
    }

    public Guid VerificationId { get; }

    /// <summary>Gerado pelo servidor; o callback precisa trazê-lo de volta (TM-36).</summary>
    public string RequestNonce { get; }

    public Guid LoginSessionId { get; }

    /// <summary>Para onde o fornecedor devolve o navegador; montada da origem pública, nunca da requisição.</summary>
    public Uri ReturnUrl { get; }

    public string PolicyVersion { get; }

    /// <summary>Chave estável da abertura no fornecedor: o id da solicitação.</summary>
    public string IdempotencyKey => VerificationId.ToString("N");
}

/// <summary>
/// Verificação aberta no fornecedor: a referência dele (casada com o callback) e a URL do fluxo para onde a pessoa
/// segue. Validada aqui por ser dado de terceiro; a allowlist de origens fica com quem configura o adaptador.
/// </summary>
public sealed record AgeCheckSession
{
    public const int MaxReferenceLength = 200;
    public const int MaxFlowUrlLength = 2048;

    public AgeCheckSession(string providerReference, Uri flowUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerReference);
        if (providerReference.Length > MaxReferenceLength || !providerReference.All(c => c is > ' ' and <= '~'))
        {
            throw new ArgumentException("Referência do fornecedor fora do formato.", nameof(providerReference));
        }

        ArgumentNullException.ThrowIfNull(flowUrl);
        if (!flowUrl.IsAbsoluteUri
            || flowUrl.Scheme != Uri.UriSchemeHttps
            || flowUrl.UserInfo.Length > 0
            || flowUrl.AbsoluteUri.Length > MaxFlowUrlLength)
        {
            throw new ArgumentException("URL do fluxo precisa ser https absoluta, sem credenciais e com até 2048 caracteres.", nameof(flowUrl));
        }

        ProviderReference = providerReference;
        FlowUrl = flowUrl;
    }

    public string ProviderReference { get; }

    public Uri FlowUrl { get; }
}
