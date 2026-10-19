using Duora.Web.Http;

namespace Duora.Web.Accounts;

/// <summary>Compatibilidade de descontinuação: nenhuma operação etária é executada.</summary>
internal static class AgeVerificationEndpoints
{
    public const string CurrentPath = "/api/v1/me/age-verifications/current";

    public static void MapAgeVerificationEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/age-verifications").WithTags("Me").ProducesProblem(401).ProducesProblem(403);
        group.MapPost("", () => ApiProblems.Problem(410, "AGE_VERIFICATION_RETIRED", "Age verification has been retired."));
        group.MapGet("/current", () => ApiProblems.Problem(410, "AGE_VERIFICATION_RETIRED", "Age verification has been retired."));
    }
}
