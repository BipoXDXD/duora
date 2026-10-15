using Duora.Core.Identity;
using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

public sealed class BlockEligibilityTests
{
    /// <summary>Bloquear só reduz interação: conta restrita ou suspensa ainda pode se proteger (decisão G4).</summary>
    [Theory]
    [InlineData(AccountStatus.Active)]
    [InlineData(AccountStatus.Restricted)]
    [InlineData(AccountStatus.Suspended)]
    public void AccountThatCanStillBeReachedMayBlock(AccountStatus status) =>
        Assert.True(BlockEligibility.AllowsBlocking(status));

    [Theory]
    [InlineData(AccountStatus.PendingEmail)]
    [InlineData(AccountStatus.PendingEligibility)]
    [InlineData(AccountStatus.DeletionPending)]
    public void PendingOrDeletingAccountMayNotBlock(AccountStatus status) =>
        Assert.False(BlockEligibility.AllowsBlocking(status));

    [Fact]
    public void EveryAccountStatusHasADecision()
    {
        var decided = new[]
        {
            AccountStatus.Active, AccountStatus.Restricted, AccountStatus.Suspended,
            AccountStatus.PendingEmail, AccountStatus.PendingEligibility, AccountStatus.DeletionPending,
        };

        Assert.Equal(Enum.GetValues<AccountStatus>().Order(), decided.Order());
    }

    [Fact]
    public void UnknownStatusIsAProgrammingError() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockEligibility.AllowsBlocking((AccountStatus)99));
}
