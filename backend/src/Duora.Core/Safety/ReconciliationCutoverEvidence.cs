using System.Globalization;

namespace Duora.Core.Safety;

/// <summary>
/// Descritor revisado do corte da reconciliação (issue #321, proposta seção 4): o identificador da evidência de
/// implantação e a janela <see cref="ReconciliationCutover"/>. Vem só da configuração do servidor; o cliente nunca
/// escolhe timestamp nem evidência. Nasce válido: identificador num alfabeto fechado e instantes UTC explícitos.
/// </summary>
public sealed record ReconciliationCutoverEvidence
{
    public const int MaxIdLength = 128;

    // Só o sufixo "Z" literal: um instante sem fuso dependeria do fuso da máquina, e "+00:00" abriria a porta a
    // outros deslocamentos escritos à mão.
    private static readonly string[] UtcFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];

    private ReconciliationCutoverEvidence(string id, ReconciliationCutover cutover)
    {
        Id = id;
        Cutover = cutover;
    }

    /// <summary>Identificador da evidência revisada: letras e dígitos ASCII, <c>-</c>, <c>_</c>, <c>.</c> e <c>:</c>.</summary>
    public string Id { get; }

    public ReconciliationCutover Cutover { get; }

    public static bool TryParse(string? id, string? lower, string? upper, out ReconciliationCutoverEvidence? evidence)
    {
        evidence = null;
        if (!IsValidId(id) || ParseUtc(lower) is not { } lowerBound || ParseUtc(upper) is not { } upperBound
            || !ReconciliationCutover.TryBetween(lowerBound, upperBound, out var cutover))
        {
            return false;
        }

        evidence = new ReconciliationCutoverEvidence(id, cutover!);
        return true;
    }

    private static bool IsValidId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? id) =>
        id is { Length: > 0 and <= MaxIdLength }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private static DateTimeOffset? ParseUtc(string? value) =>
        DateTimeOffset.TryParseExact(value, UtcFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;
}
