using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Duora.Core;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Duora.Web.Identity;

public sealed class GoogleOidcSettings
{
    public bool Enabled { get; set; }
    public bool RegistrationEnabled { get; set; }
    public bool LinkingEnabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>OIDC is only a staged credential source. It cannot issue the Duora application cookie.</summary>
public static class GoogleOidcAuthentication
{
    public const string Scheme = "Duora.GoogleOidc";
    public const string FlowCookie = "__Host-duora-google-flow";
    public const string FlowProperty = "duora:external-flow";
    public const string CallbackPath = "/signin-google-oidc";
    private const string StageScheme = "Duora.GoogleStage";

    public static IServiceCollection AddGoogleOidc(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GoogleOidcSettings>().Bind(configuration.GetSection("GoogleOidc"))
            .Validate(o => !o.Enabled || (!string.IsNullOrWhiteSpace(o.ClientId) && !string.IsNullOrWhiteSpace(o.ClientSecret)),
                "GoogleOidc enabled requires server-side client credentials.").ValidateOnStart();
        services.AddSingleton<IExternalIdentityProtection, ExternalIdentityProtection>();
        // The upstream handler may log raw protocol exception details. Aggregate closed outcomes below instead.
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.Critical));
        if (!configuration.GetValue<bool>("GoogleOidc:Enabled"))
        {
            return services;
        }

        services.AddAuthentication().AddCookie(StageScheme, o =>
        {
            o.Cookie.Name = "__Host-duora-google-stage";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = ExternalAuthenticationService.Lifetime;
        }).AddOpenIdConnect(Scheme, o =>
        {
            o.Authority = "https://accounts.google.com";
            o.MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";
            o.ClientId = configuration["GoogleOidc:ClientId"];
            o.ClientSecret = configuration["GoogleOidc:ClientSecret"];
            o.CallbackPath = CallbackPath;
            o.RemoteSignOutPath = PathString.Empty;
            o.SignedOutCallbackPath = PathString.Empty;
            o.SignInScheme = StageScheme;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.RequireHttpsMetadata = true;
            o.MapInboundClaims = false;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.UseTokenLifetime = false;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("email");
            o.BackchannelTimeout = TimeSpan.FromSeconds(10);
            o.RemoteAuthenticationTimeout = ExternalAuthenticationService.Lifetime;
            o.ProtocolValidator.RequireNonce = true;
            o.CorrelationCookie.Name = "__Host-duora-google-correlation.";
            o.CorrelationCookie.Path = "/";
            o.NonceCookie.Name = "__Host-duora-google-nonce.";
            o.NonceCookie.Path = "/";
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                ValidateAudience = true,
                ValidAudience = o.ClientId,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(60),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                NameClaimType = "sub",
                RoleClaimType = "duora:unmapped-external-role",
            };
            o.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.Prompt = "select_account";
                    context.ProtocolMessage.RedirectUri = context.HttpContext.RequestServices.GetRequiredService<PublicOrigin>().Resolve(CallbackPath).AbsoluteUri;
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var claims = context.Principal!;
                    var sub = Single(claims, "sub");
                    var email = Single(claims, "email");
                    var verified = claims.FindAll("email_verified").ToArray();
                    var azp = claims.FindAll("azp").ToArray();
                    var audienceCount = claims.FindAll("aud").Count();
                    var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                    var iat = Single(claims, "iat");
                    if (string.IsNullOrEmpty(sub) || sub.Length > 255 || !EmailAddress.TryParse(email, out _)
                        || verified.Length != 1 || verified[0].Value != "true" || verified[0].ValueType != ClaimValueTypes.Boolean
                        || azp.Length > 1 || (azp.Length == 1 && azp[0].Value != context.Options.ClientId)
                        || (audienceCount > 1 && azp.Length != 1)
                        || !long.TryParse(iat, NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
                        || issued > now.ToUnixTimeSeconds() + 60 || issued < now.ToUnixTimeSeconds() - 300)
                    {
                        context.Fail("External identity proof invalid.");
                    }
                    return Task.CompletedTask;
                },
                OnTicketReceived = async context =>
                {
                    var service = context.HttpContext.RequestServices.GetRequiredService<ExternalAuthenticationService>();
                    var local = await context.HttpContext.AuthenticateAsync(DuoraAuthentication.Scheme);
                    var sid = local.Succeeded ? DuoraClaims.ReadSessionId(local.Principal!) : null;
                    var valid = context.Properties?.Items.TryGetValue(FlowProperty, out var rawId) == true && Guid.TryParse(rawId, out var id)
                        && await service.StageProofAsync(context.Request.Cookies[FlowCookie], id,
                            new ExternalIdentity(Single(context.Principal!, "sub")!, Single(context.Principal!, "email")!), sid, context.HttpContext.RequestAborted);
                    if (!valid)
                    {
                        await service.FailAsync(context.Request.Cookies[FlowCookie], context.HttpContext.RequestAborted);
                    }

                    context.HttpContext.RequestServices.GetRequiredService<ExternalAuthenticationMetrics>().Record("callback", valid ? "validated" : "rejected");
                    context.HandleResponse();
                    ReturnToContinuation(context.HttpContext);
                },
                OnRemoteFailure = async context =>
                {
                    // Only a protected framework state identifying this browser's flow may fail that flow.
                    if (context.Properties?.Items.TryGetValue(FlowProperty, out var rawId) == true
                        && Guid.TryParse(rawId, out var id) && context.Request.Cookies[FlowCookie]?.StartsWith(id.ToString("D") + ".", StringComparison.Ordinal) == true)
                    {
                        await context.HttpContext.RequestServices.GetRequiredService<ExternalAuthenticationService>()
                            .FailAsync(context.Request.Cookies[FlowCookie], context.HttpContext.RequestAborted);
                    }
                    context.HttpContext.RequestServices.GetRequiredService<ExternalAuthenticationMetrics>().Record("callback",
                        context.Failure is HttpRequestException or TimeoutException or OperationCanceledException ? "unavailable" : "rejected");
                    context.HandleResponse();
                    ReturnToContinuation(context.HttpContext);
                },
            };
        });
        return services;
    }

    /// <summary>Reject unsupported/ambiguous transport before the upstream handler's early implicit-flow checks.</summary>
    public static IApplicationBuilder UseGoogleOidcCallbackGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (string.Equals(context.Request.Path.Value, CallbackPath, StringComparison.OrdinalIgnoreCase)
            && context.RequestServices.GetRequiredService<IOptions<GoogleOidcSettings>>().Value.Enabled)
        {
            var query = context.Request.Query;
            var ambiguous = query.Any(p => (p.Key is "state" or "code" or "error" or "iss") && p.Value.Count != 1);
            var implicitResponse = query.ContainsKey("id_token") || query.ContainsKey("access_token") || query.ContainsKey("token_type");
            var issuer = query["iss"].ToString();
            if (!HttpMethods.IsGet(context.Request.Method)
                || !string.Equals(context.Request.Path.Value, CallbackPath, StringComparison.Ordinal)
                || ambiguous || implicitResponse || (query.ContainsKey("code") && query.ContainsKey("error"))
                || (issuer.Length != 0 && issuer is not ("https://accounts.google.com" or "accounts.google.com")))
            {
                context.RequestServices.GetRequiredService<ExternalAuthenticationMetrics>().Record("callback", "rejected_transport");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                return;
            }
        }
        await next(context);
    });

    public static async Task ClearBrowserFlowAsync(HttpContext context)
    {
        if (context.Request.Cookies[FlowCookie] is { } cookie)
        {
            await context.RequestServices.GetRequiredService<ExternalAuthenticationService>().InvalidateBrowserAsync(cookie, context.RequestAborted);
            context.Response.Cookies.Delete(FlowCookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
            context.Items[FlowCookie] = true;
        }
    }

    private static string? Single(ClaimsPrincipal principal, string claim)
    {
        var values = principal.FindAll(claim).ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }

    private static void ReturnToContinuation(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/google/continue";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}

internal sealed class ExternalIdentityProtection(IDataProtectionProvider provider) : IExternalIdentityProtection
{
    private readonly IDataProtector _protector = provider.CreateProtector("Duora.ExternalIdentity.v1");
    public string Protect(ExternalIdentity identity) => _protector.Protect(JsonSerializer.Serialize(identity));
    public ExternalIdentity? Unprotect(string protectedIdentity)
    {
        try
        { return JsonSerializer.Deserialize<ExternalIdentity>(_protector.Unprotect(protectedIdentity)); }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
}
