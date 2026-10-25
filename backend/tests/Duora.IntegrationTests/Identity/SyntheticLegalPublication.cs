using Duora.Core.Identity;

namespace Duora.IntegrationTests.Identity;

internal static class SyntheticLegalPublication
{
    private static readonly string[] Kinds = ["terms", "privacy"];
    private static readonly string[] Locales = ["pt-BR", "en-US"];
    public static LegalPublication Default { get; } = Create();

    public static LegalPublication Create(string termsVersion = IdentityScenario.TermsVersion,
        string privacyVersion = IdentityScenario.TermsVersion, bool approved = true)
    {
        List<PublicLegalDocument> documents = [];
        foreach (var kind in Kinds)
        {
            foreach (var locale in Locales)
            {
                documents.Add(new PublicLegalDocument(kind, kind == "terms" ? termsVersion : privacyVersion, locale,
                    "Synthetic test document", "Synthetic Test Controller", "privacy@example.test", approved,
                    [new LegalDocumentSection("Synthetic section", ["Synthetic test content."])]));
            }
        }
        return new LegalPublication(termsVersion, privacyVersion, documents);
    }

    public static string Hash(LegalPublication publication, string document, string locale) => publication.Documents
        .Single(candidate => candidate.Document == document && candidate.Locale == locale
            && candidate.Version == (document == "terms" ? publication.TermsVersion : publication.PrivacyVersion)).Sha256;
}
