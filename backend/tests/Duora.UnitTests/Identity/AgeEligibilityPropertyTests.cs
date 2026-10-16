using CsCheck;
using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Invariantes da elegibilidade etária sobre sequências aleatórias de resultados do fornecedor e instantes (spec 4.2,
/// 12.2; TM-40). Quando uma propriedade falha, o CsCheck reduz o caso e imprime a semente que o refaz
/// (variável <c>CsCheck_Seed</c>).
/// </summary>
public sealed class AgeEligibilityPropertyTests
{
    private const long Iterations = 500;

    private static readonly DateTimeOffset Origin = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    private static readonly AgeVerificationPolicy Policy = new("2026-10-15", requestLifetime: TimeSpan.FromHours(24));

    private static readonly Gen<AccountStatus> AnyAccount = Gen.Enum<AccountStatus>();

    private static readonly Gen<AgeVerificationState> AnyState = Gen.Enum<AgeVerificationState>();

    private static readonly Gen<AgeCheckOutcome> AnyOutcome = Gen.Enum<AgeCheckOutcome>();

    /// <summary>Até 60 dias depois da origem, em minutos, para cruzar o prazo da solicitação.</summary>
    private static readonly Gen<DateTimeOffset> AnyInstant = Gen.Int[0, 60 * 24 * 60].Select(minutes => Origin.AddMinutes(minutes));

    /// <summary>Até 50 anos, em horas: a verificação adulta não vence (ADR-0024, adendo de 2026-10-16).</summary>
    private static readonly Gen<TimeSpan> AnyLongDelay = Gen.Int[0, 50 * 366 * 24].Select(hours => TimeSpan.FromHours(hours));

    private static readonly Gen<AgeVerificationSnapshot?> AnySnapshot =
        Gen.Frequency(
            (1, Gen.Const((AgeVerificationSnapshot?)null)),
            (6, Gen.Select(AnyState, AnyInstant, Gen.Bool, (state, requestedAt, flowReady) =>
                (AgeVerificationSnapshot?)new AgeVerificationSnapshot(state, requestedAt, flowReady))));

    [Fact]
    public void EligibilityAlwaysMeansAnActiveAccountWithAnAdultVerification()
    {
        Gen.Select(AnyAccount, AnySnapshot, AnyInstant).Sample(
            (account, latest, now) =>
            {
                var result = AgeEligibility.Evaluate(account, latest, now, Policy);
                return !result.IsEligible
                    || (account == AccountStatus.Active && latest is { State: AgeVerificationState.VerifiedAdult });
            },
            iter: Iterations);
    }

    [Fact]
    public void ElapsedTimeNeverGrantsEligibility()
    {
        // TM-40: nenhuma transição para elegível por decurso de prazo; com o tempo, só se perde a elegibilidade.
        Gen.Select(AnyAccount, AnySnapshot, AnyInstant, Gen.Int[0, 60 * 24 * 60]).Sample(
            (account, latest, earlier, minutesLater) =>
            {
                var later = earlier.AddMinutes(minutesLater);
                return !AgeEligibility.Evaluate(account, latest, later, Policy).IsEligible
                    || AgeEligibility.Evaluate(account, latest, earlier, Policy).IsEligible;
            },
            iter: Iterations);
    }

    [Fact]
    public void NoSequenceOfProviderResultsTurnsARestrictedAccountActive()
    {
        Gen.Select(AnyAccount, AnyOutcome.Array[1, 8]).Sample(
            (start, outcomes) =>
            {
                var account = ApplyInFreshRequests(start, outcomes);
                return start is not (AccountStatus.Restricted or AccountStatus.Suspended or AccountStatus.DeletionPending)
                    || account == start;
            },
            iter: Iterations);
    }

    [Fact]
    public void OnlyAVerifiedAdultResultActivatesAnAccount()
    {
        Gen.Select(AnyAccount, AnyState, AnyOutcome).Sample(
            (account, state, outcome) =>
            {
                var decision = AgeEligibility.Decide(account, state, outcome);
                var activated = decision is not null && account != AccountStatus.Active && decision.AccountStatus == AccountStatus.Active;
                return !activated
                    || (outcome == AgeCheckOutcome.VerifiedAdult
                        && account == AccountStatus.PendingEligibility
                        && state == AgeVerificationState.Pending);
            },
            iter: Iterations);
    }

    [Fact]
    public void AProviderResultOnlyMovesTheAccountAlongTheAllowedEdges()
    {
        Gen.Select(AnyAccount, AnyOutcome).Sample(
            (account, outcome) =>
            {
                var decision = AgeEligibility.Decide(account, AgeVerificationState.Pending, outcome)!;
                return decision.AccountStatus == account
                    || (account, decision.AccountStatus) is (AccountStatus.PendingEligibility, AccountStatus.Active)
                        or (AccountStatus.PendingEligibility, AccountStatus.Restricted)
                        or (AccountStatus.Active, AccountStatus.Restricted);
            },
            iter: Iterations);
    }

    [Fact]
    public void ADecidedRequestIgnoresEveryLaterResult()
    {
        Gen.Select(AnyAccount, AnyOutcome, AnyOutcome).Sample(
            (account, first, second) =>
            {
                var decided = AgeEligibility.Decide(account, AgeVerificationState.Pending, first)!;
                return AgeEligibility.Decide(decided.AccountStatus, decided.VerificationState, second) is null;
            },
            iter: Iterations);
    }

    [Fact]
    public void AdultVerificationStaysEligibleForever()
    {
        Gen.Select(AnyInstant, AnyLongDelay, Gen.Bool).Sample(
            (decidedAt, delay, flowReady) =>
            {
                var latest = new AgeVerificationSnapshot(AgeVerificationState.VerifiedAdult, decidedAt, flowReady);
                return AgeEligibility.Evaluate(AccountStatus.Active, latest, decidedAt + delay, Policy).IsEligible;
            },
            iter: Iterations);
    }

    [Fact]
    public void AnAdultVerificationNeverAsksForANewOne()
    {
        // Verificação só no cadastro: com VerifiedAdult, nenhum estado de conta e nenhum instante oferece pedir de novo.
        Gen.Select(AnyAccount, AnyInstant, AnyLongDelay).Sample(
            (account, decidedAt, delay) =>
            {
                var latest = new AgeVerificationSnapshot(AgeVerificationState.VerifiedAdult, decidedAt, ProviderFlowReady: true);
                return !AgeEligibility.Evaluate(account, latest, decidedAt + delay, Policy).AllowsNewRequest;
            },
            iter: Iterations);
    }

    [Fact]
    public void ANewRequestIsOfferedOnlyWhenNothingIsOpenOrVerified()
    {
        Gen.Select(AnyAccount, AnySnapshot, AnyInstant).Sample(
            (account, latest, now) =>
            {
                var result = AgeEligibility.Evaluate(account, latest, now, Policy);
                return !result.AllowsNewRequest
                    || (account is AccountStatus.PendingEligibility or AccountStatus.Active
                        && !result.IsEligible
                        && latest?.State is not (AgeVerificationState.ManualReview or AgeVerificationState.Rejected
                            or AgeVerificationState.VerifiedAdult));
            },
            iter: Iterations);
    }

    /// <summary>Cada resultado chega numa solicitação nova, como se a pessoa pedisse de novo depois de cada decisão.</summary>
    private static AccountStatus ApplyInFreshRequests(AccountStatus account, AgeCheckOutcome[] outcomes)
    {
        foreach (var outcome in outcomes)
        {
            account = AgeEligibility.Decide(account, AgeVerificationState.Pending, outcome)!.AccountStatus;
        }

        return account;
    }
}
