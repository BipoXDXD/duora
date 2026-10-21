using System.Net;
using System.Text.Json.Nodes;
using Duora.Contracts.Profiles;
using Duora.Core.Profiles;
using Duora.Infrastructure.Geolocation;
using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Profiles;

internal static class RegionDetectionEndpoints
{
    private static readonly string[] Statuses = ["detected", "unknown", "unsupported", "unavailable", "disabled"];

    public static void MapRegionDetectionEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/me/region-detection/capability", (ICountryLookup lookup) =>
            new RegionDetectionCapabilityResponse(lookup.Enabled)).WithTags("Profiles")
            .ProducesProblem(401).ProducesProblem(403);
        api.MapPost("/me/region-detection", Detect).WithTags("Profiles")
            .Produces<RegionDetectionResponse>().ProducesValidationProblem().ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(429);
    }

    public static void ConfigureOpenApi(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(RegionDetectionResponse) && schema.Properties?["status"] is OpenApiSchema status)
            {
                status.Enum = [.. Statuses
                    .Select(value => (JsonNode)JsonValue.Create(value))];
                status.Description = "Transient observation only. Country is returned only for detected and an enabled catalog entry. Never writes profile, preferences or languages.";
            }
            return Task.CompletedTask;
        });

    private static IResult Detect(HttpContext context, ICountryLookup lookup, RegionDetectionLimiter limiter, TimeProvider clock)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        if (limiter.TryConsume(userId, context.Connection.RemoteIpAddress) is { } retry)
        {
            return ApiProblems.TooManyRequests(context, retry);
        }
        // No body is accepted, even when disabled: the client cannot supply an address or another account.
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
            || context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["body"] = ["BODY_NOT_ALLOWED"] });
        }
        IResult Respond(string status, string? country = null) =>
            TypedResults.Ok(new RegionDetectionResponse(status, country, clock.GetUtcNow()));
        if (!lookup.Enabled)
        {
            return Respond("disabled");
        }
        var address = context.Connection.RemoteIpAddress;
        // A remaining forwarding chain after the single trusted hop is inconclusive, never a second hop to parse.
        if (context.Request.Headers.ContainsKey("X-Original-For") && context.Request.Headers.ContainsKey("X-Forwarded-For"))
        {
            return Respond("unknown");
        }
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }
        if (address is null || !IsPublic(address))
        {
            return Respond("unknown");
        }
        context.RequestAborted.ThrowIfCancellationRequested();
        var result = lookup.Lookup(address);
        context.RequestAborted.ThrowIfCancellationRequested();
        return result.Status switch
        {
            CountryLookupStatus.Detected when ProfileLocationCatalog.Countries.Contains(result.CountryCode, StringComparer.Ordinal)
                => Respond("detected", result.CountryCode),
            CountryLookupStatus.Detected when result.CountryCode is not null => Respond("unsupported"),
            CountryLookupStatus.Unknown => Respond("unknown"),
            _ => Respond("unavailable"),
        };
    }

    private static readonly System.Net.IPNetwork[] NonPublicV4 =
        [.. new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24",
            "203.0.113.0/24", "224.0.0.0/3" }.Select(System.Net.IPNetwork.Parse)];
    private static readonly System.Net.IPNetwork GlobalV6 = System.Net.IPNetwork.Parse("2000::/3");
    private static readonly System.Net.IPNetwork[] NonPublicV6 =
        [.. new[] { "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20" }.Select(System.Net.IPNetwork.Parse)];

    private static bool IsPublic(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        ? !NonPublicV4.Any(network => network.Contains(address))
        : address.ScopeId == 0 && GlobalV6.Contains(address) && !NonPublicV6.Any(network => network.Contains(address));
}
