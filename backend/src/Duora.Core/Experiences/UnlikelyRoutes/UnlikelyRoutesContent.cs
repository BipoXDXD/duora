namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>
/// Conteúdo publicado de Rotas Improváveis: cenário fictício, paradas com opções aprovadas e dois conjuntos de pistas,
/// um para cada participante. Nasce válido e é imutável; uma revisão editorial publica outra versão.
/// </summary>
public sealed record UnlikelyRoutesContent : IExperienceContent
{
    public const int MaxTextLength = 280;
    public const int MaxStops = 6;
    public const int MaxCluesPerSet = 4;
    private const int ClueSetCount = 2;

    public UnlikelyRoutesContent(
        int contentVersion,
        string title,
        string objective,
        string example,
        IReadOnlyList<RouteStop> stops,
        IReadOnlyList<IReadOnlyList<Clue>> clueSets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentVersion);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(clueSets);

        ContentVersion = contentVersion;
        Title = CatalogText.Require(title, MaxTextLength, nameof(title));
        Objective = CatalogText.Require(objective, MaxTextLength, nameof(objective));
        Example = CatalogText.Require(example, MaxTextLength, nameof(example));
        Stops = RequireStops(stops);
        ClueSets = RequireClueSets(clueSets);
    }

    public ExperienceId ExperienceId => UnlikelyRoutesRules.ExperienceId;

    public int ContentVersion { get; }

    public string Title { get; }

    public string Objective { get; }

    public string Example { get; }

    /// <summary>Paradas do roteiro, na ordem de escolha. Uma escolha por parada.</summary>
    public IReadOnlyList<RouteStop> Stops { get; }

    /// <summary>Exatamente dois conjuntos disjuntos; cada participante recebe um, por sorteio.</summary>
    public IReadOnlyList<IReadOnlyList<Clue>> ClueSets { get; }

    private static RouteStop[] RequireStops(IReadOnlyList<RouteStop> stops)
    {
        if (stops.Count is 0 or > MaxStops)
        {
            throw new ArgumentException($"Precisa de 1 a {MaxStops} paradas.", nameof(stops));
        }

        if (stops.Select(stop => stop.StepId).Distinct().Count() != stops.Count)
        {
            throw new ArgumentException("Cada parada precisa de um stepId único.", nameof(stops));
        }

        return [.. stops];
    }

    private static IReadOnlyList<Clue>[] RequireClueSets(IReadOnlyList<IReadOnlyList<Clue>> clueSets)
    {
        if (clueSets.Count != ClueSetCount)
        {
            throw new ArgumentException("Precisa de exatamente dois conjuntos de pistas.", nameof(clueSets));
        }

        if (clueSets.Any(set => set.Count is 0 or > MaxCluesPerSet))
        {
            throw new ArgumentException($"Cada conjunto precisa de 1 a {MaxCluesPerSet} pistas.", nameof(clueSets));
        }

        if (clueSets[0].Select(clue => clue.Text).Intersect(clueSets[1].Select(clue => clue.Text)).Any())
        {
            throw new ArgumentException("Os conjuntos de pistas não podem compartilhar uma pista.", nameof(clueSets));
        }

        return [.. clueSets.Select(set => (IReadOnlyList<Clue>)[.. set])];
    }
}

/// <summary>Parada do roteiro, identificada pelo <c>stepId</c> do comando de jogo (spec 8.6).</summary>
public sealed record RouteStop
{
    public const int MaxOptions = 4;
    private const int MinOptions = 2;

    public RouteStop(string stepId, string prompt, IReadOnlyList<RouteOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count is < MinOptions or > MaxOptions)
        {
            throw new ArgumentException($"Precisa de {MinOptions} a {MaxOptions} opções.", nameof(options));
        }

        if (options.Select(option => option.OptionId).Distinct().Count() != options.Count)
        {
            throw new ArgumentException("Cada opção precisa de um optionId único na parada.", nameof(options));
        }

        StepId = Slug.Require(stepId, nameof(stepId));
        Prompt = CatalogText.Require(prompt, UnlikelyRoutesContent.MaxTextLength, nameof(prompt));
        Options = [.. options];
    }

    public string StepId { get; }

    public string Prompt { get; }

    public IReadOnlyList<RouteOption> Options { get; }

    public RouteOption? FindOption(string optionId) => Options.FirstOrDefault(option => option.OptionId == optionId);
}

/// <summary>Opção aprovada para uma parada.</summary>
public sealed record RouteOption
{
    public RouteOption(string optionId, string label)
    {
        OptionId = Slug.Require(optionId, nameof(optionId));
        Label = CatalogText.Require(label, UnlikelyRoutesContent.MaxTextLength, nameof(label));
    }

    public string OptionId { get; }

    public string Label { get; }
}

/// <summary>Pista privada: só chega ao participante que a recebeu (INV-05).</summary>
public sealed record Clue
{
    public Clue(string text) => Text = CatalogText.Require(text, UnlikelyRoutesContent.MaxTextLength, nameof(text));

    public string Text { get; }
}
