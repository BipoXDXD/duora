using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

/// <summary>A11Y-04: toda ação tem instrução em texto e funciona por botão e teclado.</summary>
public sealed class AccessibleInteractionTests
{
    private readonly UnlikelyRoutesEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void EveryActionTypeOfTheExperienceHasATextInstruction()
    {
        var interaction = _engine.DescribeAccessibleInteraction();

        Assert.Equal(
            new UnlikelyRoutesRules(new UnlikelyRoutesSettings(1)).Descriptor.ActionTypes,
            interaction.Actions.Select(action => action.Type));
        Assert.All(interaction.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Instruction)));
    }

    [Fact]
    public void EveryActionWorksWithButtonAndKeyboard()
    {
        var interaction = _engine.DescribeAccessibleInteraction();

        Assert.All(interaction.Actions, action =>
            Assert.Equal([InputMethod.Button, InputMethod.Keyboard], action.InputMethods));
    }

    [Fact]
    public void EveryStageHasATextInstruction()
    {
        var interaction = _engine.DescribeAccessibleInteraction();

        Assert.Equal(
            ["introduction", "clues", "choices", "adjustment", "conclusion"],
            interaction.Stages.Select(stage => stage.StageId));
        Assert.All(interaction.Stages, stage => Assert.False(string.IsNullOrWhiteSpace(stage.Instruction)));
    }

    [Fact]
    public void InteractionIdentifiesTheExperienceAndRulesVersion()
    {
        var interaction = _engine.DescribeAccessibleInteraction();

        Assert.Equal(new ExperienceId("unlikely-routes"), interaction.ExperienceId);
        Assert.Equal(1, interaction.RulesVersion);
    }

    [Fact]
    public void ActionInstructionRequiresAtLeastKeyboardAndButton()
    {
        Assert.Throws<ArgumentException>(
            () => new ActionInstruction(new ActionType("acknowledge"), "Ative o botão.", [InputMethod.Button]));
    }

    [Fact]
    public void AccessibleInteractionRequiresAnInstructionForEveryActionType()
    {
        var descriptor = new UnlikelyRoutesRules(new UnlikelyRoutesSettings(1)).Descriptor;

        Assert.Throws<ArgumentException>(() => new AccessibleInteraction(
            descriptor, "Resumo.", [new StageInstruction("introduction", "Leia.")], actions: []));
    }
}
