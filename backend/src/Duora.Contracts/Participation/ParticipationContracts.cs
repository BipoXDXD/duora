using System.Text.Json.Serialization;

namespace Duora.Contracts.Participation;

public sealed record EventResponse(Guid EventId, string Title, DateTimeOffset StartsAt, DateTimeOffset CheckInOpensAt, string State, string? TimeZone);
// RoundCancelled: a última rodada do encontro que saiu de Scheduled foi cancelada e a pessoa está com presença confirmada.
// Não diz quem nem por quê (spec 5.2); volta a false quando outra rodada do encontro começa.
public sealed record ParticipationResponse(string State, Guid? RoomId, bool RoundCancelled);
public sealed record RoomActionRequest(Guid ActionId, string ExpectedVersion, string Type, string CardId, string? OptionId);
public sealed record RoomResponse(Guid RoomId, string State, string Version, string CommunicationLanguage,
    int CommunicationPolicyVersion, string DisplayLocale, DateTimeOffset InvitationExpiresAt, GameResponse? Game, IReadOnlyList<RoomAudioResponse> Audio, RoutesGameResponse? RoutesGame = null);
public sealed record GameResponse(string ExperienceId, int RulesVersion, int ContentVersion, int SchemaVersion,
    string ResourceVersion, string Status, DateTimeOffset Deadline, string Stage, string Title, string Objective,
    int CardCount, CardResponse? CurrentCard, AnswerResponse? MyAnswer, bool AwaitingPartner,
    IReadOnlyList<RevealedResponse> Revealed, IReadOnlyList<PassedResponse> Passed, IReadOnlyList<string> AvailableActions);
public sealed record CardResponse(string CardId, int Position, string Prompt, IReadOnlyList<OptionResponse> Options);
public sealed record OptionResponse(string OptionId, string Label);
public sealed record AnswerResponse(string Kind, OptionResponse? Option);
public sealed record RevealedResponse(string CardId, string Prompt, string MyOptionLabel, string PartnerOptionLabel, bool ChoseTheSame);
public sealed record PassedResponse(string CardId, string Prompt);
public sealed record PublishEventRequest(string Title, [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset StartsAt, int Capacity, Guid ExperienceVersionId, string? TimeZone = null);
