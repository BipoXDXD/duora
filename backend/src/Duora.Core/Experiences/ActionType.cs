namespace Duora.Core.Experiences;

/// <summary>Nome estável de um tipo de ação (<c>choose-option</c>), o campo <c>type</c> do comando da spec 8.6.</summary>
public readonly record struct ActionType
{
    public ActionType(string value) => Value = Slug.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}
