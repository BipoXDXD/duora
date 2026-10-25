using Duora.Contracts.Identity;
using Duora.Core.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Accounts;

internal static class LegalDocumentEndpoints
{
    public static void MapLegalDocumentEndpoints(this IEndpointRouteBuilder api) => api
        .MapGet("/legal-documents", Read)
        .AllowAnonymous()
        .WithTags("Legal")
        .WithMetadata(new PublicAntiforgeryEndpoint());

    private static Ok<LegalDocumentCatalogResponse> Read(HttpContext context, LegalPublication publication)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Ok(new LegalDocumentCatalogResponse(publication.RegistrationAvailable,
            publication.TermsVersion, publication.PrivacyVersion, publication.Documents.Select(document =>
                new LegalDocumentResponse(document.Document, document.Version, document.Locale, document.Title,
                    document.ControllerName, document.PrivacyContact, LegalPublication.IsPublished(document),
                    document.Sha256, document.Sections.Select(section => new LegalDocumentSectionResponse(section.Title, section.Paragraphs)).ToArray())
            ).ToArray()));
    }
}
