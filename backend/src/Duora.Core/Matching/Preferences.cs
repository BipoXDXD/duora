using System.Collections.Frozen;

namespace Duora.Core.Matching;

/// <summary>
/// Preferências explicitamente informadas pela pessoa, como projeção mínima do módulo Profiles (spec 5.1). Critérios
/// e valores são códigos opacos: o Matching não sabe o que significam e não infere nenhum deles.
/// <list type="bullet">
/// <item><c>ownValues</c>: a resposta que a pessoa declarou para cada critério. Critério sem resposta fica ausente.</item>
/// <item><c>acceptedValues</c>: para cada critério que a pessoa restringe, os valores que ela aceita na outra pessoa.
/// Critério ausente não restringe.</item>
/// </list>
/// </summary>
public sealed class Preferences
{
    private readonly FrozenDictionary<string, string> _ownValues;
    private readonly FrozenDictionary<string, FrozenSet<string>> _acceptedValues;

    public Preferences(
        IReadOnlyDictionary<string, string> ownValues,
        IReadOnlyDictionary<string, IReadOnlySet<string>> acceptedValues)
    {
        ArgumentNullException.ThrowIfNull(ownValues);
        ArgumentNullException.ThrowIfNull(acceptedValues);

        _ownValues = ownValues.ToFrozenDictionary(
            answer => RequireCode(answer.Key),
            answer => RequireCode(answer.Value),
            StringComparer.Ordinal);
        _acceptedValues = acceptedValues.ToFrozenDictionary(
            requirement => RequireCode(requirement.Key),
            requirement => FreezeAccepted(requirement.Value),
            StringComparer.Ordinal);
    }

    /// <summary>Pessoa que não declarou respostas nem restrições: aceita qualquer pessoa e só é aceita por quem não exige nada.</summary>
    public static Preferences Unrestricted { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlySet<string>>());

    /// <summary>
    /// Verdadeiro se a outra pessoa declarou, em todo critério que esta restringe, um valor aceito. Critério exigido e
    /// não respondido pela outra pessoa reprova: nada é presumido.
    /// </summary>
    public bool Accepts(Preferences other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (var (criterion, accepted) in _acceptedValues)
        {
            if (!other._ownValues.TryGetValue(criterion, out var declared) || !accepted.Contains(declared))
            {
                return false;
            }
        }

        return true;
    }

    private static FrozenSet<string> FreezeAccepted(IReadOnlySet<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("Critério restrito precisa aceitar ao menos um valor.", nameof(values));
        }

        return values.Select(RequireCode).ToFrozenSet(StringComparer.Ordinal);
    }

    private static string RequireCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code;
    }
}
