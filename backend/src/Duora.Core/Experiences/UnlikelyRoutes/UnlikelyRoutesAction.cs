namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>
/// Conjunto fechado de ações de Rotas Improváveis: o construtor da base é privado, então só os casos abaixo existem.
/// </summary>
public abstract record UnlikelyRoutesAction : IGameAction
{
    internal static readonly ActionType AcknowledgeType = new("acknowledge");
    internal static readonly ActionType ChooseOptionType = new("choose-option");
    internal static readonly ActionType ConfirmRouteType = new("confirm-route");
    internal static readonly ActionType RequestRevisionType = new("request-revision");

    private UnlikelyRoutesAction(ActionType type) => Type = type;

    public ActionType Type { get; }

    /// <summary>"Entendi" na introdução ou nas pistas. A etapa vai junto para um clique atrasado não valer na seguinte.</summary>
    public sealed record Acknowledge(RoutesStage Stage) : UnlikelyRoutesAction(AcknowledgeType);

    /// <summary>Escolha de uma opção aprovada para a parada da vez (spec 8.6).</summary>
    public sealed record ChooseOption(string StepId, string OptionId) : UnlikelyRoutesAction(ChooseOptionType);

    public sealed record ConfirmRoute() : UnlikelyRoutesAction(ConfirmRouteType);

    /// <summary>Apaga o roteiro e reabre as escolhas, se ainda houver revisão disponível.</summary>
    public sealed record RequestRevision() : UnlikelyRoutesAction(RequestRevisionType);
}
