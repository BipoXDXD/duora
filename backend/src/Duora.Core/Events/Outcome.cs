namespace Duora.Core.Events;

/// <summary>
/// Resultado de um comando do módulo: aceito com os efeitos que a transação deve gravar na outbox, ou recusado com
/// um motivo tipado. Uma recusa nunca traz efeitos nem altera o agregado.
/// </summary>
public sealed class Outcome<TRefusal>
    where TRefusal : struct, Enum
{
    private Outcome(TRefusal? refusal, IReadOnlyList<Effect> effects)
    {
        Refusal = refusal;
        Effects = effects;
    }

    public TRefusal? Refusal { get; }

    public IReadOnlyList<Effect> Effects { get; }

    public bool IsAccepted => Refusal is null;

    internal static Outcome<TRefusal> Accepted() => new(null, []);

    internal static Outcome<TRefusal> Accepted(IReadOnlyList<Effect> effects) => new(null, effects);

    internal static Outcome<TRefusal> Refused(TRefusal refusal) => new(refusal, []);
}
