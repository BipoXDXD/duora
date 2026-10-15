namespace Duora.Core.Safety;

/// <summary>
/// Relato livre da denúncia. Opcional: categoria e contexto bastam para abrir o caso, e exigir texto atrasaria
/// quem está em situação de risco.
/// </summary>
public sealed record ReportNarrative
{
    /// <summary>Em caracteres Unicode; mesmo teto do `narrative` em safety.reports.</summary>
    public const int MaxLength = 2000;

    private ReportNarrative(string text) => Text = text;

    public static ReportNarrative Empty { get; } = new(string.Empty);

    public string Text { get; }

    public static ReportNarrative Parse(string? text) =>
        text is null ? Empty : new(SafetyText.RequireBounded(text, MaxLength, allowEmpty: true, nameof(text)));
}
