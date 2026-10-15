namespace Duora.Core.Experiences.Constellation;

/// <summary>
/// Conjunto fechado de ações de Constelação de Escolhas: o construtor da base é privado, então só os casos abaixo
/// existem. O comentário sobre o cartão é conversa da sala, fora do motor (spec 6.3).
/// </summary>
public abstract record ConstellationAction : IGameAction
{
    internal static readonly ActionType ChooseOptionType = new("choose-option");
    internal static readonly ActionType SkipCardType = new("skip-card");

    private ConstellationAction(ActionType type) => Type = type;

    public ActionType Type { get; }

    /// <summary>
    /// Confirma uma opção aprovada do cartão. O cartão vai junto para um clique atrasado não valer no seguinte.
    /// </summary>
    public sealed record ChooseOption(string CardId, string OptionId) : ConstellationAction(ChooseOptionType);

    /// <summary>
    /// Pula o cartão: ele passa sem mostrar a escolha de ninguém. Com duas pessoas, quem escolheu deduz que a outra
    /// pulou; a saída só não diz isso explicitamente.
    /// </summary>
    public sealed record SkipCard(string CardId) : ConstellationAction(SkipCardType);
}
