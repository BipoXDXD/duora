namespace Duora.Core.Experiences;

/// <summary>
/// Versão otimista do estado da sessão: começa em 1 e sobe uma unidade por ação aplicada ou prazo resolvido.
/// É o <c>expectedVersion</c>/<c>resourceVersion</c> da spec 8.6.
/// </summary>
public readonly record struct StateVersion
{
    public static readonly StateVersion Initial = new(1);

    public StateVersion(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public long Value { get; }

    public StateVersion Next() => new(Value + 1);
}
