namespace Duora.Core.Safety;

/// <summary>
/// Dados de uma nova denúncia. O denunciante vem da sessão, nunca do corpo (TM-04); o caso de uso confere
/// antes que ele participou do contexto informado.
/// </summary>
public sealed record ReportSubmission(
    Guid ReporterId,
    Guid TargetUserId,
    ReportCategory Category,
    ReportContext Context,
    ReportNarrative Narrative);

/// <summary>Resultado de "denunciar e bloquear", a opção explícita que produz os dois efeitos (spec 7.3).</summary>
public sealed record ReportAndBlock(Report Report, Block Block);
