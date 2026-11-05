using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Events;

namespace Duora.Core.Safety;

/// <summary>Uma inscrição candidata, com a versão observada e o teto de vagas liberadas e de promoções.</summary>
public sealed record ReconciliationPreviewItem(
    Guid EventId,
    long RegistrationVersion,
    RegistrationState State,
    int MaxReleasedSeats,
    int MaxPromotions);

/// <summary>
/// Digest de uma unidade da reconciliação (ADR-0049, D1): SHA-256, em hexadecimal minúsculo, do texto canônico v1
/// em UTF-8. Prova que o moderador viu a mesma unidade que o servidor recalcula; não é segredo nem assinatura.
/// </summary>
/// <remarks>
/// O texto tem uma linha terminada em <c>\n</c> por campo, com o id da evidência no alfabeto fechado do descritor,
/// instantes em UTC com seis casas (a precisão do PostgreSQL), UUIDs no formato <c>D</c> minúsculo e os itens na ordem
/// ordinal do <c>eventId</c>, que é a ordem do UUID no PostgreSQL e não a de <see cref="Guid.CompareTo(Guid)"/>.
/// Mudar qualquer parte exige outra versão na primeira linha: prévias em circulação deixam de bater.
/// </remarks>
public static class ReconciliationUnitDigest
{
    /// <summary>Caracteres hexadecimais de um SHA-256.</summary>
    public const int Length = 64;

    private const string Header = "duora.registration-reconciliation.unit-digest.v1";
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static string Compute(
        int selectorVersion,
        ReconciliationCutoverEvidence cutover,
        Guid subjectUserId,
        Guid originalMeasureId,
        long measureVersion,
        IReadOnlyList<ReconciliationPreviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(cutover);
        ArgumentNullException.ThrowIfNull(items);
        var text = new StringBuilder();
        Line(text, Header);
        Line(text, string.Create(CultureInfo.InvariantCulture, $"selectorVersion={selectorVersion}"));
        Line(text, $"evidenceId={cutover.Id}");
        Line(text, $"lower={Instant(cutover.Cutover.Lower)}");
        Line(text, $"upper={Instant(cutover.Cutover.Upper)}");
        Line(text, $"subjectUserId={Uuid(subjectUserId)}");
        Line(text, $"originalMeasureId={Uuid(originalMeasureId)}");
        Line(text, string.Create(CultureInfo.InvariantCulture, $"measureVersion={measureVersion}"));
        foreach (var item in items.OrderBy(item => Uuid(item.EventId), StringComparer.Ordinal))
        {
            Line(text, string.Create(
                CultureInfo.InvariantCulture,
                $"item={Uuid(item.EventId)};{item.RegistrationVersion};{item.State};{item.MaxReleasedSeats};{item.MaxPromotions}"));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Sessenta e quatro dígitos hexadecimais minúsculos.</summary>
    public static bool IsWellFormed(string? digest) =>
        digest is { Length: Length } && digest.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static void Line(StringBuilder text, string line) => text.Append(line).Append('\n');

    private static string Uuid(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);
}
