using System.Globalization;
using System.Security.Claims;
using Duora.Core.Identity;

namespace Duora.Web.Identity;

/// <summary>
/// Conta e provas com que uma sessão nova nasce (login ou renovação depois de mudar MFA ou senha). Na renovação,
/// <paramref name="AbsoluteExpiresAt"/> é o limite absoluto da sessão anterior: renovar nunca estende a vida
/// contada desde o login (M2 da revisão do #38).
/// </summary>
public sealed record SessionGrant(
    Guid UserId,
    string SecurityStamp,
    SessionPolicy Policy,
    DateTimeOffset? ReauthenticatedAt,
    DateTimeOffset? MfaVerifiedAt,
    DateTimeOffset? TotpVerifiedAt,
    DateTimeOffset? AbsoluteExpiresAt = null);

/// <summary>
/// Estado da sessão corrente como o banco o tem agora (lido a cada requisição pelo ticket store, nunca pelo
/// cliente): política, últimas provas, se a conta tem papel privilegiado neste momento e se ela ainda usa a senha
/// temporária do bootstrap (D4).
/// </summary>
public sealed record SessionState(
    SessionPolicy Policy,
    DateTimeOffset? ReauthenticatedAt,
    DateTimeOffset? MfaVerifiedAt,
    DateTimeOffset? TotpVerifiedAt,
    DateTimeOffset? AbsoluteExpiresAt,
    bool AccountIsPrivileged,
    bool AwaitsPasswordChange)
{
    /// <summary>Conta privilegiada sem segundo fator nesta sessão: só pode cadastrar o MFA (ADR-0017).</summary>
    public bool AwaitsMfaEnrollment => AccountIsPrivileged && MfaVerifiedAt is null;

    /// <summary>Sessão que abre as rotas <c>/admin</c>: conta privilegiada, política administrativa e MFA provado.</summary>
    public bool IsAdministrative =>
        AccountIsPrivileged && Policy == SessionPolicy.Administrative && MfaVerifiedAt is not null;
}

/// <summary>Claims do principal de sessão. Ficam só no ticket do servidor; o cookie não as carrega.</summary>
public static class DuoraClaims
{
    public const string SessionId = "duora:sid";

    public const string SecurityStamp = "duora:stamp";

    public const string Policy = "duora:policy";

    public const string ReauthenticatedAt = "duora:reauthenticated_at";

    public const string MfaVerifiedAt = "duora:mfa_verified_at";

    public const string TotpVerifiedAt = "duora:totp_verified_at";

    public const string AbsoluteExpiresAt = "duora:absolute_expires_at";

    public const string AccountIsPrivileged = "duora:privileged";

    public const string AwaitsPasswordChange = "duora:awaits_password_change";

    /// <summary>Claims de estado: valem pelo que está no banco, então o ticket store as tira antes de gravar e as repõe ao ler.</summary>
    internal static IReadOnlyList<string> SessionStateTypes { get; } =
        [Policy, ReauthenticatedAt, MfaVerifiedAt, TotpVerifiedAt, AbsoluteExpiresAt, AccountIsPrivileged, AwaitsPasswordChange];

    public static Guid? ReadUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? ReadSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(SessionId), out var id) ? id : null;

    /// <summary>Estado da sessão autenticada; null sem sessão.</summary>
    public static SessionState? ReadSession(ClaimsPrincipal principal)
    {
        if (principal.FindFirstValue(Policy) is not { } policy || !Enum.TryParse<SessionPolicy>(policy, out var parsed))
        {
            return null;
        }

        return new SessionState(
            parsed,
            ReadInstant(principal, ReauthenticatedAt),
            ReadInstant(principal, MfaVerifiedAt),
            ReadInstant(principal, TotpVerifiedAt),
            ReadInstant(principal, AbsoluteExpiresAt),
            principal.FindFirstValue(AccountIsPrivileged) == bool.TrueString,
            principal.FindFirstValue(AwaitsPasswordChange) == bool.TrueString);
    }

    /// <summary>Principal de uma sessão nova; o id da sessão é acrescentado ao gravá-la.</summary>
    public static ClaimsPrincipal ForSignIn(SessionGrant grant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, grant.UserId.ToString("D")),
            new(SecurityStamp, grant.SecurityStamp),
            new(Policy, grant.Policy.ToString()),
        };
        AddInstant(claims, ReauthenticatedAt, grant.ReauthenticatedAt);
        AddInstant(claims, MfaVerifiedAt, grant.MfaVerifiedAt);
        AddInstant(claims, TotpVerifiedAt, grant.TotpVerifiedAt);
        AddInstant(claims, AbsoluteExpiresAt, grant.AbsoluteExpiresAt);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, DuoraAuthentication.Scheme));
    }

    internal static void AddInstant(ICollection<Claim> claims, string type, DateTimeOffset? instant)
    {
        if (instant is { } value)
        {
            claims.Add(new Claim(type, value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    internal static DateTimeOffset? ReadInstant(ClaimsPrincipal principal, string type) =>
        DateTimeOffset.TryParse(
            principal.FindFirstValue(type),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var instant)
            ? instant
            : null;
}
