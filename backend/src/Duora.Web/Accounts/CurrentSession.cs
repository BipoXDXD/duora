using Duora.Web.Identity;
using Microsoft.AspNetCore.Antiforgery;

namespace Duora.Web.Accounts;

/// <summary>Sessão autenticada da requisição, para as rotas que exigem sessão.</summary>
internal static class CurrentSession
{
    /// <summary>Conta e sessão; falha alto se a rota foi alcançada sem sessão, o que a autorização impede.</summary>
    public static (Guid UserId, Guid SessionId) Require(HttpContext context) =>
        (DuoraClaims.ReadUserId(context.User), DuoraClaims.ReadSessionId(context.User)) is ({ } userId, { } sessionId)
            ? (userId, sessionId)
            : throw new InvalidOperationException("Rota com sessão alcançada sem sessão.");

    public static SessionState State(HttpContext context) =>
        DuoraClaims.ReadSession(context.User) ?? throw new InvalidOperationException("Sessão sem estado.");

    /// <summary>
    /// Troca a sessão corrente por uma nova (nova chave, novo cookie) depois de mudar MFA ou senha, que trocam o
    /// security stamp. Mantém a política e o limite absoluto da sessão anterior (renovar não estende a vida desde o
    /// login) e leva as provas indicadas.
    /// </summary>
    public static Task RenewAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        string securityStamp,
        DateTimeOffset? reauthenticatedAt,
        DateTimeOffset? mfaVerifiedAt,
        DateTimeOffset? totpVerifiedAt)
    {
        var (userId, _) = Require(context);
        var session = State(context);
        var grant = new SessionGrant(
            userId,
            securityStamp,
            session.Policy,
            reauthenticatedAt,
            mfaVerifiedAt,
            totpVerifiedAt,
            session.AbsoluteExpiresAt);
        return AuthEndpoints.StartSessionAsync(context, antiforgery, grant);
    }
}
