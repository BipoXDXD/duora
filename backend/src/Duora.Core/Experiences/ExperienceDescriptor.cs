namespace Duora.Core.Experiences;

/// <summary>O que uma versão de regras publica sobre si (spec 6.1).</summary>
public sealed record ExperienceDescriptor
{
    public ExperienceDescriptor(
        ExperienceId experienceId, int rulesVersion, int schemaVersion, IReadOnlyList<ActionType> actionTypes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rulesVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentNullException.ThrowIfNull(actionTypes);
        if (actionTypes.Count == 0 || actionTypes.Distinct().Count() != actionTypes.Count)
        {
            throw new ArgumentException("Precisa de ao menos um tipo de ação, sem repetição.", nameof(actionTypes));
        }

        ExperienceId = experienceId;
        RulesVersion = rulesVersion;
        SchemaVersion = schemaVersion;
        ActionTypes = [.. actionTypes];
    }

    public ExperienceId ExperienceId { get; }

    public int RulesVersion { get; }

    public int SchemaVersion { get; }

    /// <summary>Conjunto fechado de tipos de ação aceitos por esta versão de regras.</summary>
    public IReadOnlyList<ActionType> ActionTypes { get; }

    public ExperienceVersion WithContent(int contentVersion) =>
        new(ExperienceId, RulesVersion, contentVersion, SchemaVersion);
}
