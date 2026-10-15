namespace Duora.Core.Experiences;

/// <summary>Id gerado pelo cliente para uma ação; reenviar o mesmo id não repete o efeito (INV-06).</summary>
public readonly record struct ActionId
{
    public ActionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Não pode ser o GUID vazio.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}
