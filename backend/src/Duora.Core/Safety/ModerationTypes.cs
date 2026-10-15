namespace Duora.Core.Safety;

public enum ModerationMeasure
{
    Warning,
    Restriction,
    Suspension,
    PermanentBan,

    /// <summary>Sem sanção: só existe como revisão que reverte uma medida em recurso.</summary>
    Cleared,
}

/// <summary>Autor de uma decisão de moderação: uma pessoa identificada ou a automação (spec 12.3).</summary>
public abstract record ModerationAuthor
{
    private ModerationAuthor()
    {
    }

    public bool Is(Guid userId) => this is Human human && human.UserId == userId;

    public sealed record Human : ModerationAuthor
    {
        public Human(Guid userId)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
            UserId = userId;
        }

        public Guid UserId { get; }
    }

    public sealed record Automated : ModerationAuthor;
}

/// <summary>Versão publicada da política aplicada (tabela safety.policy_versions; INV-10).</summary>
public sealed record PolicyVersion
{
    private const int MaxPartLength = 100;

    public PolicyVersion(string code, string version)
    {
        Code = SafetyText.RequireBounded(code, MaxPartLength, allowEmpty: false, nameof(code));
        Version = SafetyText.RequireBounded(version, MaxPartLength, allowEmpty: false, nameof(version));
    }

    public string Code { get; }

    public string Version { get; }
}

/// <summary>
/// O que quem decide informa. <paramref name="Duration"/> é relativa ao instante da decisão, para o fim da medida
/// nunca vir de um relógio de fora.
/// </summary>
public sealed record ModerationDecision(
    ModerationMeasure Measure,
    PolicyVersion PolicyVersion,
    IReadOnlyList<Guid> EvidenceIds,
    string Reason,
    ModerationAuthor Author,
    TimeSpan? Duration = null);
