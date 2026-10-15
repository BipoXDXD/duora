namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>
/// Projeção de Rotas Improváveis para um participante (glossário: <c>ParticipantView</c>). Montada do zero: pistas
/// do outro, sorteio, ids de participante e registro de ações não existem neste tipo (INV-05).
/// </summary>
public sealed record UnlikelyRoutesView(
    string ExperienceId,
    int RulesVersion,
    int ContentVersion,
    int SchemaVersion,
    long ResourceVersion,
    SessionStatus Status,
    DateTimeOffset Deadline,
    RoutesStage Stage,
    string Title,
    string Objective,
    string Example,
    IReadOnlyList<string> MyClues,
    RouteStopView? CurrentStop,
    bool IsMyTurn,
    IReadOnlyList<ChosenStopView> Route,
    int RevisionsRemaining,
    bool AwaitingPartner,
    IReadOnlyList<string> AvailableActions);

public sealed record RouteStopView(string StepId, string Prompt, IReadOnlyList<RouteOptionView> Options);

public sealed record RouteOptionView(string OptionId, string Label);

public sealed record ChosenStopView(string StepId, string Prompt, string OptionLabel);
