using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;

namespace Duora.UnitTests.Experiences;

/// <summary>
/// Forma canônica da ação (spec 8.7, ADR-0020): o hash precisa ser o mesmo em qualquer processo e release, porque
/// vai para <c>game_actions.payload_sha256</c> e decide reenvio depois de um reinício. Os valores esperados foram
/// calculados fora do código, com <c>printf '%s' '&lt;forma canônica&gt;' | shasum -a 256</c>.
/// </summary>
public sealed class ActionFingerprintTests
{
    [Fact]
    public void ChooseOptionHasAFixedHash()
    {
        // 20:duora.game-action/v1,13:choose-option,1:2,8:OptionId,12:s:lighthouse,6:StepId,14:s:route-stop-1,
        var fingerprint = ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));

        Assert.Equal("a1189611ecb6f80d5a695da6e0553480f62add160cc815305117d4e3c31dd797", fingerprint.Sha256Hex);
    }

    [Fact]
    public void EnumFieldIsHashedByItsDeclaredValue()
    {
        // 20:duora.game-action/v1,11:acknowledge,1:1,5:Stage,3:e:2,
        var fingerprint = ActionFingerprint.Of(new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues));

        Assert.Equal("6c7dfe6f25b3909d162af777f1f245d50a8fdd73f4a36d10c3e8e72e6eba27b2", fingerprint.Sha256Hex);
    }

    [Fact]
    public void LengthPrefixCountsUtf8BytesNotCharacters()
    {
        // 20:duora.game-action/v1,13:choose-option,1:2,8:OptionId,7:s:café,6:StepId,14:s:route-stop-1,
        var fingerprint = ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", "café"));

        Assert.Equal("9307fdce03c85ecfe1bd2498efc7b0b2842de0fc7bee7740cdd0ef43ecd10e26", fingerprint.Sha256Hex);
    }

    [Fact]
    public void ActionWithoutFieldsIsHashedByItsType()
    {
        // 20:duora.game-action/v1,13:confirm-route,1:0,
        var fingerprint = ActionFingerprint.Of(new UnlikelyRoutesAction.ConfirmRoute());

        Assert.Equal("44f6ee8d185b40cbbb362b37d2fe172c7d2ebd054f49bb2a3485eb235ec38269", fingerprint.Sha256Hex);
    }

    [Fact]
    public void ActionsOfDifferentTypesWithoutFieldsDiffer()
    {
        Assert.NotEqual(
            ActionFingerprint.Of(new UnlikelyRoutesAction.ConfirmRoute()),
            ActionFingerprint.Of(new UnlikelyRoutesAction.RequestRevision()));
    }

    [Fact]
    public void MovingCharactersBetweenFieldsChangesTheHash()
    {
        Assert.NotEqual(
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("ab", "c")),
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("a", "bc")));
    }

    [Fact]
    public void NullAndEmptyStringDiffer()
    {
        // O record não valida os campos, então null chega aqui se a fronteira falhar; o "!" simula isso.
        Assert.NotEqual(
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", null!)),
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", "")));
    }

    [Fact]
    public void EqualActionsBuiltSeparatelyHaveTheSameHash()
    {
        Assert.Equal(
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse")),
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse")));
    }

    [Fact]
    public void FieldOfUnsupportedTypeFailsLoudly()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => ActionFingerprint.Of(new ActionWithInstant(DateTimeOffset.UnixEpoch)));

        Assert.Contains(nameof(ActionWithInstant.At), error.Message, StringComparison.Ordinal);
    }

    private sealed record ActionWithInstant(DateTimeOffset At) : IGameAction
    {
        public ActionType Type { get; } = new("with-instant");
    }
}
