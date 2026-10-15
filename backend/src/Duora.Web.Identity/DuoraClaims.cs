using System.Security.Claims;

namespace Duora.Web.Identity;

/// <summary>Claims do principal de sessão. Ficam só no ticket do servidor; o cookie não as carrega.</summary>
public static class DuoraClaims
{
    public const string SessionId = "duora:sid";

    public const string SecurityStamp = "duora:stamp";

    public static Guid? ReadUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? ReadSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(SessionId), out var id) ? id : null;

    /// <summary>Principal de uma conta que acabou de provar a senha; o id da sessão é acrescentado ao gravá-la.</summary>
    public static ClaimsPrincipal ForSignIn(VerifiedAccount account) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.UserId.ToString("D")),
                new Claim(SecurityStamp, account.SecurityStamp),
            ],
            DuoraAuthentication.Scheme));
}
