using Duora.Core.Identity;
using Duora.Core.Profiles;

namespace Duora.UnitTests.Profiles;

public sealed class ProfileEditPolicyTests
{
    [Theory]
    [InlineData(AccountStatus.PendingEligibility)]
    [InlineData(AccountStatus.Active)]
    public void AllowsAccountsInGoodStanding(AccountStatus status)
    {
        Assert.True(ProfileEditPolicy.Allows(status));
    }

    /// <summary>Spec 8.2: sessão restrita só vê a própria conta, segurança e recurso; não edita o que outros veem.</summary>
    [Theory]
    [InlineData(AccountStatus.PendingEmail)]
    [InlineData(AccountStatus.Restricted)]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionPending)]
    public void DeniesEveryOtherStatus(AccountStatus status)
    {
        Assert.False(ProfileEditPolicy.Allows(status));
    }

    [Fact]
    public void EveryStatusHasADecision()
    {
        // Estado novo no enum cai no braço que lança: precisa de decisão explícita, não de permissão por omissão.
        Assert.All(Enum.GetValues<AccountStatus>(), status => _ = ProfileEditPolicy.Allows(status));
    }
}
