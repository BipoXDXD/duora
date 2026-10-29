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

    [Fact]
    public void TermsReacceptanceBindsTheActiveTermsVersionLanguageAndDigest()
    {
        var publication = Create();
        var termsHash = publication.Documents.Single(document => document.Document == "terms" && document.Locale == "en-US").Sha256;
        var privacyHash = publication.Documents.Single(document => document.Document == "privacy" && document.Locale == "en-US").Sha256;

        Assert.True(publication.AcceptsTerms("terms-v2", "en-US", termsHash));
        Assert.False(publication.AcceptsTerms("terms-v2", "pt-BR", termsHash));
        Assert.False(publication.AcceptsTerms("terms-v2", "en-US", privacyHash));
        Assert.False(publication.AcceptsTerms("terms-v2", "en-US", null));
        Assert.False(publication.AcceptsTerms("old-terms", "en-US", termsHash));
        Assert.False(publication.AcceptsTerms("privacy-v3", "en-US", privacyHash));
        Assert.False(publication.AcceptsTerms("terms-v2", null, termsHash));
        Assert.False(publication.AcceptsTerms("terms-v2", "en-us", termsHash));
    }

    [Fact]
    public void AnInactiveTermsVersionStaysInTheCatalogButCannotBeAccepted()
    {
        var current = Create().Documents;
        var older = current.Where(document => document.Document == "terms")
            .Select(document => document with { Version = "terms-v1", Title = "Older synthetic document" });
        var publication = new LegalPublication("terms-v2", "privacy-v3", current.Concat(older).ToArray());
        var olderHash = publication.Documents.Single(document => document.Version == "terms-v1" && document.Locale == "pt-BR").Sha256;

        Assert.True(publication.RegistrationAvailable);
        Assert.False(publication.AcceptsTerms("terms-v1", "pt-BR", olderHash));
    }

    [Fact]
    public void UnpublishedTermsCannotBeAccepted()
    {
        var publication = Create(approved: false);
        var termsHash = publication.Documents.Single(document => document.Document == "terms" && document.Locale == "pt-BR").Sha256;

        Assert.False(publication.AcceptsTerms("terms-v2", "pt-BR", termsHash));
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
