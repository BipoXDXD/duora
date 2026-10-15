namespace Duora.Core.Matching;

/// <summary>
/// Duas contas distintas sem direção, normalizadas como <c>user_low &lt; user_high</c> (spec 10.3). Representa o
/// par selecionado e também as restrições sem direção que o seletor recebe: bloqueio (que tem efeito bilateral) e
/// par já formado no evento.
/// </summary>
public sealed record UserPair
{
    private UserPair(Guid userLow, Guid userHigh)
    {
        UserLow = userLow;
        UserHigh = userHigh;
    }

    /// <summary>
    /// Menor id pela ordem do <see cref="Guid.CompareTo(Guid)"/>, que coincide com a do <c>uuid</c> do PostgreSQL
    /// (byte a byte, como a forma textual).
    /// </summary>
    public Guid UserLow { get; }

    public Guid UserHigh { get; }

    public static UserPair Of(Guid first, Guid second)
    {
        if (first == Guid.Empty || second == Guid.Empty)
        {
            throw new ArgumentException("Um par exige duas contas identificadas.");
        }

        if (first == second)
        {
            throw new ArgumentException("Um par exige duas contas diferentes.");
        }

        return first.CompareTo(second) < 0 ? new UserPair(first, second) : new UserPair(second, first);
    }

    public override string ToString() => $"{UserLow}–{UserHigh}";
}
