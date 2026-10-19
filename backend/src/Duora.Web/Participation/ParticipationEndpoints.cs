using System.Globalization;
using Duora.Contracts.Participation;
using Duora.Infrastructure.Participation;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Npgsql;

namespace Duora.Web.Participation;

internal static class ParticipationEndpoints
{
    public static void MapParticipationEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapCatalogEndpoints();
        var routes = api.MapGroup("").WithTags("Participation").RequireAuthorization(ParticipationPolicies.EligibleParticipant)
            .ProducesProblem(401).ProducesProblem(403);
        routes.AddEndpointFilter(async (context, next) =>
        {
            try
            { return await next(context); }
            catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                ApiProblems.SetRetryAfter(context.HttpContext, TimeSpan.FromSeconds(2));
                return ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable, "Temporarily unavailable.");
            }
        });
        routes.MapGet("/events", async (ParticipationService service, CancellationToken ct) =>
            (await service.EventsAsync(ct)).Select(e => new EventResponse(e.Id, e.Title, e.StartsAt, e.StartsAt.AddMinutes(-10), e.State)).ToArray());
        routes.MapPut("/events/{eventId:guid}/registration", async (Guid eventId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            Result(await service.RegisterAsync(eventId, User(context), false, ct)));
        routes.MapPost("/events/{eventId:guid}/check-in", async (Guid eventId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            Result(await service.RegisterAsync(eventId, User(context), true, ct)));
        routes.MapGet("/events/{eventId:guid}/participation", async (Guid eventId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            await service.StatusAsync(eventId, User(context), ct) is { } s ? Results.Ok(new ParticipationResponse(s.State, s.RoomId)) : Results.NotFound()).Produces<ParticipationResponse>();
        routes.MapGet("/rooms/{roomId:guid}", async (Guid roomId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            await service.RoomAsync(roomId, User(context), ct) is { } room ? Results.Ok(Map(room)) : Results.NotFound()).Produces<RoomResponse>();
        routes.MapPost("/rooms/{roomId:guid}/accept", async (Guid roomId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            Result(await service.RespondAsync(roomId, User(context), true, ct)));
        routes.MapPost("/rooms/{roomId:guid}/reject", async (Guid roomId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            Result(await service.RespondAsync(roomId, User(context), false, ct)));
        routes.MapPost("/rooms/{roomId:guid}/exit", async (Guid roomId, HttpContext context, ParticipationService service, CancellationToken ct) =>
            Result(await service.ExitAsync(roomId, User(context), ct)));
        routes.MapPost("/rooms/{roomId:guid}/actions", async (Guid roomId, RoomActionRequest request, HttpContext context, ParticipationService service, CancellationToken ct) =>
            long.TryParse(request.ExpectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? Result(await service.ActionAsync(roomId, User(context), request.ActionId, version, request.Type, request.CardId, request.OptionId, ct))
                : Result(ParticipationOutcome.Invalid));
        api.MapPost("/admin/events", async (PublishEventRequest request, HttpContext context, ParticipationService service, CancellationToken ct) =>
            await service.PublishAsync(request.Title, request.StartsAt, request.Capacity, request.ExperienceVersionId, User(context), ct) is { } id
                ? Results.Created($"/api/v1/events/{id}", new { eventId = id }) : Result(ParticipationOutcome.Invalid))
            .RequireAuthorization(SessionAuthorization.AdministrativeSession)
            .RequireProductAdministrator().RequireRecentMfa().ProducesProblem(401).ProducesProblem(403);
    }

    private static Guid User(HttpContext context) => DuoraClaims.ReadUserId(context.User)!.Value;
    private static IResult Result(ParticipationOutcome outcome) => outcome switch
    {
        ParticipationOutcome.Done => Results.NoContent(),
        ParticipationOutcome.Conflict => ApiProblems.Problem(409, "VERSION_CONFLICT", "Reload the current room."),
        ParticipationOutcome.Invalid => ApiProblems.Problem(400, "VALIDATION_FAILED", "Invalid request."),
        _ => Results.NotFound(),
    };

    private static RoomResponse Map(ParticipationRoom room)
    {
        var g = room.Game;
        var game = g is null ? null : new GameResponse(g.ExperienceId, g.RulesVersion, g.ContentVersion, g.SchemaVersion,
            g.ResourceVersion.ToString(CultureInfo.InvariantCulture), g.Status.ToString(), g.Deadline, g.Stage.ToString(), g.Title, g.Objective,
            g.CardCount, g.CurrentCard is not { } card ? null : new CardResponse(card.CardId, card.Position, card.Prompt,
                card.Options.Select(o => new OptionResponse(o.OptionId, o.Label)).ToArray()),
            g.MyAnswer is not { } answer ? null : new AnswerResponse(answer.Kind.ToString(), answer.Option is not { } o ? null : new(o.OptionId, o.Label)),
            g.AwaitingPartner, g.Revealed.Select(r => new RevealedResponse(r.CardId, r.Prompt, r.MyOptionLabel, r.PartnerOptionLabel, r.ChoseTheSame)).ToArray(),
            g.Passed.Select(p => new PassedResponse(p.CardId, p.Prompt)).ToArray(), g.AvailableActions);
        return new(room.Id, room.State, room.Version.ToString(CultureInfo.InvariantCulture), room.Language, room.PolicyVersion, room.Locale, room.InviteExpiresAt, game,
            room.Audio.Select(cue => new RoomAudioResponse(cue.CueId, cue.AssetId, cue.Transcript, cue.AccessibleDescription, cue.Sha256,
                $"/api/v1/rooms/{room.Id}/audio/{cue.AssetId}")).ToArray());
    }
}
