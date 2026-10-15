namespace Duora.Core.Events;

/// <summary>Motivo do cancelamento, obrigatório para a auditoria (spec 8.5).</summary>
public sealed record CancellationReason
{
    public const int MaxLength = 500;

    public CancellationReason(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > MaxLength)
        {
            throw new ArgumentException($"O motivo precisa ter de 1 a {MaxLength} caracteres.", nameof(text));
        }

        if (trimmed.Any(IsForbiddenControlCharacter))
        {
            throw new ArgumentException("O motivo não aceita caracteres de controle.", nameof(text));
        }

        Text = trimmed;
    }

    public string Text { get; }

    private static bool IsForbiddenControlCharacter(char character) => char.IsControl(character) && character != '\n';
}

/// <summary>Quem cancelou o evento, por quê e quando.</summary>
public sealed record EventCancellation(Guid CancelledBy, CancellationReason Reason, DateTimeOffset CancelledAt);
