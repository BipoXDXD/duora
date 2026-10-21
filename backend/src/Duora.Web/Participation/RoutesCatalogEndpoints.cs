using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Contracts.Participation;
using Duora.Core.Experiences.UnlikelyRoutes;
using Duora.Infrastructure.Participation;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Duora.Web.Participation;

internal static class RoutesCatalogEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public static void MapRoutesCatalogEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/admin/route-experience-versions", PublishAsync)
            .Accepts<PublishRoutesCatalogRequest>("application/json")
            .Produces<CatalogPublishedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(413)
            .WithMetadata(new RequestSizeLimitAttribute(ParticipationService.MaxCatalogBodyBytes))
            .RequireAuthorization(SessionAuthorization.AdministrativeSession)
            .RequireProductAdministrator().RequireRecentMfa().HandleCatalogContention().ProducesProblem(503);
    }

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
            var request = JsonSerializer.Deserialize<PublishRoutesCatalogRequest>(body.GetBuffer().AsSpan(0, (int)body.Length), InputJson);
            if (request?.EnUS is null || request.PtBR is null || request.Audio is null
                || request.Audio.Any(cue => cue is null || cue.EnUS is null || cue.PtBR is null))
            {
                return Invalid();
            }
            var id = await service.PublishRoutesCatalogAsync(DuoraClaims.ReadUserId(context.User)!.Value,
                Content(request.EnUS, request.ContentVersion), Content(request.PtBR, request.ContentVersion),
                request.Audio.Select(cue => new CatalogAudioUpload(cue.CueId, Variant(cue.EnUS), Variant(cue.PtBR))).ToArray(),
                request.EditorialApproved, ct);
            return id is null ? Invalid() : Results.Created($"/api/v1/admin/route-experience-versions/{id}", new CatalogPublishedResponse(id.Value));
        }
        catch (JsonException) { return Invalid(); }
        catch (ArgumentException) { return Invalid(); }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Invalid() => ApiProblems.Problem(400, ApiProblems.ValidationFailed, "Invalid bilingual catalog or audio manifest.");
    private static CatalogAudioUploadVariant Variant(CatalogAudioVariantRequest input) =>
        new(input.AssetId, input.Transcript, input.AccessibleDescription, input.Provenance, input.Sha256, input.Bytes);
    private static UnlikelyRoutesContent Content(RoutesCatalogContentRequest input, int version)
    {
        ArgumentNullException.ThrowIfNull(input.Stops);
        ArgumentNullException.ThrowIfNull(input.ClueSets);
        return new(version, input.Title, input.Objective, input.Example, input.Stops.Select(stop =>
        {
            ArgumentNullException.ThrowIfNull(stop);
            ArgumentNullException.ThrowIfNull(stop.Options);
            return new RouteStop(stop.StepId, stop.Prompt, stop.Options.Select(option =>
            {
                ArgumentNullException.ThrowIfNull(option);
                return new RouteOption(option.OptionId, option.Label);
            }).ToArray());
        }).ToArray(), input.ClueSets.Select(set =>
        {
            ArgumentNullException.ThrowIfNull(set);
            return (IReadOnlyList<Clue>)set.Select(clue =>
            {
                ArgumentNullException.ThrowIfNull(clue);
                return new Clue(clue.Text);
            }).ToArray();
        }).ToArray());
    }
}
