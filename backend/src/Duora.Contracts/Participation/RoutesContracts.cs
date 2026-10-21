namespace Duora.Contracts.Participation;

public sealed record RoutesActionRequest(Guid ActionId, string ExpectedVersion, string Type, string? Stage, string? StepId, string? OptionId);
public sealed record RoutesGameResponse(string ExperienceId, int RulesVersion, int ContentVersion, int SchemaVersion,
    string ResourceVersion, string Status, DateTimeOffset Deadline, string Stage, string Title, string Objective,
    string Example, IReadOnlyList<string> MyClues, RouteStopResponse? CurrentStop, bool IsMyTurn,
    IReadOnlyList<ChosenStopResponse> Route, int RevisionsRemaining, bool AwaitingPartner, IReadOnlyList<string> AvailableActions);
public sealed record RouteStopResponse(string StepId, string Prompt, IReadOnlyList<OptionResponse> Options);
public sealed record ChosenStopResponse(string StepId, string Prompt, string OptionLabel);
