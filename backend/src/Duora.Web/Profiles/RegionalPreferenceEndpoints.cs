using System.Globalization;
using System.Text.Json.Nodes;
using Duora.Contracts.Profiles;
using Duora.Core.Profiles;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Profiles;
using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Profiles;

internal static class RegionalPreferenceEndpoints
{
    private static readonly string[] RequestModes = ["countries", "unrestricted"];
    private static readonly string[] ResponseModes = ["pending", "countries", "unrestricted"];
    public static void MapRegionalPreferenceEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/me/regional-preferences", GetAsync).WithTags("Preferences")
            .Produces<OwnRegionalPreferencesResponse>().ProducesProblem(401).ProducesProblem(403)
            .AddOpenApiOperationTransformer(PreferenceEndpoints.DocumentConcurrency);
        api.MapPut("/me/regional-preferences", PutAsync).WithTags("Preferences")
            .Produces<OwnRegionalPreferencesResponse>().ProducesValidationProblem().ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(412).ProducesProblem(428)
            .AddOpenApiOperationTransformer(PreferenceEndpoints.DocumentConcurrency);
    }

    public static void ConfigureOpenApi(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var request = context.JsonTypeInfo.Type == typeof(UpdateRegionalPreferencesRequest);
            if ((request || context.JsonTypeInfo.Type == typeof(OwnRegionalPreferencesResponse)) && schema.Properties is { } properties)
            {
                if (properties["mode"] is OpenApiSchema mode)
                {
                    mode.Type = JsonSchemaType.String;
                    mode.Enum = [.. (request ? RequestModes : ResponseModes)
                        .Select(value => (JsonNode)JsonValue.Create(value))];
                    mode.Description = "Explicit regional choice. Pending means no declaration; unrestricted is never inferred.";
                }
                if (properties["countryCodes"] is OpenApiSchema countries)
                {
                    countries.Type = JsonSchemaType.Array;
                    countries.UniqueItems = true;
                    countries.MaxItems = ProfileLocationCatalog.Countries.Count;
                    countries.Description = "Enabled ISO country codes. Countries mode requires a nonempty list; unrestricted requires an empty list.";
                    if (countries.Items is OpenApiSchema item)
                    {
                        item.Enum = [.. ProfileLocationCatalog.Countries.Select(value => (JsonNode)JsonValue.Create(value))];
                    }
                }
                schema.Required ??= new HashSet<string>();
                schema.Required.Add("mode");
                schema.Required.Add("countryCodes");
            }
            return Task.CompletedTask;
        });

    private static async Task<IResult> GetAsync(HttpContext context, RegionalPreferenceStore store, CancellationToken ct)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        return Respond(context, await store.GetOwnAsync(userId, ct));
    }

    private static async Task<IResult> PutAsync(UpdateRegionalPreferencesRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, HttpContext context, RegionalPreferenceStore store, CancellationToken ct)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var condition = EntityTags.ParseIfMatch(ifMatch);
        if (condition.Kind == IfMatchKind.Missing)
        {
            return ApiProblems.Problem(428, ApiProblems.PreconditionRequired);
        }
        if (!RegionalPreference.TryCreate(request.Mode, request.CountryCodes, out var preference))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["regionalPreferences"] = ["EXPLICIT_REGIONAL_PREFERENCE_REQUIRED"] });
        }
        try
        {
            var saved = condition.Kind == IfMatchKind.Version
                ? await store.UpdateOwnAsync(userId, condition.Version, preference!, ct) : null;
            return saved is null ? ApiProblems.Problem(412, ApiProblems.PreconditionFailed) : Respond(context, saved);
        }
        catch (LanguagePreferenceRestrictedException)
        {
            return ApiProblems.Problem(403, ApiProblems.AccountRestricted);
        }
    }

    private static Ok<OwnRegionalPreferencesResponse> Respond(HttpContext context, OwnRegionalPreferences saved)
    {
        context.Response.Headers.ETag = EntityTags.Format(saved.Version);
        var mode = saved.Preference.Mode switch
        {
            RegionalPreferenceMode.Pending => "pending",
            RegionalPreferenceMode.Countries => "countries",
            RegionalPreferenceMode.Unrestricted => "unrestricted",
            _ => throw new InvalidOperationException("Unsupported regional preference mode."),
        };
        return TypedResults.Ok(new OwnRegionalPreferencesResponse(mode, saved.Preference.CountryCodes.ToArray(),
            saved.Version.ToString(CultureInfo.InvariantCulture)));
    }
}
