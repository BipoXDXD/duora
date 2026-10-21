namespace Duora.Contracts.Participation;

public sealed record EventResponse(Guid EventId, string Title, DateTimeOffset StartsAt, DateTimeOffset CheckInOpensAt, string State, string? TimeZone);
public sealed record ParticipationResponse(string State, Guid? RoomId);
public sealed record RoomActionRequest(Guid ActionId, string ExpectedVersion, string Type, string CardId, string? OptionId);
public sealed record RoomResponse(Guid RoomId, string State, string Version, string CommunicationLanguage,
    int CommunicationPolicyVersion, string DisplayLocale, DateTimeOffset InvitationExpiresAt, GameResponse? Game, IReadOnlyList<RoomAudioResponse> Audio);
public sealed record GameResponse(string ExperienceId, int RulesVersion, int ContentVersion, int SchemaVersion,
    string ResourceVersion, string Status, DateTimeOffset Deadline, string Stage, string Title, string Objective,
    int CardCount, CardResponse? CurrentCard, AnswerResponse? MyAnswer, bool AwaitingPartner,
    IReadOnlyList<RevealedResponse> Revealed, IReadOnlyList<PassedResponse> Passed, IReadOnlyList<string> AvailableActions);
public sealed record CardResponse(string CardId, int Position, string Prompt, IReadOnlyList<OptionResponse> Options);
public sealed record OptionResponse(string OptionId, string Label);
public sealed record AnswerResponse(string Kind, OptionResponse? Option);
public sealed record RevealedResponse(string CardId, string Prompt, string MyOptionLabel, string PartnerOptionLabel, bool ChoseTheSame);
public sealed record PassedResponse(string CardId, string Prompt);
public sealed record PublishEventRequest(string Title, DateTimeOffset StartsAt, int Capacity, Guid ExperienceVersionId, string? TimeZone = null);
