using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class AccountSuspensionTests
{
    private const string Reason = "Ofensas repetidas a outras pessoas, contra os Termos.";

    [Fact]
    public void AModeratorSuspensionKeepsWhoDecidedWhyAndWhom()
    {
        var order = SuspensionOrder.ByModerator(ModeratorMaria, Bruno, SuspensionReason.TermsViolation, Reason);

        Assert.Equal(Bruno, order.SubjectUserId);
        Assert.Equal(SuspensionReason.TermsViolation, order.Reason);
        Assert.Equal(Reason, order.InternalReason);
        Assert.Equal(new ModerationAuthor.Human(ModeratorMaria), order.Author);
    }

    [Fact]
    public void TheSystemCannotSuspendForATermsViolation()
    {
        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => SuspensionOrder.Automated(Bruno, SuspensionReason.TermsViolation, Reason));

        Assert.Equal(SafetyRule.MeasureRequiresHumanAuthor, violation.Rule);
    }

    [Fact]
    public void TheSystemMaySuspendAfterAFailedSelfieVerification()
    {
        var order = SuspensionOrder.Automated(Bruno, SuspensionReason.SelfieVerificationFailed, "Limite de tentativas.");

        Assert.Equal(SuspensionReason.SelfieVerificationFailed, order.Reason);
        Assert.Equal(new ModerationAuthor.Automated(), order.Author);
    }

    [Fact]
    public void AModeratorCannotSuspendThemself()
    {
        Assert.Throws<ArgumentException>(
            () => SuspensionOrder.ByModerator(ModeratorMaria, ModeratorMaria, SuspensionReason.TermsViolation, Reason));
    }

    [Fact]
    public void TheSubjectNeedsAnId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SuspensionOrder.ByModerator(ModeratorMaria, Guid.Empty, SuspensionReason.TermsViolation, Reason));
    }

    [Fact]
    public void AnUnknownReasonIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SuspensionOrder.ByModerator(ModeratorMaria, Bruno, (SuspensionReason)99, Reason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("motivo\u0000oculto")]
    public void TheInternalReasonMustBeRealText(string reason)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => SuspensionOrder.ByModerator(ModeratorMaria, Bruno, SuspensionReason.TermsViolation, reason));
    }

    [Fact]
    public void TheInternalReasonFitsExactlyTheCeilingButNotOneMore()
    {
        var atCeiling = new string('a', SuspensionOrder.InternalReasonMaxLength);

        var order = SuspensionOrder.ByModerator(ModeratorMaria, Bruno, SuspensionReason.TermsViolation, atCeiling);

        Assert.Equal(atCeiling, order.InternalReason);
        Assert.Throws<ArgumentOutOfRangeException>(() => SuspensionOrder.ByModerator(
            ModeratorMaria, Bruno, SuspensionReason.TermsViolation, atCeiling + "a"));
    }

    [Fact]
    public void ALiftByAModeratorKeepsTheModeratorAndTheReason()
    {
        var lift = SuspensionLift.ByModerator(ModeratorPedro, "Engano na identificação da conta.");

        Assert.Equal(new ModerationAuthor.Human(ModeratorPedro), lift.Author);
        Assert.Equal("Engano na identificação da conta.", lift.InternalReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    public void ALiftNeedsAReason(string reason)
    {
        Assert.ThrowsAny<ArgumentException>(() => SuspensionLift.ByModerator(ModeratorPedro, reason));
        Assert.ThrowsAny<ArgumentException>(() => SuspensionLift.Automated(reason));
    }

    [Fact]
    public void ASystemCommandKeyIsScopedBySourceAndNeverByAPerson()
    {
        var source = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

        var key = ModerationCommandKey.ForSystem("selfie-verification", source);

        Assert.Equal("system:selfie-verification:00000000-0000-0000-0000-0000000000c1", key.Scope);
        Assert.NotEqual(key, ModerationCommandKey.ForSystem("selfie-verification", Guid.Parse("00000000-0000-0000-0000-0000000000c2")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Selfie")]
    [InlineData("selfie verification")]
    public void ASystemCommandSourceIsACatalogCode(string source)
    {
        Assert.Throws<ArgumentException>(() => ModerationCommandKey.ForSystem(source, Guid.NewGuid()));
    }

    [Fact]
    public void AModeratorCommandKeyIsScopedByTheModerator()
    {
        var maria = ModerationCommandKey.ForModerator(ModeratorMaria, "chave-1");
        var pedro = ModerationCommandKey.ForModerator(ModeratorPedro, "chave-1");

        Assert.NotEqual(maria, pedro);
        Assert.Equal($"moderator:{ModeratorMaria:D}:chave-1", maria.Scope);
    }
}
