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
        Code = SafetyText.RequireText(code, MaxPartLength, nameof(code));
        Version = SafetyText.RequireText(version, MaxPartLength, nameof(version));
    }

    public string Code { get; }

    public string Version { get; }
}

/// <summary>
/// O que a pessoa sancionada lê: o código do motivo no catálogo da política, que liga a medida a uma cláusula da
/// <see cref="PolicyVersion"/>, e um texto curto opcional, escrito de propósito para ela. Nunca leva o motivo interno.
/// </summary>
public sealed record SubjectNotice
{
    public const int ReasonCodeMaxLength = 64;
    public const int TextMaxLength = 500;

    public SubjectNotice(string reasonCode, string? text)
    {
        ReasonCode = SafetyText.RequireCatalogCode(reasonCode, ReasonCodeMaxLength, nameof(reasonCode));
        Text = text is null ? null : SafetyText.RequireText(text, TextMaxLength, nameof(text));
    }

    /// <summary>Código da cláusula no catálogo, como `conduta.assedio`.</summary>
    public string ReasonCode { get; }

    public string? Text { get; }
}

/// <summary>
/// O que quem decide informa. <paramref name="Duration"/> é relativa ao instante da decisão, para o fim da medida
/// nunca vir de um relógio de fora. <paramref name="InternalReason"/> serve à auditoria (INV-10) e não chega à pessoa.
/// </summary>
public sealed record ModerationDecision(
    ModerationMeasure Measure,
    PolicyVersion PolicyVersion,
    IReadOnlyList<Guid> EvidenceIds,
    string InternalReason,
    SubjectNotice Notice,
    ModerationAuthor Author,
    TimeSpan? Duration = null);

/// <summary>Fundamentação da decisão de um recurso, com a mesma separação entre motivo interno e aviso.</summary>
public sealed record AppealRuling(string InternalReason, SubjectNotice Notice, PolicyVersion PolicyVersion);
