using Duora.Core;
using Duora.Web.Http;
using Microsoft.AspNetCore.Antiforgery;

namespace Duora.Web.Security;

/// <summary>
/// Origens de onde um navegador pode mandar mutações: a origem pública da SPA e, só em Development, o Vite em
/// <c>https://localhost:5173</c> (environment.md).
/// </summary>
internal sealed class TrustedOrigins
{
    public const string ViteDevServer = "https://localhost:5173";

    private readonly HashSet<string> _origins;

    public TrustedOrigins(PublicOrigin publicOrigin, IHostEnvironment environment)
    {
        _origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { publicOrigin.ToString() };
        if (environment.IsDevelopment())
        {
            _origins.Add(ViteDevServer);
        }
    }

    public bool Contains(string origin) => _origins.Contains(origin);
}

/// <summary>
/// CSRF em toda mutação (spec 11.3), inclusive login e logout: token antiforgery no header <c>X-XSRF-TOKEN</c> e,
/// como defesa adicional, <c>Origin</c> e Fetch Metadata. Qualquer falha responde 403
/// <c>CSRF_VALIDATION_FAILED</c>, sem dizer qual verificação falhou. Roda depois da autorização: rota protegida
/// sem sessão recebe 401 antes, e o token é conferido contra a identidade já autenticada.
/// </summary>
internal sealed class CsrfProtectionMiddleware(RequestDelegate next, IAntiforgery antiforgery, TrustedOrigins origins)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsSafeMethod(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!HasTrustedProvenance(context.Request) || !await antiforgery.IsRequestValidAsync(context))
        {
            await ApiProblems
                .Problem(StatusCodes.Status403Forbidden, ApiProblems.CsrfValidationFailed, "Requisição recusada.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    /// <summary>
    /// Navegadores atuais mandam <c>Origin</c> em todo POST e <c>Sec-Fetch-Site</c> em toda requisição; quando vêm,
    /// têm de apontar para a mesma origem. Sem nenhum dos dois, a decisão fica com o token.
    /// </summary>
    private bool HasTrustedProvenance(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        if (origin.Count > 0 && (origin.Count > 1 || !origins.Contains(origin.ToString())))
        {
            return false;
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        return fetchSite.Count == 0 || fetchSite.ToString() == "same-origin";
    }
}

/// <summary>Emite o par antiforgery: cookie HttpOnly do framework e o token legível <c>XSRF-TOKEN</c> para o header.</summary>
internal static class AntiforgeryTokens
{
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string ReadableCookieName = "XSRF-TOKEN";
    public const string FrameworkCookieName = "__Host-duora-af";

    /// <summary>
    /// O token está ligado à identidade do <see cref="HttpContext.User"/>; depois de login ou logout, chame de novo
    /// com o usuário novo para a SPA não precisar de outra ida a <c>/auth/csrf</c>.
    /// </summary>
    public static void Issue(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(
            ReadableCookieName,
            tokens.RequestToken ?? throw new InvalidOperationException("Antiforgery não gerou token de requisição."),
            new CookieOptions
            {
                HttpOnly = false,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });
    }

    public static void Configure(AntiforgeryOptions options)
    {
        options.HeaderName = HeaderName;
        options.FormFieldName = "__RequestVerificationToken";
        options.Cookie.Name = FrameworkCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
    }
}
