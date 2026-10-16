using Duora.Core.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;

namespace Duora.Web.Security;

/// <summary>
/// Step-up (auth.md, "Reautenticação e step-up"): a rota só roda se a sessão provou a credencial, ou o segundo
/// fator, dentro de <see cref="RecentAuthentication.Window"/>. Faltando a prova: 401
/// <c>REAUTHENTICATION_REQUIRED</c>, sem efeito. O instante vem do banco a cada requisição, nunca do cliente.
/// </summary>
internal static class StepUp
{
    /// <summary>Senha (e TOTP, se a conta tem MFA) provada há pouco: login ou API-A13.</summary>
    public static TBuilder RequireRecentAuthentication<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter((context, next) =>
            Guard(context, next, session => session.ReauthenticatedAt));

    /// <summary>Segundo fator provado há pouco: ações administrativas críticas (TOTP obrigatório).</summary>
    public static TBuilder RequireRecentMfa<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter((context, next) =>
            Guard(context, next, session => session.MfaVerifiedAt));

    private static ValueTask<object?> Guard(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        Func<SessionState, DateTimeOffset?> provedAt)
    {
        var http = context.HttpContext;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (DuoraClaims.ReadSession(http.User) is { } session && RecentAuthentication.IsRecent(provedAt(session), now))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(ApiProblems.Problem(
            StatusCodes.Status401Unauthorized,
            ApiProblems.ReauthenticationRequired,
            "Confirme a senha para continuar."));
    }
}
