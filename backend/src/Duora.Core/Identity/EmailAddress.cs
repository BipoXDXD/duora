using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;

namespace Duora.Core.Identity;

/// <summary>
/// Endereço de e-mail já validado na fronteira. Não normaliza (não apara espaços nem muda caixa): o que a pessoa
/// digitou é o que se guarda, e a busca usa o normalizador do Identity.
/// </summary>
public sealed record EmailAddress
{
    /// <summary>Limite de caminho do RFC 5321 menos os delimitadores; é o limite do contrato da API.</summary>
    public const int MaxLength = 254;

    private const int MaxLocalPartLength = 64;

    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out EmailAddress? address)
    {
        address = IsAcceptable(value) ? new EmailAddress(value) : null;
        return address is not null;
    }

    public override string ToString() => Value;

    private static bool IsAcceptable([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value.Any(IsForbiddenCharacter))
        {
            return false;
        }

        // MailAddress aceita "Nome <a@b>"; comparar o endereço extraído com a entrada recusa essas formas.
        if (!MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
        {
            return false;
        }

        // Exigir um ponto no domínio recusa "ana@localhost", que nenhum provedor real entrega.
        return parsed.User.Length <= MaxLocalPartLength && parsed.Host.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsForbiddenCharacter(char character) => char.IsControl(character) || char.IsWhiteSpace(character);
}
