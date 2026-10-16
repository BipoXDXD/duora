using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Duora.Infrastructure.Audit;

/// <summary>
/// Quem rodou um comando operacional no host (<c>--operator</c>), no formato de nome de usuário de sistema:
/// minúsculas, dígitos, <c>.</c>, <c>_</c> e <c>-</c>, sem começar por <c>.</c> ou <c>-</c>, até 32 caracteres. Não é texto
/// livre: o mesmo formato vale como CHECK em <c>ops.audit_events.actor_label</c>.
/// </summary>
public sealed partial record OperatorName
{
    public const int MaxLength = 32;

    /// <summary>
    /// Formato do CHECK no banco. No PostgreSQL, <c>$</c> só casa no fim do texto; no .NET também casa antes de um
    /// <c>\n</c> final, por isso a validação daqui usa <c>\z</c>.
    /// </summary>
    public const string DatabasePattern = "^[a-z0-9_][a-z0-9._-]{0,31}$";

    private OperatorName(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out OperatorName? name)
    {
        name = value is not null && Format().IsMatch(value) ? new OperatorName(value) : null;
        return name is not null;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"\A[a-z0-9_][a-z0-9._-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
