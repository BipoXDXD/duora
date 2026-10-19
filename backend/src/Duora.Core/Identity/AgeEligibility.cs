namespace Duora.Core.Identity;

/// <summary>
/// Historical protocol kept for migration compatibility tests only (superseded by ADR-0029).
/// No production host registers its callers. Live authorization uses ParticipationEligibility and selfie evidence.
/// </summary>
public static class AgeEligibility
{
    /// <summary>
    /// Efeito de um resultado sobre uma solicitação. Só uma solicitação <c>Pending</c> aceita <c>VerifiedAdult</c> ou
    /// <c>Inconclusive</c>, inclusive a que passou do prazo sem ser substituída; nas demais eles não mudam nada
    /// (<c>null</c>). <c>NotAdult</c> vale em qualquer estado: é suspeita de menor, durável e independente da conta
    /// (spec 12.2). Numa <c>Pending</c> a rejeita; nas demais (até a <c>VerifiedAdult</c> da mesma solicitação) deixa
    /// o estado como está; sempre grava a suspeita e restringe a conta em verificação (pendente ou ativa).
    /// <c>VerifiedAdult</c> ativa só a conta <c>PendingEligibility</c>; <c>Inconclusive</c> vai para revisão manual
    /// sem mexer na conta. Nenhum resultado tira a conta de <c>Restricted</c>, <c>Suspended</c> ou
    /// <c>DeletionPending</c>.
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

        if (verification != AgeVerificationState.Pending && outcome == AgeCheckOutcome.NotAdult)
        {
            // Suspeita de menor nunca é ignorada (spec 12.2): mesmo numa solicitação abandonada ou já decidida.
            return new AgeVerificationDecision(verification, RestrictedIfInFlow(account), MinorSuspected: true);
        }

        if (verification != AgeVerificationState.Pending)
        {
            return null;
        }

        return outcome switch
        {
            AgeCheckOutcome.VerifiedAdult => new AgeVerificationDecision(
                AgeVerificationState.VerifiedAdult,
                account == AccountStatus.PendingEligibility ? AccountStatus.Active : account,
                MinorSuspected: false),
            AgeCheckOutcome.NotAdult => new AgeVerificationDecision(
                AgeVerificationState.Rejected,
                RestrictedIfInFlow(account),
                MinorSuspected: true),
            AgeCheckOutcome.Inconclusive => new AgeVerificationDecision(
                AgeVerificationState.ManualReview,
                account,
                MinorSuspected: false),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Resultado desconhecido."),
        };
    }

    /// <summary>
    /// Elegível = conta <c>Active</c> com verificação <c>VerifiedAdult</c>, que não vence. O tempo nunca concede
    /// elegibilidade (TM-40): só faz uma solicitação pendente abandonada dar lugar a uma nova; revisão manual não vence
    /// sozinha. Com <c>VerifiedAdult</c>, nenhum estado de conta volta a oferecer nova verificação. Suspeita de menor
    /// em qualquer solicitação da pessoa nega sempre, com <c>NotAvailable</c>: só a revisão humana (M2) a remove.
    /// </summary>
    public static AgeEligibilityResult Evaluate(
        AccountStatus account,
        AgeVerificationSnapshot? latest,
        DateTimeOffset now,
        AgeVerificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsInVerificationFlow(account) || latest is { MinorSuspected: true })
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

    /// <summary>
    /// Estados em que alguém (fornecedor ou revisão) já se pronunciou sobre a pessoa. A decisão mais recente decide a
    /// elegibilidade; só sem nenhuma decisão vale o pedido mais recente (<c>Pending</c> ou <c>Expired</c>). Assim a
    /// verificação adulta não vence, e uma decisão negativa posterior manda sobre ela: na dúvida, não elegível.
    /// </summary>
    public static IReadOnlyList<AgeVerificationState> Decisions { get; } =
        [AgeVerificationState.VerifiedAdult, AgeVerificationState.Rejected, AgeVerificationState.ManualReview];

    /// <summary>Contas em que a verificação etária ainda decide alguma coisa.</summary>
    private static bool IsInVerificationFlow(AccountStatus account) =>
        account is AccountStatus.PendingEligibility or AccountStatus.Active;

    private static AccountStatus RestrictedIfInFlow(AccountStatus account) =>
        IsInVerificationFlow(account) ? AccountStatus.Restricted : account;

    private static AgeEligibilityResult NotEligible(AgeVerificationNextStep next) => new(IsEligible: false, next);
}
