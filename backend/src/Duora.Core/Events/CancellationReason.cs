using System.Diagnostics.CodeAnalysis;

namespace Duora.Core.Events;

/// <summary>
/// Motivo do cancelamento, obrigatório para a auditoria (spec 8.5). Nasce só por <see cref="Parse"/>, então todo
/// motivo existente já está aparado, tem de 1 a <see cref="MaxLength"/> caracteres e não tem caracteres de controle
/// além da quebra de linha.
/// </summary>
public sealed record CancellationReason
{
    public const int MaxLength = 500;

    private CancellationReason(string text) => Text = text;

    public string Text { get; }

    /// <summary>Converte o texto vindo do administrador no motivo ou numa recusa tipada.</summary>
    public static CancellationReasonParse Parse(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return CancellationReasonParse.Refused(CancellationReasonRefusal.Blank);
        }

        if (trimmed.Length > MaxLength)
        {
            return CancellationReasonParse.Refused(CancellationReasonRefusal.TooLong);
        }

        if (trimmed.Any(IsForbiddenControlCharacter))
        {
            return CancellationReasonParse.Refused(CancellationReasonRefusal.ControlCharacter);
        }

        return CancellationReasonParse.Parsed(new CancellationReason(trimmed));
    }

    private static bool IsForbiddenControlCharacter(char character) => char.IsControl(character) && character != '\n';
}

/// <summary>Por que um texto não serve como motivo de cancelamento.</summary>
public enum CancellationReasonRefusal
{
    /// <summary>Ausente, vazio ou só com espaços.</summary>
    Blank = 1,

    /// <summary>Mais de <see cref="CancellationReason.MaxLength"/> caracteres depois de aparado.</summary>
    TooLong,

    /// <summary>Caractere de controle (NUL, tabulação, sino...); só a quebra de linha é aceita.</summary>
    ControlCharacter,
}

/// <summary>Resultado de <see cref="CancellationReason.Parse"/>: o motivo válido ou a recusa.</summary>
public sealed class CancellationReasonParse
{
    private CancellationReasonParse(CancellationReason? reason, CancellationReasonRefusal? refusal)
    {
        Reason = reason;
        Refusal = refusal;
    }

    public CancellationReason? Reason { get; }

    public CancellationReasonRefusal? Refusal { get; }

    [MemberNotNullWhen(true, nameof(Reason))]
    public bool IsValid => Reason is not null;

    internal static CancellationReasonParse Parsed(CancellationReason reason) => new(reason, null);

    internal static CancellationReasonParse Refused(CancellationReasonRefusal refusal) => new(null, refusal);
}

/// <summary>Quem cancelou o evento, por quê e quando.</summary>
public sealed record EventCancellation(Guid CancelledBy, CancellationReason Reason, DateTimeOffset CancelledAt);
