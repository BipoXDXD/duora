namespace Duora.Contracts.Participation;

public sealed record RoutesCatalogContentRequest(string Title, string Objective, string Example,
    IReadOnlyList<RoutesCatalogStopRequest> Stops, IReadOnlyList<IReadOnlyList<RoutesCatalogClueRequest>> ClueSets);
public sealed record RoutesCatalogStopRequest(string StepId, string Prompt, IReadOnlyList<OptionResponse> Options);
public sealed record RoutesCatalogClueRequest(string Text);
public sealed record PublishRoutesCatalogRequest(int ContentVersion, RoutesCatalogContentRequest EnUS, RoutesCatalogContentRequest PtBR,
    IReadOnlyList<CatalogAudioRequest> Audio, bool EditorialApproved);
