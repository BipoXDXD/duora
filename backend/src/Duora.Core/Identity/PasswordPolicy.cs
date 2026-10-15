namespace Duora.Core.Identity;

public enum PasswordProblem
{
    TooShort,
    TooLong,
    ControlCharacter,
}

/// <summary>
/// Política de senha da NIST SP 800-63B: só comprimento, sem regras de composição. Qualquer caractere imprimível
/// vale, inclusive espaço e Unicode, para não atrapalhar frases-senha nem gerenciadores de senha.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;

    /// <summary>Teto que também limita o custo do hash por requisição.</summary>
    public const int MaxLength = 128;

    /// <summary>Devolve o primeiro problema encontrado, ou null quando a senha é aceitável.</summary>
    public static PasswordProblem? FindProblem(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Any(char.IsControl))
        {
            return PasswordProblem.ControlCharacter;
        }

        // Conta caracteres (Unicode scalar values), não unidades UTF-16: um emoji conta como um.
        var length = password.EnumerateRunes().Count();
        return length switch
        {
            < MinLength => PasswordProblem.TooShort,
            > MaxLength => PasswordProblem.TooLong,
            _ => null,
        };
    }
}
