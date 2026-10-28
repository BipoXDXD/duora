using System.Net;
using System.Net.Http.Json;
using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;

namespace Duora.IntegrationTests.Identity;

public sealed class LegalDocumentTests(PostgresFixture postgres)
{
    private const string PublishedController = "Luca Grimmer";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualPublicationIsPublishedUnderTheNamedController()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { LegalDocuments = null };
        using var browser = new Browser(factory);
        using var response = await browser.GetAsync("/api/v1/legal-documents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var catalog = await response.Content.ReadFromJsonAsync<LegalDocumentCatalogResponse>(Ct);
        Assert.NotNull(catalog);
        Assert.True(catalog.RegistrationAvailable);
        Assert.Equal(4, catalog.Documents.Count);
        Assert.All(catalog.Documents, document =>
        {
            Assert.True(document.Published);
            Assert.Equal(PublishedController, document.ControllerName);
            Assert.Equal("support@meetduora.com", document.PrivacyContact);
            Assert.NotEmpty(document.Sections);
            Assert.Matches("^[a-f0-9]{64}$", document.Sha256);
        });
    }

    [Fact]
    public async Task AcceptingTheActualPublicationRegistersWithItsExactFingerprints()
    {
        var publication = LegalPublicationResources.Load();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { LegalDocuments = null };
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        await browser.FetchCsrfTokenAsync();
        using var registered = await browser.PostJsonAsync("/api/v1/auth/register",
            IdentityScenario.RegistrationBody(email, publication: publication, termsLocale: "pt-BR", privacyLocale: "en-US"));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var scenario = new IdentityScenario(postgres, factory);
        var userId = (await scenario.ReadLatestLinkAsync(email, "EmailConfirmation")).UserId;
        foreach (var (kind, version, locale) in new[] { ("terms", publication.TermsVersion, "pt-BR"), ("privacy", publication.PrivacyVersion, "en-US") })
        {
            Assert.Equal(1, await scenario.ScalarAsync("""
                select count(*) from identity.consent_records
                where user_id = $1 and lower(document) = $2 and version = $3 and document_locale = $4 and document_sha256 = $5
                """, userId, kind, version, locale, SyntheticLegalPublication.Hash(publication, kind, locale)));
        }
    }

    [Fact]
    public async Task UnapprovedPublicationCannotWriteRegistrationEvidence()
    {
        var unapproved = SyntheticLegalPublication.Create(approved: false);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { LegalDocuments = unapproved };
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        await browser.FetchCsrfTokenAsync();
        using var registered = await browser.PostJsonAsync("/api/v1/auth/register",
            IdentityScenario.RegistrationBody(email, publication: unapproved));
        await ProblemAssert.IsProblemAsync(registered, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        var scenario = new IdentityScenario(postgres, factory);
        Assert.Equal(0, await scenario.CountUsersAsync(email));
        Assert.Equal(0, await scenario.CountOutboxEventsAsync(email));
    }

    [Theory]
    [InlineData("old-version", "pt-BR", null)]
    [InlineData(IdentityScenario.TermsVersion, null, null)]
    [InlineData(IdentityScenario.TermsVersion, "pt-BR", null)]
    [InlineData(IdentityScenario.TermsVersion, "en-US", "wrong")]
    public async Task AStaleOrIncompleteReceiptCannotCreateAnAccount(string termsVersion, string? termsLocale, string? termsSha256)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        var email = IdentityScenario.NewEmail();
        var publication = SyntheticLegalPublication.Default;
        using var response = await browser.PostJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = IdentityScenario.ValidPassword,
            acceptedTermsVersion = termsVersion,
            acceptedPrivacyVersion = publication.PrivacyVersion,
            acceptedTermsLocale = termsLocale,
            acceptedPrivacyLocale = "en-US",
            acceptedTermsSha256 = termsSha256,
            acceptedPrivacySha256 = SyntheticLegalPublication.Hash(publication, "privacy", "en-US"),
        });
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var scenario = new IdentityScenario(postgres, factory);
        Assert.Equal(0, await scenario.CountUsersAsync(email));
        Assert.Equal(0, await scenario.CountOutboxEventsAsync(email));
    }

    [Fact]
    public async Task AcceptancePersistsTheExactPublishedContentFingerprint()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var response = await IdentityScenario.RegisterAsync(browser, email);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var scenario = new IdentityScenario(postgres, factory);
        var userId = (await scenario.ReadLatestLinkAsync(email, "EmailConfirmation")).UserId;
        foreach (var document in SyntheticLegalPublication.Default.Documents.Where(document => document.Locale == "pt-BR"))
        {
            Assert.Equal(1, await scenario.ScalarAsync("""
                select count(*) from identity.consent_records
                where user_id = $1 and lower(document) = $2 and version = $3 and document_locale = $4 and document_sha256 = $5
                """, userId, document.Document, document.Version, document.Locale, document.Sha256));
        }
    }
}
