using System.Globalization;
using Duora.Contracts.Profiles;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Duora.Web.Profiles;

internal static class TimeZoneEndpoints
{
    public static void MapTimeZoneEndpoints(this IEndpointRouteBuilder api)
    {
        var resource = api.MapGroup("/me/time-zone").WithTags("Preferences");
        resource.MapGet("", GetAsync).Produces<OwnTimeZoneResponse>().ProducesProblem(401).ProducesProblem(403)
            .AddOpenApiOperationTransformer(PreferenceEndpoints.DocumentConcurrency);
        resource.MapPut("", PutAsync).Produces<OwnTimeZoneResponse>().ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(412).ProducesProblem(428).ProducesProblem(503)
            .AddOpenApiOperationTransformer(PreferenceEndpoints.DocumentConcurrency);
        resource.MapGet("/options", () => new TimeZoneOptionsResponse(TimeZones.Identifiers))
            .Produces<TimeZoneOptionsResponse>().ProducesProblem(401).ProducesProblem(403);
    }

    private static async Task<IResult> GetAsync(HttpContext context, TimeZoneStore store, CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId || await store.GetOwnAsync(userId, cancellationToken) is not { } value)
        {
            return TypedResults.Unauthorized();
        }
        return Respond(context, value);
    }

    private static async Task<IResult> PutAsync(UpdateTimeZoneRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext context, TimeZoneStore store, CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var condition = EntityTags.ParseIfMatch(ifMatch);
        if (condition.Kind == IfMatchKind.Missing)
        {
            return ApiProblems.Problem(428, ApiProblems.PreconditionRequired, "Envie o ETag da última leitura.");
        }
        if (request.TimeZone is { } zone && !TimeZones.IsSupported(zone))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["timeZone"] = ["UNSUPPORTED_TIME_ZONE"] });
        }
        try
        {
            var saved = condition.Kind == IfMatchKind.Version
                ? await store.UpdateOwnAsync(userId, condition.Version, request.TimeZone, cancellationToken) : null;
            return saved is null
                ? ApiProblems.Problem(412, ApiProblems.PreconditionFailed, "O recurso mudou. Recarregue antes de salvar.")
                : Respond(context, saved);
        }
        catch (TimeZonePreferenceRestrictedException)
        {
            return ApiProblems.Problem(403, ApiProblems.AccountRestricted, "A situação da conta não permite esta alteração.");
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<OwnTimeZoneResponse> Respond(HttpContext context, OwnTimeZone value)
    {
        context.Response.Headers.ETag = EntityTags.Format(value.Version);
        return TypedResults.Ok(new OwnTimeZoneResponse(value.TimeZone, value.Version.ToString(CultureInfo.InvariantCulture)));
    }
}
