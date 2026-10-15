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
                + "apaga a descrição. Sem If-Match: 428. Versão desatualizada: 412, sem gravar nada.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .AddOpenApiOperationTransformer(DocumentETag)
            .AddOpenApiOperationTransformer(RequireIfMatch);
    }

    /// <summary>Limites do DTO de edição no contrato, tirados das mesmas constantes do domínio.</summary>
    public static void ConfigureOpenApi(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(UpdateProfileRequest) && schema.Properties is { } properties)
            {
                SetLength(properties, "displayName", DisplayName.MaxLength);
                SetLength(properties, "bio", ProfileBio.MaxLength);
            }

            return Task.CompletedTask;
        });

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
            profile.DisplayName?.Value,
            profile.Bio?.Value,
            profile.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
