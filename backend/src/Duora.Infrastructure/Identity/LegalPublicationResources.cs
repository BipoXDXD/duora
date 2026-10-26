using System.Text.Json;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Identity;

internal static class LegalPublicationResources
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LegalPublication Load()
    {
        using var stream = typeof(LegalPublicationResources).Assembly.GetManifestResourceStream(
            "Duora.Infrastructure.Identity.LegalDocuments.catalog.json")
            ?? throw new InvalidOperationException("The versioned legal document resource is missing.");
        var source = JsonSerializer.Deserialize<PublicationSource>(stream, Json)
            ?? throw new InvalidOperationException("The versioned legal document resource is invalid.");
        return new LegalPublication(source.TermsVersion, source.PrivacyVersion, source.Documents);
    }

    private sealed record PublicationSource(string TermsVersion, string PrivacyVersion, IReadOnlyList<PublicLegalDocument> Documents);
}
