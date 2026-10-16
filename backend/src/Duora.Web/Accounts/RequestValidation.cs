using System.Diagnostics.CodeAnalysis;
using Duora.Core.Identity;

namespace Duora.Web.Accounts;

/// <summary>
/// Validação de fronteira dos DTOs de conta: converte para os tipos do domínio ou acumula erros com códigos
/// estáveis por campo. Texto com caractere de controle (inclusive NUL) é recusado aqui, antes de chegar ao banco.
/// </summary>
internal sealed class RequestValidation
{
    public const string Required = "REQUIRED";
    public const string InvalidEmail = "INVALID_EMAIL";
    public const string PasswordTooShort = "PASSWORD_TOO_SHORT";
    public const string PasswordTooLong = "PASSWORD_TOO_LONG";
    public const string InvalidCharacters = "INVALID_CHARACTERS";
    public const string InvalidFormat = "INVALID_FORMAT";
    public const string TooLong = "TOO_LONG";

    /// <summary>Teto do token dos links; os tokens do Identity em base64url ficam bem abaixo.</summary>
    public const int MaxTokenLength = 1024;

    private const int MaxVersionLength = 32;
    private const int AuthenticatorCodeLength = 6;
    private const int MaxTypedAuthenticatorCodeLength = 16;
    private const int MaxRecoveryCodeLength = 32;

    private readonly Dictionary<string, string[]> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public IDictionary<string, string[]> Errors => _errors;

    public bool Email(string field, string? value, [NotNullWhen(true)] out EmailAddress? email)
    {
        email = null;
        if (string.IsNullOrEmpty(value))
        {
            return Fail(field, Required);
        }

        return EmailAddress.TryParse(value, out email) || Fail(field, InvalidEmail);
    }

    /// <summary>Senha nova: política completa (NIST, 12 a 128 caracteres).</summary>
    public bool NewPassword(string field, string? value, [NotNullWhen(true)] out string? password)
    {
        password = value;
        if (value is null)
        {
            return Fail(field, Required);
        }

        return PasswordPolicy.FindProblem(value) switch
        {
            null => true,
            PasswordProblem.TooShort => Fail(field, PasswordTooShort),
            PasswordProblem.TooLong => Fail(field, PasswordTooLong),
            PasswordProblem.ControlCharacter => Fail(field, InvalidCharacters),
            var problem => throw new InvalidOperationException($"Problema de senha não mapeado: {problem}."),
        };
    }

    /// <summary>
    /// Senha no login: sem mínimo (não se ensina a política a quem tenta adivinhar), mas com o teto, que limita o
    /// custo do hash, e sem caractere de controle.
    /// </summary>
    public bool ExistingPassword(string field, string? value, [NotNullWhen(true)] out string? password)
    {
        password = value;
        if (string.IsNullOrEmpty(value))
        {
            return Fail(field, Required);
        }

        return PasswordPolicy.FindProblem(value) switch
        {
            PasswordProblem.TooLong => Fail(field, PasswordTooLong),
            PasswordProblem.ControlCharacter => Fail(field, InvalidCharacters),
            _ => true,
        };
    }

    /// <summary>Versão de documento legal: 1 a 32 caracteres entre letras, dígitos, ponto, hífen e sublinhado.</summary>
    public bool DocumentVersion(string field, string? value, [NotNullWhen(true)] out string? version)
    {
        version = value;
        if (string.IsNullOrEmpty(value))
        {
            return Fail(field, Required);
        }

        if (value.Length > MaxVersionLength)
        {
            return Fail(field, TooLong);
        }

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') || Fail(field, InvalidFormat);
    }

    public bool Token(string field, string? value, [NotNullWhen(true)] out string? token)
    {
        token = value;
        if (string.IsNullOrEmpty(value))
        {
            return Fail(field, Required);
        }

        if (value.Length > MaxTokenLength)
        {
            return Fail(field, TooLong);
        }

        return !value.Any(char.IsControl) || Fail(field, InvalidCharacters);
    }

    /// <summary>
    /// Código do autenticador TOTP: 6 dígitos ASCII, aceitando os espaços com que os apps o mostram ("123 456");
    /// devolve o código sem espaços.
    /// </summary>
    public bool AuthenticatorCode(string field, string? value, [NotNullWhen(true)] out string? code)
    {
        code = null;
        if (string.IsNullOrEmpty(value))
        {
            return Fail(field, Required);
        }

        if (value.Length > MaxTypedAuthenticatorCodeLength)
        {
            return Fail(field, InvalidFormat);
        }

        var normalized = Duora.Core.Identity.Totp.NormalizeInput(value);
        if (normalized.Length != AuthenticatorCodeLength || !normalized.All(char.IsAsciiDigit))
        {
            return Fail(field, InvalidFormat);
        }

        code = normalized;
        return true;
    }

    /// <summary>Código de recuperação: até 32 caracteres, sem caractere de controle.</summary>
    public bool RecoveryCode(string field, string? value, [NotNullWhen(true)] out string? code)
    {
        code = value;
        if (string.IsNullOrEmpty(value))
        {
            return Fail(field, Required);
        }

        if (value.Length > MaxRecoveryCodeLength)
        {
            return Fail(field, TooLong);
        }

        return !value.Any(char.IsControl) || Fail(field, InvalidCharacters);
    }

    /// <summary>Campo que não pode vir junto com outro (ex.: código do autenticador e de recuperação).</summary>
    public bool Exclusive(string field, bool bothPresent) => !bothPresent || Fail(field, InvalidFormat);

    public bool Present<T>(string field, T? value, out T present)
        where T : struct
    {
        present = value.GetValueOrDefault();
        return value.HasValue || Fail(field, Required);
    }

    private bool Fail(string field, string code)
    {
        _errors[field] = [code];
        return false;
    }
}
