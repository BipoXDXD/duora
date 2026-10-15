namespace Duora.Core.Safety;

public enum ReportPriority
{
    Normal,
    High,
    Urgent,
}

/// <summary>Máquina de estados da denúncia (spec 4.2).</summary>
public enum ReportState
{
    Received,
    Triaged,
    UnderReview,
    Actioned,
    Closed,
    Appealed,
}

/// <summary>
/// O que o denunciante pode ver do andamento. Não distingue medida aplicada de arquivamento nem revela recurso
/// do denunciado, para não expor a situação de outra pessoa (spec 8.4, TM-31).
/// </summary>
public enum ReportStatusForReporter
{
    Received,
    UnderReview,
    Concluded,
}
