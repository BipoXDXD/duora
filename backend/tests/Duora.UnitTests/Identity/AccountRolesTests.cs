using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class AccountRolesTests
{
    [Theory]
    [InlineData(AccountRoles.Moderator)]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public void ModerationAndAdministrationRolesArePrivileged(string role)
    {
        Assert.True(AccountRoles.IsPrivileged(role));
    }

    [Theory]
    [InlineData(AccountRoles.Participant)]
    [InlineData("MODERATOR")]
    [InlineData("admin")]
    [InlineData("")]
    public void ParticipantAndUnknownNamesAreNotPrivileged(string role)
    {
        Assert.False(AccountRoles.IsPrivileged(role));
    }

    [Fact]
    public void PolicyOfAnAccountWithoutRolesIsNormal()
    {
        Assert.Equal(SessionPolicy.Normal, AccountRoles.SessionPolicyFor([]));
    }

    [Fact]
    public void PolicyOfAnAccountWithAnyPrivilegedRoleIsAdministrative()
    {
        Assert.Equal(
            SessionPolicy.Administrative,
            AccountRoles.SessionPolicyFor([AccountRoles.Participant, AccountRoles.Operator]));
    }

    [Fact]
    public void TheCatalogHasExactlyTheFourFixedRoles()
    {
        Assert.Equal(
            ["moderator", "operator", "participant", "product_admin"],
            AccountRoles.All.Order(StringComparer.Ordinal));
    }
}
