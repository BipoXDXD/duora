using Duora.Web.Http;
using Duora.Web.Security;

namespace Duora.Web.Webhooks;

/// <summary>Callbacks etários aposentados. Corpo e assinatura não são processados nem persistidos.</summary>
internal static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{provider}", () => ApiProblems.Problem(410, "AGE_VERIFICATION_RETIRED", "Age verification has been retired."))
            .AllowAnonymous().WithMetadata(new CsrfExemptEndpoint()).ExcludeFromDescription();
    }
}
