using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.UnitTests.Experiences.Constellation.ConstellationFixture;

namespace Duora.UnitTests.Experiences.Constellation;

/// <summary>A11Y-04 e A11Y-08: instruções em texto, só botão e teclado, e prazo explicado sem depender de reflexo.</summary>
public sealed class ConstellationAccessibilityTests
{
    private readonly AccessibleInteraction _interaction = Engine(ClockAtRoundStart()).DescribeAccessibleInteraction();

    [Fact]
    public void EveryActionTypeHasATextInstructionInDescriptorOrder()
    {
        Assert.Equal(["choose-option", "skip-card"], _interaction.Actions.Select(action => action.Type.Value));
        Assert.Equal(new ConstellationRules().Descriptor.ActionTypes, _interaction.Actions.Select(action => action.Type));
    }

    [Fact]
    public void EveryActionWorksWithButtonAndKeyboardOnly()
    {
        Assert.All(_interaction.Actions, action =>
            Assert.Equal([InputMethod.Button, InputMethod.Keyboard], action.InputMethods));
    }

    [Fact]
    public void EveryStageHasATextInstruction()
    {
        Assert.Equal(["cards", "summary"], _interaction.Stages.Select(stage => stage.StageId));
    }

    [Fact]
    public void SummaryExplainsTheDeadlineAndThatNothingDependsOnReflexes()
    {
        Assert.Contains("prazo", _interaction.Summary, StringComparison.Ordinal);
        Assert.Contains("tempo de reação", _interaction.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractionIdentifiesTheExperienceAndRulesVersion()
    {
        Assert.Equal((new ExperienceId("choice-constellation"), 1), (_interaction.ExperienceId, _interaction.RulesVersion));
    }
}
