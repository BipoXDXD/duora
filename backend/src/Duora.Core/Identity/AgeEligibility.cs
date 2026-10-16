namespace Duora.Core.Identity;

/// <summary>
/// Regra etária como funções puras (spec 4.2 e 12.2; ADR-0024): o que um resultado do fornecedor faz com a solicitação
/// e com a conta, e se a conta participa num instante. Quem persiste aplica a decisão com UPDATE condicional; esta
/// classe não lê relógio nem banco. A verificação adulta acontece uma vez, no cadastro, e não vence (decisão do dono,
/// 2026-10-16).
/// </summary>
public static class AgeEligibility
{
    /// <summary>
    /// Efeito de um resultado sobre uma solicitação. Só uma solicitação <c>Pending</c> aceita resultado, inclusive a
    /// que passou do prazo sem ser substituída; para as demais devolve <c>null</c> (resultado repetido ou tardio não
    /// muda nada), salvo <c>NotAdult</c> numa <c>Expired</c>, que restringe a conta e deixa a solicitação como está.
    /// <c>VerifiedAdult</c> ativa só a conta <c>PendingEligibility</c>; <c>NotAdult</c> restringe a conta em verificação
    /// (pendente ou ativa), mesmo a de quem já é adulta verificada; <c>Inconclusive</c> vai para revisão manual sem
    /// mexer na conta. Nenhum resultado tira a conta de <c>Restricted</c>, <c>Suspended</c> ou <c>DeletionPending</c>.
    /// </summary>
    public static AgeVerificationDecision? Decide(
        AccountStatus account,
        AgeVerificationState verification,
        AgeCheckOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Resultado desconhecido.");
        }

        if (verification == AgeVerificationState.Expired && outcome == AgeCheckOutcome.NotAdult)
        {
            // Suspeita de menor nunca é ignorada (spec 12.2): mesmo numa solicitação abandonada, restringe a conta.
            return new AgeVerificationDecision(AgeVerificationState.Expired, RestrictedIfInFlow(account));
        }

        if (verification != AgeVerificationState.Pending)
        {
            return null;
        }

        return outcome switch
        {
            AgeCheckOutcome.VerifiedAdult => new AgeVerificationDecision(
                AgeVerificationState.VerifiedAdult,
                account == AccountStatus.PendingEligibility ? AccountStatus.Active : account),
            AgeCheckOutcome.NotAdult => new AgeVerificationDecision(AgeVerificationState.Rejected, RestrictedIfInFlow(account)),
            AgeCheckOutcome.Inconclusive => new AgeVerificationDecision(AgeVerificationState.ManualReview, account),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Resultado desconhecido."),
        };
    }

    /// <summary>
    /// Elegível = conta <c>Active</c> com verificação <c>VerifiedAdult</c>, que não vence. O tempo nunca concede
    /// elegibilidade (TM-40): só faz uma solicitação pendente abandonada dar lugar a uma nova; revisão manual não vence
    /// sozinha. Com <c>VerifiedAdult</c>, nenhum estado de conta volta a oferecer nova verificação.
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
            { State: AgeVerificationState.VerifiedAdult } =>
                account == AccountStatus.Active
                    ? new AgeEligibilityResult(IsEligible: true, AgeVerificationNextStep.None)
                    // Conta pendente com verificação adulta não sai pelo fluxo (o callback ativa as duas juntas). Quem
                    // devolver uma conta ao fluxo a leva a Active; aqui só se nega, sem pedir verificação de novo.
                    : NotEligible(AgeVerificationNextStep.NotAvailable),
            { State: AgeVerificationState.Expired } => NotEligible(AgeVerificationNextStep.StartVerification),
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

    /// <summary>Estados em que alguém (fornecedor ou revisão) já se pronunciou sobre a pessoa.</summary>
    public static IReadOnlyList<AgeVerificationState> Decisions { get; } =
        [AgeVerificationState.VerifiedAdult, AgeVerificationState.Rejected, AgeVerificationState.ManualReview];

    /// <summary>
    /// Qual solicitação decide a elegibilidade quando a pessoa tem mais de uma: a mais recente, salvo quando ela é só
    /// um pedido sem decisão (<c>Pending</c> ou <c>Expired</c>, linha antiga do tempo da validade) feito depois de uma
    /// <c>VerifiedAdult</c>, porque a verificação adulta não vence. Qualquer decisão posterior (<c>Rejected</c>,
    /// <c>ManualReview</c>) manda sobre ela: na dúvida, não elegível.
    /// </summary>
    public static bool AdultVerificationPrevails(AgeVerificationState latestDecision, AgeVerificationState latest) =>
        latestDecision == AgeVerificationState.VerifiedAdult
        && latest is AgeVerificationState.Pending or AgeVerificationState.Expired;

    /// <summary>Contas em que a verificação etária ainda decide alguma coisa.</summary>
    private static bool IsInVerificationFlow(AccountStatus account) =>
        account is AccountStatus.PendingEligibility or AccountStatus.Active;

    private static AccountStatus RestrictedIfInFlow(AccountStatus account) =>
        IsInVerificationFlow(account) ? AccountStatus.Restricted : account;

    private static AgeEligibilityResult NotEligible(AgeVerificationNextStep next) => new(IsEligible: false, next);
}
