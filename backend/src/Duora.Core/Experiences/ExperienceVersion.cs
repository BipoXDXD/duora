namespace Duora.Core.Experiences;

/// <summary>
/// Versões publicadas que uma sessão de jogo fixa ao nascer (INV-09): regras (código), conteúdo (catálogo) e
/// schema do estado persistido.
/// </summary>
public sealed record ExperienceVersion
{
    public ExperienceVersion(ExperienceId experienceId, int rulesVersion, int contentVersion, int schemaVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rulesVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);

        ExperienceId = experienceId;
        RulesVersion = rulesVersion;
        ContentVersion = contentVersion;
        SchemaVersion = schemaVersion;
    }

    public ExperienceId ExperienceId { get; }

    public int RulesVersion { get; }

    public int ContentVersion { get; }

    public int SchemaVersion { get; }
}
