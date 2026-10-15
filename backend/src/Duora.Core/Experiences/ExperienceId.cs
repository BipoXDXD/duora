namespace Duora.Core.Experiences;

/// <summary>Identificador estável de uma experiência do catálogo, como <c>unlikely-routes</c>.</summary>
public readonly record struct ExperienceId
{
    public ExperienceId(string value) => Value = Slug.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}
