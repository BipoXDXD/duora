namespace Duora.Core.Safety;

/// <summary>
/// Código de uma categoria de denúncia (PK de safety.report_categories). O conjunto vem da lookup table, porque a
/// política muda sem deploy (regra SQL 6); o domínio só garante o formato.
/// </summary>
public sealed record ReportCategoryCode
{
    public const int MaxLength = 64;

    public ReportCategoryCode(string value) => Value = SafetyText.RequireCatalogCode(value, MaxLength, nameof(value));

    public string Value { get; }
}

/// <summary>
/// Categoria como o caso de uso a lê da lookup table, ativa, com a severidade como dado (`is_severe`). Denúncia
/// grave chega como urgente e a triagem automatizada não a rebaixa (spec 19.3).
/// </summary>
public sealed record ReportCategory(ReportCategoryCode Code, bool IsSevere);
