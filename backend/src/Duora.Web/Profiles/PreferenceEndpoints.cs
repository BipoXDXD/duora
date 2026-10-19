using System.Globalization;
using Duora.Contracts.Profiles;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Profiles;
using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Profiles;

internal static class PreferenceEndpoints
{
    public static void MapPreferenceEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/me/preferences", GetPreferencesAsync).WithTags("Preferences").Produces<OwnPreferencesResponse>().AddOpenApiOperationTransformer(DocumentConcurrency);
        api.MapPut("/me/preferences", PutPreferencesAsync).WithTags("Preferences").Produces<OwnPreferencesResponse>()
            .ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(412).ProducesProblem(428)
            .AddOpenApiOperationTransformer(DocumentConcurrency);
        api.MapGet("/me/locale", GetLocaleAsync).WithTags("Preferences").Produces<OwnLocaleResponse>().AddOpenApiOperationTransformer(DocumentConcurrency);
        api.MapPut("/me/locale", PutLocaleAsync).WithTags("Preferences").Produces<OwnLocaleResponse>()
            .ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(412).ProducesProblem(428)
            .AddOpenApiOperationTransformer(DocumentConcurrency);
    }

    private static Task DocumentConcurrency(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Responses?.TryGetValue("200", out var ok) == true && ok is OpenApiResponse response)
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["ETag"] = new OpenApiHeader
            {
                Required = true,
                Description = "Strong version tag; send as If-Match when updating this resource.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is OpenApiParameter { Name: "If-Match" } condition)
            {
                condition.Required = true;
            }
        }
        return Task.CompletedTask;
    }

    private static async Task<IResult> GetPreferencesAsync(HttpContext context, PreferenceStore store, CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        return Respond(context, await store.GetOwnAsync(userId, cancellationToken));
    }

    private static async Task<IResult> GetLocaleAsync(HttpContext context, LocaleStore store, CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var locale = await store.GetOwnAsync(userId, cancellationToken);
        return locale is null ? TypedResults.Unauthorized() : Respond(context, locale);
    }

    private static async Task<IResult> PutPreferencesAsync(UpdatePreferencesRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext context, PreferenceStore store, CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var condition = EntityTags.ParseIfMatch(ifMatch);
        if (condition.Kind == IfMatchKind.Missing)
        {
            return Missing();
        }
        if (request.CommunicationLanguages is not { Length: >= 1 and <= 2 } languages
            || languages.Any(x => x is not ("en" or "pt"))
            || languages.Distinct(StringComparer.Ordinal).Count() != languages.Length
            || request.AllowDifferentDisplayLocales is not { } allow)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["preferences"] = ["EXPLICIT_DECLARATION_REQUIRED"] });
        }
        try
        {
            var saved = condition.Kind == IfMatchKind.Version
                ? await store.UpdateOwnAsync(userId, condition.Version, languages, allow, cancellationToken) : null;
            return saved is null ? Stale() : Respond(context, saved);
        }
        catch (LanguagePreferenceRestrictedException)
        {
            return Restricted();
        }
    }

    private static async Task<IResult> PutLocaleAsync(UpdateLocaleRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext context, LocaleStore store, CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var condition = EntityTags.ParseIfMatch(ifMatch);
        if (condition.Kind == IfMatchKind.Missing)
        {
            return Missing();
        }
        if (request.Locale is not ("en-US" or "pt-BR"))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["locale"] = ["UNSUPPORTED_LOCALE"] });
        }
        try
        {
            var saved = condition.Kind == IfMatchKind.Version
                ? await store.UpdateOwnAsync(userId, condition.Version, request.Locale, cancellationToken) : null;
            return saved is null ? Stale() : Respond(context, saved);
        }
        catch (LanguagePreferenceRestrictedException)
        {
            return Restricted();
        }
    }

    private static ProblemHttpResult Missing() => ApiProblems.Problem(428, ApiProblems.PreconditionRequired, "Envie o ETag da última leitura.");
    private static ProblemHttpResult Stale() => ApiProblems.Problem(412, ApiProblems.PreconditionFailed, "O recurso mudou. Recarregue antes de salvar.");
    private static ProblemHttpResult Restricted() => ApiProblems.Problem(403, ApiProblems.AccountRestricted, "A situação da conta não permite esta alteração.");

    private static Ok<OwnPreferencesResponse> Respond(HttpContext context, OwnPreferences preferences)
    {
        context.Response.Headers.ETag = EntityTags.Format(preferences.Version);
        return TypedResults.Ok(new OwnPreferencesResponse(preferences.CommunicationLanguages, preferences.AllowDifferentDisplayLocales,
            preferences.DeclarationRequired, preferences.Version.ToString(CultureInfo.InvariantCulture)));
    }

    private static Ok<OwnLocaleResponse> Respond(HttpContext context, OwnLocale locale)
    {
        context.Response.Headers.ETag = EntityTags.Format(locale.Version);
        return TypedResults.Ok(new OwnLocaleResponse(locale.Locale, locale.Version.ToString(CultureInfo.InvariantCulture)));
    }
}
