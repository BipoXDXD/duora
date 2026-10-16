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

/// <summary>
/// Perfil do titular (API-P01 e API-P02). O alvo é sempre a conta da sessão: nenhuma rota recebe id de perfil, e a
/// projeção para outras pessoas (API-P04) é outra operação, ainda não implementada.
/// </summary>
internal static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder api)
    {
        var profile = api.MapGroup("/me/profile").WithTags("Profile");

        profile.MapGet("", GetOwnProfileAsync)
            .WithDescription("Perfil editável do titular. O header ETag traz a mesma versão do campo version.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AddOpenApiOperationTransformer(DocumentETag);
        profile.MapPatch("", UpdateOwnProfileAsync)
            .WithDescription(
                "Edição parcial com If-Match obrigatório (o ETag da leitura). Campo ausente fica como está; bio null "
                + "apaga a descrição; regionCode null volta a região para não informada. Sem If-Match: 428. Versão "
                + "desatualizada: 412, sem gravar nada.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .AddOpenApiOperationTransformer(DocumentETag)
            .AddOpenApiOperationTransformer(RequireIfMatch);
    }

    /// <summary>
    /// Limites do DTO de edição e as 27 UFs da região no contrato, tirados das mesmas constantes do domínio. A região
    /// é enum nos dois sentidos, com <c>null</c> entre os valores (não informada; no PATCH, limpar).
    /// </summary>
    public static void ConfigureOpenApi(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var type = context.JsonTypeInfo.Type;
            if (type == typeof(UpdateProfileRequest) && schema.Properties is { } request)
            {
                SetLength(request, "displayName", DisplayName.MaxLength);
                SetLength(request, "bio", ProfileBio.MaxLength);
                SetRegionCodes(request);
            }
            else if (type == typeof(OwnProfileResponse) && schema.Properties is { } response)
            {
                SetRegionCodes(response);
            }

            return Task.CompletedTask;
        });

    private static void SetRegionCodes(IDictionary<string, IOpenApiSchema> properties)
    {
        if (properties.TryGetValue("regionCode", out var property) && property is OpenApiSchema schema)
        {
            schema.Enum = [.. RegionCode.All.Select(code => (JsonNode)JsonValue.Create(code)), JsonNullSentinel.JsonNull];
            schema.Description = "UF em ISO 3166-2:BR, na forma canônica; null quando não informada.";
        }
    }

    private static void SetLength(IDictionary<string, IOpenApiSchema> properties, string name, int maxLength)
    {
        if (properties.TryGetValue(name, out var property) && property is OpenApiSchema schema)
        {
            schema.MinLength = 1;
            schema.MaxLength = maxLength;
        }
    }

    private static Task DocumentETag(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Responses?.TryGetValue("200", out var ok) == true && ok is OpenApiResponse response)
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Versão atual do perfil, para mandar no If-Match da próxima edição.",
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>Sem If-Match a API responde 428, não 400; no contrato o header é obrigatório.</summary>
    private static Task RequireIfMatch(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is OpenApiParameter { Name: "If-Match" } ifMatch)
            {
                ifMatch.Required = true;
                ifMatch.Description = "ETag da última leitura do perfil.";
            }
        }

        return Task.CompletedTask;
    }

    private static async Task<Results<Ok<OwnProfileResponse>, UnauthorizedHttpResult>> GetOwnProfileAsync(
        HttpContext context,
        ProfileStore profiles,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return Respond(context, await profiles.GetOwnAsync(userId, cancellationToken));
    }

    private static async Task<Results<Ok<OwnProfileResponse>, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>>
        UpdateOwnProfileAsync(
            UpdateProfileRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch,
            HttpContext context,
            AccountService accounts,
            ProfileStore profiles,
            CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId
            || await accounts.FindAccountAsync(userId, cancellationToken) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProfileEditPolicy.Allows(account.Status))
        {
            return ApiProblems.Problem(
                StatusCodes.Status403Forbidden,
                ApiProblems.AccountRestricted,
                "A situação da conta não permite alterar o perfil.");
        }

        var condition = EntityTags.ParseIfMatch(ifMatch);
        if (condition.Kind == IfMatchKind.Missing)
        {
            return ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiProblems.PreconditionRequired,
                "Envie o ETag da última leitura do perfil no header If-Match.");
        }

        if (ProfileEditParser.Parse(request, out var errors) is not { } edit)
        {
            return ApiProblems.Validation(errors);
        }

        var saved = condition.Kind == IfMatchKind.Version
            ? await profiles.UpdateOwnAsync(userId, condition.Version, edit, cancellationToken)
            : null;
        return saved is null
            ? ApiProblems.Problem(
                StatusCodes.Status412PreconditionFailed,
                ApiProblems.PreconditionFailed,
                "O perfil mudou desde a sua leitura. Recarregue e aplique a alteração de novo.")
            : Respond(context, saved);
    }

    private static Ok<OwnProfileResponse> Respond(HttpContext context, OwnProfile profile)
    {
        context.Response.Headers.ETag = EntityTags.Format(profile.Version);
        return TypedResults.Ok(new OwnProfileResponse(
            profile.DisplayName,
            profile.Bio,
            profile.RegionCode,
            profile.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
