using Duora.Core.Events;
using Duora.Core.Identity;

namespace Duora.Core.Safety;

/// <summary>
/// Janela UTC <c>[L,U]</c> da troca para a política de inscrições de contas suspensas (ADR-0044, issue #321):
/// L é o limite inferior comprovado do início da transição e U o superior, com schema e todos os escritores
/// compatíveis. Vem de evidência de implantação revisada, nunca de um timestamp escolhido pelo cliente nem da data
/// da migração.
/// </summary>
public sealed record ReconciliationCutover
{
    private ReconciliationCutover(DateTimeOffset lower, DateTimeOffset upper)
    {
        Lower = lower;
        Upper = upper;
    }

    public DateTimeOffset Lower { get; }

    public DateTimeOffset Upper { get; }

    public static ReconciliationCutover Between(DateTimeOffset lower, DateTimeOffset upper)
    {
        RequireFiniteUtc(lower, nameof(lower));
        RequireFiniteUtc(upper, nameof(upper));
        if (upper < lower)
        {
            throw new ArgumentException("O limite superior do corte não pode anteceder o inferior.", nameof(upper));
        }

        return new ReconciliationCutover(lower, upper);
    }

    private static void RequireFiniteUtc(DateTimeOffset value, string name)
    {
        // MinValue/MaxValue são como o Npgsql materializa -infinity/infinity: nunca valem como limite comprovado.
        if (value.Offset != TimeSpan.Zero || value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, "O corte exige um instante UTC finito.");
        }
    }
}

/// <summary>
/// Motivo pelo qual uma inscrição não é candidata automática à reconciliação. A ordem declarada é a precedência da
/// contagem sem dupla contagem: histórico terminal, evento encerrado, medida/conta, corte/cronologia, integridade
/// (que inclui o sujeito privilegiado, como na tabela da proposta).
/// </summary>
public enum LegacyRegistrationIssue
{
    AlreadyWithdrawnBySuspension = 1,
    TerminalRegistration,
    ClosedEvent,
    MissingOriginal,
    MultipleOriginals,
    LiftedOriginal,
    AccountNotSuspended,
    InconsistentAccount,
    MissingDeploymentEvidence,
    TimestampUntrusted,
    PostCutover,
    RolloutWindow,
    RegisteredAfterOriginal,
    PrivilegedSubject,
    InvalidHistory,
    CapacityMismatch,
    ActiveReservation,
}

/// <summary>Conta alvo como Identity a informa: estado atual, estado anterior gravado e papel privilegiado.</summary>
public sealed record LegacyAccountFacts(AccountStatus Status, bool HasStatusBeforeSuspension, bool IsPrivileged);

/// <summary>A suspensão ativa (a "medida original"), com versão e instante da decisão.</summary>
public sealed record LegacyMeasureFacts(Guid Id, long Version, DateTimeOffset DecidedAt);

/// <summary>Medidas de suspensão da conta: a ativa, quantas ativas existem e se há alguma revogada.</summary>
public sealed record LegacyMeasuresFacts(LegacyMeasureFacts? Active, int ActiveCount, bool HasLifted);

/// <summary>
/// A inscrição como está agora. <paramref name="RegisteredAt"/> é nulo quando o banco guarda infinito;
/// <paramref name="CheckedInAtUntrusted"/> marca <c>checked_in_at</c> infinito, que chega como nulo.
/// </summary>
public sealed record LegacyRegistrationRow(
    RegistrationState State,
    DateTimeOffset? RegisteredAt,
    long? WaitlistPosition,
    DateTimeOffset? CheckedInAt,
    bool WithdrawnBySuspension,
    bool HasActiveReservation,
    bool CheckedInAtUntrusted = false);

/// <summary>
/// O evento da inscrição; <paramref name="OccupantCount"/> são as inscrições Confirmed/CheckedIn e
/// <paramref name="StartsAtUntrusted"/> marca <c>starts_at</c> infinito, que a promoção atual trataria como futuro.
/// </summary>
public sealed record LegacyEventFacts(
    EventState State,
    int Capacity,
    int ConfirmedCount,
    int OccupantCount,
    long NextWaitlistPosition,
    bool HasInvalidWaitlist,
    bool StartsAtUntrusted = false);

/// <summary>Tudo o que a prévia leu sobre uma inscrição, no mesmo snapshot.</summary>
public sealed record LegacyRegistrationFacts(
    LegacyAccountFacts Account,
    LegacyMeasuresFacts Measures,
    LegacyRegistrationRow Registration,
    LegacyEventFacts Event);

/// <summary>Resultado da classificação de uma inscrição: todos os motivos, e o principal pela precedência.</summary>
public sealed record LegacyRegistrationClassification(IReadOnlySet<LegacyRegistrationIssue> Issues)
{
    public bool IsCandidate => Issues.Count == 0;

    /// <summary>O motivo de maior precedência; nulo para candidata.</summary>
    public LegacyRegistrationIssue? Primary => IsCandidate ? null : Issues.Min();

    /// <summary>Motivos que pausam a conta/medida inteira, inclusive as demais inscrições candidatas dela.</summary>
    public bool BlocksUnit => Issues.Overlaps(UnitBlockers);

    private static readonly LegacyRegistrationIssue[] UnitBlockers =
    [
        LegacyRegistrationIssue.MultipleOriginals,
        LegacyRegistrationIssue.InconsistentAccount,
        LegacyRegistrationIssue.PrivilegedSubject,
        LegacyRegistrationIssue.InvalidHistory,
        LegacyRegistrationIssue.CapacityMismatch,
        LegacyRegistrationIssue.ActiveReservation,
    ];
}

/// <summary>
/// Seletor conservador da reconciliação de inscrições legadas (proposta da #321, seção 5). Só decide; não lê nem
/// grava. Candidata automática exige suspensão ativa única, conta coerente, <c>registered_at &lt;= decided_at &lt; L</c>,
/// inscrição ativa sem proveniência de retirada, evento Published/Running e nenhum indício de corrupção.
/// </summary>
public static class LegacyRegistrationSelector
{
    /// <summary>Versão do seletor; mudar a regra invalida prévias anteriores.</summary>
    public const int Version = 1;

    public static LegacyRegistrationClassification Classify(LegacyRegistrationFacts facts, ReconciliationCutover? cutover)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var issues = new HashSet<LegacyRegistrationIssue>();
        AddRegistrationHistory(facts.Registration, issues);
        AddEventState(facts.Event, issues);
        AddMeasureAndAccount(facts.Account, facts.Measures, issues);
        AddChronology(facts.Registration, facts.Measures.Active, cutover, issues);
        if (facts.Event.StartsAtUntrusted)
        {
            issues.Add(LegacyRegistrationIssue.TimestampUntrusted);
        }

        AddIntegrity(facts.Registration, facts.Event, issues);
        return new LegacyRegistrationClassification(issues);
    }

    private static void AddRegistrationHistory(LegacyRegistrationRow registration, HashSet<LegacyRegistrationIssue> issues)
    {
        if (registration.WithdrawnBySuspension)
        {
            issues.Add(LegacyRegistrationIssue.AlreadyWithdrawnBySuspension);
        }
        else if (!IsActive(registration.State))
        {
            issues.Add(LegacyRegistrationIssue.TerminalRegistration);
        }
    }

    private static void AddEventState(LegacyEventFacts @event, HashSet<LegacyRegistrationIssue> issues)
    {
        if (@event.State is not (EventState.Published or EventState.Running))
        {
            issues.Add(LegacyRegistrationIssue.ClosedEvent);
        }
    }

    private static void AddMeasureAndAccount(
        LegacyAccountFacts account, LegacyMeasuresFacts measures, HashSet<LegacyRegistrationIssue> issues)
    {
        if (measures.ActiveCount > 1)
        {
            issues.Add(LegacyRegistrationIssue.MultipleOriginals);
        }
        else if (measures.Active is null)
        {
            issues.Add(measures.HasLifted ? LegacyRegistrationIssue.LiftedOriginal : LegacyRegistrationIssue.MissingOriginal);
        }

        if (account.Status != AccountStatus.Suspended)
        {
            // Medida ativa com conta fora de Suspended é divergência; sem medida ativa, é só conta não suspensa.
            issues.Add(measures.Active is null
                ? LegacyRegistrationIssue.AccountNotSuspended
                : LegacyRegistrationIssue.InconsistentAccount);
        }
        else if (!account.HasStatusBeforeSuspension)
        {
            issues.Add(LegacyRegistrationIssue.InconsistentAccount);
        }

        if (account.IsPrivileged)
        {
            issues.Add(LegacyRegistrationIssue.PrivilegedSubject);
        }
    }

    private static void AddChronology(LegacyRegistrationRow registration, LegacyMeasureFacts? original,
        ReconciliationCutover? cutover, HashSet<LegacyRegistrationIssue> issues)
    {
        if (registration.RegisteredAt is null || registration.CheckedInAtUntrusted)
        {
            issues.Add(LegacyRegistrationIssue.TimestampUntrusted);
        }

        if (cutover is null)
        {
            issues.Add(LegacyRegistrationIssue.MissingDeploymentEvidence);
            return;
        }

        if (original is null)
        {
            return;
        }

        // Corte e cronologia são independentes: todos os motivos aparecem, para o diagnóstico separado de cada um.
        if (original.DecidedAt >= cutover.Upper)
        {
            issues.Add(LegacyRegistrationIssue.PostCutover);
        }
        else if (original.DecidedAt >= cutover.Lower)
        {
            issues.Add(LegacyRegistrationIssue.RolloutWindow);
        }

        if (registration.RegisteredAt > original.DecidedAt)
        {
            issues.Add(LegacyRegistrationIssue.RegisteredAfterOriginal);
        }
    }

    private static void AddIntegrity(
        LegacyRegistrationRow registration, LegacyEventFacts @event, HashSet<LegacyRegistrationIssue> issues)
    {
        // Só o que a reconciliação poderia tocar pausa a unidade: um contador antigo de evento encerrado não.
        if (!IsTouchable(registration, @event))
        {
            return;
        }

        if (HasInvalidHistory(registration, @event))
        {
            issues.Add(LegacyRegistrationIssue.InvalidHistory);
        }

        if (@event.ConfirmedCount != @event.OccupantCount || @event.ConfirmedCount > @event.Capacity)
        {
            issues.Add(LegacyRegistrationIssue.CapacityMismatch);
        }

        if (registration.HasActiveReservation)
        {
            issues.Add(LegacyRegistrationIssue.ActiveReservation);
        }
    }

    private static bool HasInvalidHistory(LegacyRegistrationRow registration, LegacyEventFacts @event)
    {
        var invalidPosition = registration.State == RegistrationState.Waitlisted
            ? registration.WaitlistPosition is not { } position || position < 1 || position >= @event.NextWaitlistPosition
            : registration.WaitlistPosition is not null;
        // Só o check-in grava checked_in_at; entre os estados ativos ele existe se e somente se CheckedIn.
        var invalidCheckIn = (registration.State == RegistrationState.CheckedIn) != registration.CheckedInAt.HasValue
            || registration.CheckedInAt < registration.RegisteredAt;
        return invalidPosition || invalidCheckIn || @event.HasInvalidWaitlist;
    }

    /// <summary>Inscrição ativa, sem proveniência de retirada, num evento ainda aberto.</summary>
    public static bool IsTouchable(LegacyRegistrationRow registration, LegacyEventFacts @event)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(@event);
        return IsActive(registration.State) && !registration.WithdrawnBySuspension
            && @event.State is EventState.Published or EventState.Running;
    }

    private static bool IsActive(RegistrationState state) =>
        state is RegistrationState.Waitlisted or RegistrationState.Confirmed or RegistrationState.CheckedIn;
}
