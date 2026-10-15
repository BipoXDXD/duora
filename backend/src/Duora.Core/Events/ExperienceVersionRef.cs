namespace Duora.Core.Events;

/// <summary>Referência à versão publicada de uma experiência do catálogo, por id e número de versão (INV-09).</summary>
public sealed record ExperienceVersionRef
{
    public ExperienceVersionRef(Guid experienceVersionId, int version)
    {
        if (experienceVersionId == Guid.Empty)
        {
            throw new ArgumentException("A versão de experiência precisa de id.", nameof(experienceVersionId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        ExperienceVersionId = experienceVersionId;
        Version = version;
    }

    public Guid ExperienceVersionId { get; }

    public int Version { get; }
}
