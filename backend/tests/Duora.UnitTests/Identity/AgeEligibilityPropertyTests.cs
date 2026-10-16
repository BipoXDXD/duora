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

    private static readonly AgeVerificationPolicy Policy = new(
        "2026-10-15",
        validity: TimeSpan.FromDays(30),
        requestLifetime: TimeSpan.FromHours(24));

    private static readonly Gen<AccountStatus> AnyAccount = Gen.Enum<AccountStatus>();

    private static readonly Gen<AgeVerificationState> AnyState = Gen.Enum<AgeVerificationState>();

    private static readonly Gen<AgeCheckOutcome> AnyOutcome = Gen.Enum<AgeCheckOutcome>();

    /// <summary>Até 60 dias depois da origem, em minutos, para cruzar a validade e o prazo da solicitação.</summary>
    private static readonly Gen<DateTimeOffset> AnyInstant = Gen.Int[0, 60 * 24 * 60].Select(minutes => Origin.AddMinutes(minutes));

    private static readonly Gen<AgeVerificationSnapshot?> AnySnapshot =
        Gen.Frequency(
            (1, Gen.Const((AgeVerificationSnapshot?)null)),
            (6, Gen.Select(AnyState, AnyInstant, AnyInstant, Gen.Bool, MakeSnapshot)));

    [Fact]
    public void EligibilityAlwaysMeansAnActiveAccountWithAnUnexpiredAdultVerification()
    {
        Gen.Select(AnyAccount, AnySnapshot, AnyInstant).Sample(
            (account, latest, now) =>
            {
                var result = AgeEligibility.Evaluate(account, latest, now, Policy);
                return !result.IsEligible
                    || (account == AccountStatus.Active
                        && latest is { State: AgeVerificationState.VerifiedAdult, ExpiresAt: { } expiresAt }
                        && expiresAt > now);
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
        Gen.Select(AnyAccount, AnyOutcome.Array[1, 8], AnyInstant).Sample(
            (start, outcomes, now) =>
            {
                var (account, _) = ApplyInFreshRequests(start, outcomes, now);
                return start is not (AccountStatus.Restricted or AccountStatus.Suspended or AccountStatus.DeletionPending)
                    || account == start;
            },
            iter: Iterations);
    }

    [Fact]
    public void OnlyAVerifiedAdultResultActivatesAnAccount()
    {
        Gen.Select(AnyAccount, AnyState, AnyOutcome, AnyInstant).Sample(
            (account, state, outcome, now) =>
            {
                var decision = AgeEligibility.Decide(account, state, outcome, now, Policy);
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
        Gen.Select(AnyAccount, AnyOutcome, AnyInstant).Sample(
            (account, outcome, now) =>
            {
                var decision = AgeEligibility.Decide(account, AgeVerificationState.Pending, outcome, now, Policy)!;
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
        Gen.Select(AnyAccount, AnyOutcome, AnyOutcome, AnyInstant).Sample(
            (account, first, second, now) =>
            {
                var decided = AgeEligibility.Decide(account, AgeVerificationState.Pending, first, now, Policy)!;
                return AgeEligibility.Decide(decided.AccountStatus, decided.VerificationState, second, now, Policy) is null;
            },
            iter: Iterations);
    }

    [Fact]
    public void VerifiedAdultAlwaysCarriesAnExpiryInsideTheValidity()
    {
        Gen.Select(AnyAccount, AnyInstant).Sample(
            (account, now) =>
            {
                var decision = AgeEligibility.Decide(account, AgeVerificationState.Pending, AgeCheckOutcome.VerifiedAdult, now, Policy)!;
                return decision.ExpiresAt == now + Policy.Validity;
            },
            iter: Iterations);
    }

    [Fact]
    public void ANewRequestIsOfferedOnlyWhenNothingIsOpenOrValid()
    {
        Gen.Select(AnyAccount, AnySnapshot, AnyInstant).Sample(
            (account, latest, now) =>
            {
                var result = AgeEligibility.Evaluate(account, latest, now, Policy);
                return !result.AllowsNewRequest
                    || (account is AccountStatus.PendingEligibility or AccountStatus.Active
                        && !result.IsEligible
                        && latest?.State is not (AgeVerificationState.ManualReview or AgeVerificationState.Rejected));
            },
            iter: Iterations);
    }

    /// <summary>Cada resultado chega numa solicitação nova, como se a pessoa pedisse de novo depois de cada decisão.</summary>
    private static (AccountStatus Account, AgeVerificationState Last) ApplyInFreshRequests(
        AccountStatus account,
        AgeCheckOutcome[] outcomes,
        DateTimeOffset now)
    {
        var last = AgeVerificationState.Pending;
        foreach (var outcome in outcomes)
        {
            var decision = AgeEligibility.Decide(account, AgeVerificationState.Pending, outcome, now, Policy)!;
            account = decision.AccountStatus;
            last = decision.VerificationState;
        }

        return (account, last);
    }

    private static AgeVerificationSnapshot? MakeSnapshot(
        AgeVerificationState state,
        DateTimeOffset requestedAt,
        DateTimeOffset expiresAt,
        bool flowReady) =>
        new(
            state,
            requestedAt,
            state is AgeVerificationState.VerifiedAdult or AgeVerificationState.Expired ? expiresAt : null,
            flowReady);
}
