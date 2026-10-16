namespace Duora.Core.Identity;

/// <summary>
/// Regra etária como funções puras (spec 4.2 e 12.2; ADR-0024): o que um resultado do fornecedor faz com a solicitação
/// e com a conta, e se a conta participa num instante. Quem persiste aplica a decisão com UPDATE condicional; esta
/// classe não lê relógio nem banco.
/// </summary>
public static class AgeEligibility
{
    /// <summary>
    /// Efeito de um resultado sobre uma solicitação. Só uma solicitação <c>Pending</c> aceita resultado; para as
    /// demais devolve <c>null</c> (resultado repetido ou tardio não muda nada). <c>VerifiedAdult</c> ativa só a conta
    /// <c>PendingEligibility</c>; <c>NotAdult</c> restringe a conta em verificação (pendente ou ativa);
    /// <c>Inconclusive</c> vai para revisão manual sem mexer na conta. Nenhum resultado tira a conta de
    /// <c>Restricted</c>, <c>Suspended</c> ou <c>DeletionPending</c>.
    /// </summary>
    public static AgeVerificationDecision? Decide(
        AccountStatus account,
        AgeVerificationState verification,
        AgeCheckOutcome outcome,
        DateTimeOffset decidedAt,
        AgeVerificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Resultado desconhecido.");
        }

        if (verification != AgeVerificationState.Pending)
        {
            return null;
        }

        return outcome switch
        {
            AgeCheckOutcome.VerifiedAdult => new AgeVerificationDecision(
                AgeVerificationState.VerifiedAdult,
                decidedAt + policy.Validity,
                account == AccountStatus.PendingEligibility ? AccountStatus.Active : account),
            AgeCheckOutcome.NotAdult => new AgeVerificationDecision(
                AgeVerificationState.Rejected,
                ExpiresAt: null,
                IsInVerificationFlow(account) ? AccountStatus.Restricted : account),
            AgeCheckOutcome.Inconclusive => new AgeVerificationDecision(
                AgeVerificationState.ManualReview,
                ExpiresAt: null,
                account),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Resultado desconhecido."),
        };
    }

    /// <summary>
    /// Elegível = conta <c>Active</c> com verificação adulta cuja validade ainda não chegou (<c>expiresAt &gt; now</c>).
    /// O tempo só tira elegibilidade, nunca a concede (TM-40): solicitação pendente que passou do prazo volta a
    /// oferecer uma nova, revisão manual não vence sozinha.
    /// </summary>
    public static AgeEligibilityResult Evaluate(
        AccountStatus account,
        AgeVerificationSnapshot? latest,
        DateTimeOffset now,
        AgeVerificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsInVerificationFlow(account))
        {
            return NotEligible(AgeVerificationNextStep.NotAvailable);
        }

        return latest switch
        {
            null => NotEligible(AgeVerificationNextStep.StartVerification),
            { State: AgeVerificationState.VerifiedAdult, ExpiresAt: { } expiresAt } when expiresAt > now =>
                account == AccountStatus.Active
                    ? new AgeEligibilityResult(IsEligible: true, AgeVerificationNextStep.None)
                    : NotEligible(AgeVerificationNextStep.NotAvailable),
            { State: AgeVerificationState.VerifiedAdult or AgeVerificationState.Expired } =>
                NotEligible(AgeVerificationNextStep.StartVerification),
            { State: AgeVerificationState.Pending } pending when pending.RequestedAt + policy.RequestLifetime <= now =>
                NotEligible(AgeVerificationNextStep.StartVerification),
            { State: AgeVerificationState.Pending, ProviderFlowReady: true } =>
                NotEligible(AgeVerificationNextStep.ContinueWithProvider),
            { State: AgeVerificationState.Pending } => NotEligible(AgeVerificationNextStep.WaitForProvider),
            { State: AgeVerificationState.ManualReview } => NotEligible(AgeVerificationNextStep.WaitForReview),
            { State: AgeVerificationState.Rejected } => NotEligible(AgeVerificationNextStep.NotAvailable),
            _ => throw new ArgumentOutOfRangeException(nameof(latest), latest.State, "Estado de verificação desconhecido."),
        };
    }

    /// <summary>Contas em que a verificação etária ainda decide alguma coisa.</summary>
    private static bool IsInVerificationFlow(AccountStatus account) =>
        account is AccountStatus.PendingEligibility or AccountStatus.Active;

    private static AgeEligibilityResult NotEligible(AgeVerificationNextStep next) => new(IsEligible: false, next);
}
