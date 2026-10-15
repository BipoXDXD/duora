namespace Duora.Core.Experiences;

/// <summary>
/// Instruções em texto e meios de interação de uma versão de experiência (spec 6.1, A11Y-04). Toda ação de todo jogo
/// funciona por botão e teclado.
/// </summary>
public sealed record AccessibleInteraction
{
    private const int MaxSummaryLength = 600;

    public AccessibleInteraction(
        ExperienceDescriptor descriptor,
        string summary,
        IReadOnlyList<StageInstruction> stages,
        IReadOnlyList<ActionInstruction> actions)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(actions);
        if (stages.Count == 0)
        {
            throw new ArgumentException("Toda experiência descreve ao menos uma etapa.", nameof(stages));
        }

        if (!actions.Select(action => action.Type).SequenceEqual(descriptor.ActionTypes))
        {
            throw new ArgumentException(
                "Precisa de exatamente uma instrução por tipo de ação do descritor, na mesma ordem.", nameof(actions));
        }

        ExperienceId = descriptor.ExperienceId;
        RulesVersion = descriptor.RulesVersion;
        Summary = CatalogText.Require(summary, MaxSummaryLength, nameof(summary));
        Stages = [.. stages];
        Actions = [.. actions];
    }

    public ExperienceId ExperienceId { get; }

    public int RulesVersion { get; }

    public string Summary { get; }

    public IReadOnlyList<StageInstruction> Stages { get; }

    public IReadOnlyList<ActionInstruction> Actions { get; }
}

public sealed record StageInstruction
{
    private const int MaxInstructionLength = 400;

    public StageInstruction(string stageId, string instruction)
    {
        StageId = Slug.Require(stageId, nameof(stageId));
        Instruction = CatalogText.Require(instruction, MaxInstructionLength, nameof(instruction));
    }

    public string StageId { get; }

    public string Instruction { get; }
}

public sealed record ActionInstruction
{
    private const int MaxInstructionLength = 400;

    public ActionInstruction(ActionType type, string instruction, IReadOnlyList<InputMethod> inputMethods)
    {
        ArgumentNullException.ThrowIfNull(inputMethods);
        if (!inputMethods.Contains(InputMethod.Button) || !inputMethods.Contains(InputMethod.Keyboard))
        {
            throw new ArgumentException("Toda ação precisa funcionar por botão e por teclado.", nameof(inputMethods));
        }

        Type = type;
        Instruction = CatalogText.Require(instruction, MaxInstructionLength, nameof(instruction));
        InputMethods = [.. inputMethods.Distinct()];
    }

    public ActionType Type { get; }

    public string Instruction { get; }

    public IReadOnlyList<InputMethod> InputMethods { get; }
}

/// <summary>
/// Meios de executar uma ação. Arrastar, depender de cor ou de tempo de reação não existem neste vocabulário de
/// propósito: nenhuma experiência pode exigi-los (A11Y-04).
/// </summary>
public enum InputMethod
{
    Button = 1,
    Keyboard = 2,
}
