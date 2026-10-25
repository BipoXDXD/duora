using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class LegalPublicationTests
{
    private static readonly string[] Kinds = ["terms", "privacy"];
    private static readonly string[] Locales = ["pt-BR", "en-US"];
    [Fact]
    public void UnapprovedOrUnidentifiedDocumentsCannotAuthorizeRegistration()
    {
        Assert.False(Create(approved: false).RegistrationAvailable);
        Assert.False(Create(controller: null).RegistrationAvailable);
        Assert.False(Create(contact: null).RegistrationAvailable);
        Assert.False(Create(contact: "not an address").RegistrationAvailable);
    }

    [Fact]
    public void AcceptanceBindsEachActualDocumentVersionAndLanguage()
    {
        var publication = Create();
        Assert.True(publication.RegistrationAvailable);
        var termsHash = publication.Documents.Single(document => document.Document == "terms" && document.Locale == "pt-BR").Sha256;
        var privacyHash = publication.Documents.Single(document => document.Document == "privacy" && document.Locale == "en-US").Sha256;
        Assert.True(publication.Accepts("terms-v2", "privacy-v3", "pt-BR", "en-US", termsHash, privacyHash));
        Assert.False(publication.Accepts("terms-v2", "privacy-v3", "pt-BR", "en-US", privacyHash, termsHash));
        Assert.False(publication.Accepts("terms-v2", "privacy-v3", "en-US", "pt-BR", termsHash, privacyHash));
        Assert.False(publication.Accepts("old-terms", "privacy-v3", "pt-BR", "en-US"));
        Assert.False(publication.Accepts("terms-v2", "privacy-v3", null, "en-US"));
        Assert.False(publication.Accepts("terms-v2", "privacy-v3", "pt-br", "en-US"));
        Assert.False(publication.Accepts("terms-v2", "privacy-v3", "pt-BR", "fr"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void UrlPathSegmentsCannotBeDocumentVersions(string version)
    {
        var documents = Create().Documents.Select(document => document with { Version = version }).ToArray();
        Assert.False(new LegalPublication(version, version, documents).RegistrationAvailable);
    }

    [Fact]
    public void MissingOrDuplicatedVariantsFailClosedWithoutInventingTranslations()
    {
        var documents = Create().Documents;
        Assert.False(new LegalPublication("terms-v2", "privacy-v3", documents.Skip(1).ToArray()).RegistrationAvailable);
        Assert.False(new LegalPublication("terms-v2", "privacy-v3", documents.Append(documents[0]).ToArray()).RegistrationAvailable);
    }

    private static LegalPublication Create(bool approved = true, string? controller = "Synthetic Test Controller", string? contact = "privacy@example.test")
    {
        List<PublicLegalDocument> documents = [];
        foreach (var kind in Kinds)
        {
            foreach (var locale in Locales)
            {
                documents.Add(new PublicLegalDocument(kind, kind == "terms" ? "terms-v2" : "privacy-v3", locale,
                    "Synthetic document", controller, contact, approved, [new LegalDocumentSection("Synthetic section", ["Synthetic text."])]));
            }
        }
        return new LegalPublication("terms-v2", "privacy-v3", documents);
    }
}
