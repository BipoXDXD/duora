using System.Collections.ObjectModel;

namespace Duora.Core.Experiences.Constellation;

/// <summary>
/// Conteúdo publicado de Constelação de Escolhas: cartões leves com opções aprovadas, o mesmo conjunto para as duas
/// pessoas. Nasce válido e é imutável; uma revisão editorial publica outra versão (spec 6.3, 6.4).
/// </summary>
public sealed record ConstellationContent : IExperienceContent
{
    public const int MaxTextLength = 280;

    /// <summary>Teto que cabe no jogo de até 8 minutos com conversa sobre cada cartão.</summary>
    public const int MaxCards = 6;

    public ConstellationContent(
        int contentVersion, string title, string objective, IReadOnlyList<ConstellationCard> cards)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentVersion);
        ArgumentNullException.ThrowIfNull(cards);

        ContentVersion = contentVersion;
        Title = CatalogText.Require(title, MaxTextLength, nameof(title));
        Objective = CatalogText.Require(objective, MaxTextLength, nameof(objective));
        Cards = RequireCards(cards);
    }

    public ExperienceId ExperienceId => ConstellationRules.ExperienceId;

    public int ContentVersion { get; }

    public string Title { get; }

    public string Objective { get; }

    /// <summary>Cartões na ordem em que aparecem; os dois participantes respondem ao mesmo cartão ao mesmo tempo.</summary>
    public IReadOnlyList<ConstellationCard> Cards { get; }

    private static ReadOnlyCollection<ConstellationCard> RequireCards(IReadOnlyList<ConstellationCard> cards)
    {
        if (cards.Count is 0 or > MaxCards)
        {
            throw new ArgumentException($"Precisa de 1 a {MaxCards} cartões.", nameof(cards));
        }

        if (cards.Select(card => card.CardId).Distinct().Count() != cards.Count)
        {
            throw new ArgumentException("Cada cartão precisa de um cardId único.", nameof(cards));
        }

        return Array.AsReadOnly(cards.ToArray());
    }
}

/// <summary>Cartão com uma pergunta leve e opções aprovadas.</summary>
public sealed record ConstellationCard
{
    public const int MaxOptions = 4;
    private const int MinOptions = 2;

    public ConstellationCard(string cardId, string prompt, IReadOnlyList<ConstellationOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count is < MinOptions or > MaxOptions)
        {
            throw new ArgumentException($"Precisa de {MinOptions} a {MaxOptions} opções.", nameof(options));
        }

        if (options.Select(option => option.OptionId).Distinct().Count() != options.Count)
        {
            throw new ArgumentException("Cada opção precisa de um optionId único no cartão.", nameof(options));
        }

        CardId = Slug.Require(cardId, nameof(cardId));
        Prompt = CatalogText.Require(prompt, ConstellationContent.MaxTextLength, nameof(prompt));
        Options = Array.AsReadOnly(options.ToArray());
    }

    public string CardId { get; }

    public string Prompt { get; }

    public IReadOnlyList<ConstellationOption> Options { get; }

    public ConstellationOption? FindOption(string optionId) =>
        Options.FirstOrDefault(option => option.OptionId == optionId);
}

/// <summary>Opção aprovada de um cartão.</summary>
public sealed record ConstellationOption
{
    public ConstellationOption(string optionId, string label)
    {
        OptionId = Slug.Require(optionId, nameof(optionId));
        Label = CatalogText.Require(label, ConstellationContent.MaxTextLength, nameof(label));
    }

    public string OptionId { get; }

    public string Label { get; }
}
