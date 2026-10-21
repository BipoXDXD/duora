using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Contracts.Participation;
using Duora.Core.Experiences.Constellation;
using Duora.Infrastructure.Participation;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Duora.Web.Participation;

internal static class CatalogEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/admin/experience-versions", PublishAsync)
            .Accepts<PublishCatalogRequest>("application/json")
            .Produces<CatalogPublishedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(413)
            .WithMetadata(new RequestSizeLimitAttribute(ParticipationService.MaxCatalogBodyBytes))
            .RequireAuthorization(SessionAuthorization.AdministrativeSession)
            .RequireProductAdministrator().RequireRecentMfa().HandleCatalogContention().ProducesProblem(503);
        api.MapGet("/rooms/{roomId:guid}/audio/{assetId}", async (Guid roomId, string assetId, HttpContext context,
            ParticipationService service, CancellationToken ct) =>
        {
            var audio = await service.AudioAsync(roomId, DuoraClaims.ReadUserId(context.User)!.Value, assetId, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return audio is null ? Results.NotFound() : Results.Bytes(audio.Bytes, audio.ContentType);
        }).RequireAuthorization(ParticipationPolicies.EligibleParticipant)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .HandleCatalogContention().ProducesProblem(503);
    }

    internal static TBuilder HandleCatalogContention<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.LockNotAvailable
                or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                ApiProblems.SetRetryAfter(context.HttpContext, TimeSpan.FromSeconds(2));
                return ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable, "Temporarily unavailable.");
            }
        });

    public static TBuilder RequireProductAdministrator<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var user = DuoraClaims.ReadUserId(http.User);
            var service = http.RequestServices.GetRequiredService<ParticipationService>();
            return user is not null && await service.IsProductAdministratorAsync(user.Value, http.RequestAborted)
                ? await next(context)
                : ApiProblems.Problem(403, "FORBIDDEN", "Forbidden.");
        });

    private static async Task<IResult> PublishAsync(HttpContext context, ParticipationService service, CancellationToken ct)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = ParticipationService.MaxCatalogBodyBytes;
        }
        if (!context.Request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
        if (context.Request.ContentLength > ParticipationService.MaxCatalogBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > ParticipationService.MaxCatalogBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            body.Write(buffer, 0, read);
        }
        try
        {
            var request = JsonSerializer.Deserialize<PublishCatalogRequest>(body.GetBuffer().AsSpan(0, (int)body.Length), InputJson);
            if (request?.EnUS is null || request.PtBR is null || request.Audio is null
                || request.Audio.Any(cue => cue is null || cue.EnUS is null || cue.PtBR is null))
            {
                return Invalid();
            }
            var id = await service.PublishCatalogAsync(DuoraClaims.ReadUserId(context.User)!.Value,
                Content(request.EnUS, request.ContentVersion), Content(request.PtBR, request.ContentVersion),
                request.Audio.Select(cue => new CatalogAudioUpload(cue.CueId, Variant(cue.EnUS), Variant(cue.PtBR))).ToArray(),
                request.EditorialApproved, ct);
            return id is null ? Invalid() : Results.Created($"/api/v1/admin/experience-versions/{id}", new CatalogPublishedResponse(id.Value));
        }
        catch (JsonException) { return Invalid(); }
        catch (ArgumentException) { return Invalid(); }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Invalid() => ApiProblems.Problem(400, ApiProblems.ValidationFailed, "Invalid bilingual catalog or audio manifest.");
    private static CatalogAudioUploadVariant Variant(CatalogAudioVariantRequest input) =>
        new(input.AssetId, input.Transcript, input.AccessibleDescription, input.Provenance, input.Sha256, input.Bytes);
    private static ConstellationContent Content(CatalogContentRequest input, int version)
    {
        ArgumentNullException.ThrowIfNull(input.Cards);
        return new(version, input.Title, input.Objective, input.Cards.Select(card =>
        {
            ArgumentNullException.ThrowIfNull(card);
            ArgumentNullException.ThrowIfNull(card.Options);
            return new ConstellationCard(card.CardId, card.Prompt, card.Options.Select(option =>
            {
                ArgumentNullException.ThrowIfNull(option);
                return new ConstellationOption(option.OptionId, option.Label);
            }).ToArray());
        }).ToArray());
    }
}
