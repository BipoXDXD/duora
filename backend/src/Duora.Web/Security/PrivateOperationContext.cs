using Duora.Contracts.Identity;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Antiforgery;

namespace Duora.Web.Security;

internal sealed class PrivateOperationEndpoint;
internal sealed class PublicAntiforgeryEndpoint;
internal sealed class SessionContextBootstrapEndpoint;
internal sealed class SessionAwareLogoutEndpoint;

/// <summary>
/// Reutiliza a prova protegida do framework, acrescentando finalidade e a sessão existente. Identificadores
/// continuam apenas no corpo privado do bootstrap; não são autorizadores fornecidos pelo cliente.
/// </summary>
internal static class PrivateOperationContext
{
    private static readonly object PrivateIssuance = new();

    // Routing errors (404/405/415) can select an endpoint without the group's metadata.
    private static readonly HashSet<string> PublicErrorRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/v1/auth/csrf",
        "GET /api/v1/legal-documents",
        "GET /api/v1/me",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/confirm-email",
        "POST /api/v1/auth/email-confirmation-requests",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/password-reset-requests",
        "POST /api/v1/auth/password-resets",
    };


    public static bool RequiresPrivateProof(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        var route = $"{context.Request.Method} {context.Request.Path.Value?.TrimEnd('/')}";
        if (metadata?.GetMetadata<SessionAwareLogoutEndpoint>() is not null
            || string.Equals(route, "POST /api/v1/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            return context.User.Identity?.IsAuthenticated == true;
        }
        if (metadata?.GetMetadata<PublicAntiforgeryEndpoint>() is not null
            || (HttpMethods.IsGet(context.Request.Method)
                && metadata?.GetMetadata<SessionContextBootstrapEndpoint>() is not null))
        {
            return false;
        }
        return metadata?.GetMetadata<PrivateOperationEndpoint>() is not null
            || (context.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase)
                && !PublicErrorRoutes.Contains(route));
    }

    public static PrivateSessionContextResponse Issue(HttpContext context, IAntiforgery antiforgery)
    {
        var userId = DuoraClaims.ReadUserId(context.User)
            ?? throw new InvalidOperationException("Bootstrap privado sem titular autenticado.");
        var sessionId = DuoraClaims.ReadSessionId(context.User)
            ?? throw new InvalidOperationException("Bootstrap privado sem sessão autenticada.");
        var session = DuoraClaims.ReadSession(context.User)
            ?? throw new InvalidOperationException("Bootstrap privado sem estado de sessão.");
        context.Items.Add(PrivateIssuance, true);
        try
        {
            var token = antiforgery.GetAndStoreTokens(context).RequestToken
                ?? throw new InvalidOperationException("Antiforgery não gerou prova privada.");
            var phase = session.AwaitsPasswordChange ? "password-change"
                : session.AwaitsMfaEnrollment ? "mfa-enrollment" : "active";
            return new PrivateSessionContextResponse(userId, sessionId, phase, token);
        }
        finally
        {
            context.Items.Remove(PrivateIssuance);
        }
    }

    public static bool IsPrivateIssuance(HttpContext context) => context.Items.ContainsKey(PrivateIssuance);
}

internal sealed class SessionAntiforgeryAdditionalDataProvider : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) =>
        Data(context, PrivateOperationContext.IsPrivateIssuance(context))
        ?? throw new InvalidOperationException("Antiforgery autenticado sem sessão válida.");

    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        Data(context, PrivateOperationContext.RequiresPrivateProof(context)) is { } expected
        && string.Equals(expected, additionalData, StringComparison.Ordinal);

    private static string? Data(HttpContext context, bool isPrivate)
    {
        var purpose = isPrivate ? "private" : "public";
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return DuoraClaims.ReadSessionId(context.User) is { } sessionId
                ? $"duora:operation:v1:{purpose}:{sessionId:D}" : null;
        }
        return isPrivate ? null : "duora:operation:v1:public:anonymous";
    }
}
