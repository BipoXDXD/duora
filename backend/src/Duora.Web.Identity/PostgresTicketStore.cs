using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Duora.Web.Identity;

/// <summary>
/// <see cref="ITicketStore"/> sobre o PostgreSQL (ADR-0005): o cookie leva só uma chave aleatória de 256 bits,
/// e o ticket fica no banco, protegido com Data Protection. Cada requisição consulta a sessão, então revogação,
/// troca de senha (security stamp) e expiração valem já na requisição seguinte.
/// </summary>
internal sealed partial class PostgresTicketStore(
    LoginSessions sessions,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<PostgresTicketStore> logger) : ITicketStore
{
    private const int KeyBytes = 32;
    private const string LogoutReason = "logout";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Duora.Identity.LoginSessionTicket.v1");

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        StoreAsync(ticket, httpContext: null, cancellationToken);

    public async Task<string> StoreAsync(
        AuthenticationTicket ticket,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var identity = ticket.Principal.Identities.Single(identity => identity.IsAuthenticated);
        var userId = DuoraClaims.ReadUserId(ticket.Principal)
            ?? throw new InvalidOperationException("Ticket sem identificador de usuário.");
        var securityStamp = identity.FindFirst(DuoraClaims.SecurityStamp)?.Value
            ?? throw new InvalidOperationException("Ticket sem security stamp.");

        var principal = ticket.Principal;
        var policy = DuoraClaims.ReadSession(principal)?.Policy
            ?? throw new InvalidOperationException("Ticket sem política de sessão.");
        var reauthenticatedAt = DuoraClaims.ReadInstant(principal, DuoraClaims.ReauthenticatedAt);
        var mfaVerifiedAt = DuoraClaims.ReadInstant(principal, DuoraClaims.MfaVerifiedAt);
        var totpVerifiedAt = DuoraClaims.ReadInstant(principal, DuoraClaims.TotpVerifiedAt);
        var inheritedLimit = DuoraClaims.ReadInstant(principal, DuoraClaims.AbsoluteExpiresAt);
        RemoveSessionState(identity);

        var now = time.GetUtcNow();
        var window = SessionLifetime.For(policy).Open(now);
        if (inheritedLimit is { } limit && limit < window.AbsoluteExpiresAt)
        {
            // Renovação: a sessão nova herda o fim da anterior, nunca o estende.
            window = new SessionWindow(window.IdleExpiresAt < limit ? window.IdleExpiresAt : limit, limit);
        }
        var sessionId = Guid.CreateVersion7(now);
        identity.AddClaim(new Claim(DuoraClaims.SessionId, sessionId.ToString("D")));
        ticket.Properties.IssuedUtc = now;
        ticket.Properties.ExpiresUtc = window.AbsoluteExpiresAt;

        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyBytes));
        var description = SessionDescription.FromUserAgent(httpContext?.Request.Headers.UserAgent.ToString());
        await sessions.CreateAsync(
            new NewLoginSession(
                sessionId,
                userId,
                key,
                Protect(ticket),
                securityStamp,
                description,
                now,
                window,
                policy,
                reauthenticatedAt,
                mfaVerifiedAt,
                totpVerifiedAt),
            cancellationToken);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        RemoveSessionState(ticket.Principal.Identities.Single(identity => identity.IsAuthenticated));
        return sessions.ReplaceTicketAsync(key, Protect(ticket), cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        if (await sessions.TouchAsync(key, cancellationToken) is not { } session
            || Unprotect(session.ProtectedTicket) is not { } ticket)
        {
            return null;
        }

        var identity = ticket.Principal.Identities.Single(identity => identity.IsAuthenticated);
        RemoveSessionState(identity);
        var state = new List<Claim>
        {
            new(DuoraClaims.Policy, session.Policy.ToString()),
            new(DuoraClaims.AccountIsPrivileged, session.AccountIsPrivileged.ToString()),
            new(DuoraClaims.AwaitsPasswordChange, session.AwaitsPasswordChange.ToString()),
        };
        DuoraClaims.AddInstant(state, DuoraClaims.ReauthenticatedAt, session.ReauthenticatedAt);
        DuoraClaims.AddInstant(state, DuoraClaims.MfaVerifiedAt, session.MfaVerifiedAt);
        DuoraClaims.AddInstant(state, DuoraClaims.TotpVerifiedAt, session.TotpVerifiedAt);
        DuoraClaims.AddInstant(state, DuoraClaims.AbsoluteExpiresAt, session.AbsoluteExpiresAt);
        identity.AddClaims(state);
        return ticket;
    }

    /// <summary>O estado da sessão vale pelo banco; uma cópia antiga dentro do ticket nunca é lida.</summary>
    private static void RemoveSessionState(ClaimsIdentity identity)
    {
        foreach (var claim in identity.Claims.Where(claim => DuoraClaims.SessionStateTypes.Contains(claim.Type)).ToList())
        {
            identity.RemoveClaim(claim);
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public Task RemoveAsync(string key, CancellationToken cancellationToken) =>
        sessions.RevokeByKeyAsync(key, LogoutReason, cancellationToken);

    private byte[] Protect(AuthenticationTicket ticket) => _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private AuthenticationTicket? Unprotect(byte[] protectedTicket)
    {
        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(protectedTicket));
        }
        catch (CryptographicException exception)
        {
            // Chave do key ring perdida ou trocada (spec 11.2): a sessão deixa de valer e a pessoa entra de novo.
            LogUnreadableTicket(logger, exception);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ticket de sessão ilegível com o key ring atual; sessão tratada como inválida")]
    private static partial void LogUnreadableTicket(ILogger logger, Exception exception);
}
