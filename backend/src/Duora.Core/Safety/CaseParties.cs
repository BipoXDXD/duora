namespace Duora.Core.Safety;

/// <summary>
/// Pessoas envolvidas num caso: quem sofre a medida e, se houver denúncia, quem denunciou. Nenhuma delas tria,
/// revisa ou decide o próprio caso (spec 12.3, TM-34).
/// </summary>
public sealed record CaseParties
{
    public CaseParties(Guid subjectUserId, Guid? reporterId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(subjectUserId, Guid.Empty);
        if (reporterId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(reporterId), "Denunciante precisa de id.");
        }

        if (reporterId == subjectUserId)
        {
            throw new ArgumentException("Denunciante e pessoa denunciada são pessoas diferentes.", nameof(reporterId));
        }

        SubjectUserId = subjectUserId;
        ReporterId = reporterId;
    }

    public Guid SubjectUserId { get; }

    /// <summary>Nulo quando a medida nasce de sinal de abuso, sem denúncia.</summary>
    public Guid? ReporterId { get; }

    public bool Includes(Guid person) => person == SubjectUserId || person == ReporterId;
}
