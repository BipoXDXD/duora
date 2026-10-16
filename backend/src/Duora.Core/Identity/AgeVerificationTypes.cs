namespace Duora.Core.Identity;

/// <summary>
/// Estados de uma solicitação de verificação etária (spec 12.2), persistidos pelo nome. <c>Pending</c> e
/// <c>ManualReview</c> são as solicitações abertas: há no máximo uma por pessoa.
/// </summary>
public enum AgeVerificationState
{
    Pending,
    VerifiedAdult,
    Rejected,
    Expired,
    ManualReview,
}

/// <summary>Resultado que o fornecedor informa sobre uma solicitação, já traduzido do protocolo dele.</summary>
public enum AgeCheckOutcome
{
    VerifiedAdult,
    NotAdult,
    Inconclusive,
}

/// <summary>O que a pessoa precisa fazer agora (API-P06). Não carrega nenhuma inferência do fornecedor (TM-38).</summary>
public enum AgeVerificationNextStep
{
    /// <summary>Elegível: nada a fazer.</summary>
    None,

    /// <summary>Sem solicitação aberta nem verificação adulta: pode pedir uma (API-P05).</summary>
    StartVerification,

    /// <summary>Solicitação criada; o worker ainda não obteve o fluxo do fornecedor.</summary>
    WaitForProvider,

    /// <summary>O fluxo do fornecedor está pronto e a pessoa segue por ele.</summary>
    ContinueWithProvider,

    /// <summary>Resultado inconclusivo, em revisão manual.</summary>
    WaitForReview,

    /// <summary>A conta não está num estado em que a verificação avance (restrita, suspensa, em exclusão).</summary>
    NotAvailable,
}

/// <summary>
/// Solicitação da pessoa que decide a elegibilidade (<see cref="AgeEligibility.Decisions"/>).
/// <c>ProviderFlowReady</c> diz se o worker já obteve o fluxo do fornecedor.
/// </summary>
public sealed record AgeVerificationSnapshot(
    AgeVerificationState State,
    DateTimeOffset RequestedAt,
    bool ProviderFlowReady);

/// <summary>Elegibilidade etária de uma conta num instante e o próximo passo da pessoa.</summary>
public sealed record AgeEligibilityResult(bool IsEligible, AgeVerificationNextStep NextStep)
{
    /// <summary>API-P05 só cria solicitação quando a regra oferece começar uma.</summary>
    public bool AllowsNewRequest => NextStep == AgeVerificationNextStep.StartVerification;
}

/// <summary>
/// Efeito de um resultado do fornecedor: novo estado da solicitação e estado em que a conta deve ficar.
/// </summary>
public sealed record AgeVerificationDecision(AgeVerificationState VerificationState, AccountStatus AccountStatus)
{
    public bool ChangesAccountFrom(AccountStatus current) => AccountStatus != current;
}
