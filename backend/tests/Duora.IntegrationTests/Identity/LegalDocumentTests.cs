using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;

namespace Duora.IntegrationTests.Identity;

public sealed partial class LegalDocumentTests(PostgresFixture postgres)
{
    // Temporary (decisions-2026-11-07): the identification returns once there is a registered entity to publish.
    private const string ActiveController = "Responsável pelo Duora";
    private const string CurrentTermsVersion = "2026-11-07";
    private const string CurrentPrivacyVersion = "2026-11-07";
    // SHA-256 of each casefolded word of the former personal identification, so the guard does not republish it.
    private static readonly string[] FormerIdentificationWords =
    [
        "6ad24e7987574a99f016032e7603f0cad2842d85c3a3162fe53a05e6e58547cb",
        "d70f47790f689414789eeff231703429c7f88a10210775906460edbf38589d90",
    ];
    private static readonly string[] FormerIdentificationPhrases = ["pessoa física", "pessoa fisica", ", an individual"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualPublicationIsPublishedUnderTheNeutralController()
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
        Assert.Equal(CurrentTermsVersion, catalog.TermsVersion);
        Assert.Equal(CurrentPrivacyVersion, catalog.PrivacyVersion);
        var active = catalog.Documents.Where(document => IsActive(document, catalog)).ToArray();
        Assert.Equal(4, active.Length);
        Assert.All(active, document => Assert.Equal(ActiveController, document.ControllerName));
        // The superseded versions stay in the catalog so earlier acceptance evidence still resolves to its text.
        Assert.Equal(2, catalog.Documents.Count(document => document.Document == "terms" && document.Version == "2026-10-25"));
        Assert.Equal(2, catalog.Documents.Count(document => document.Document == "terms" && document.Version == "2026-10-29"));
        Assert.Equal(2, catalog.Documents.Count(document => document.Document == "privacy" && document.Version == "2026-10-25"));
        Assert.All(catalog.Documents, document =>
        {
            Assert.True(document.Published);
            Assert.Equal("support@meetduora.com", document.PrivacyContact);
            Assert.NotEmpty(document.Sections);
            Assert.Matches("^[a-f0-9]{64}$", document.Sha256);
        });
    }

    [Fact]
    public async Task ActiveDocumentsServedByTheApiDoNotCarryTheFormerPersonalIdentification()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { LegalDocuments = null };
        using var browser = new Browser(factory);
        using var response = await browser.GetAsync("/api/v1/legal-documents");
        var catalog = await response.Content.ReadFromJsonAsync<LegalDocumentCatalogResponse>(Ct);
        Assert.NotNull(catalog);
        var active = catalog.Documents.Where(document => IsActive(document, catalog)).ToArray();
        Assert.Equal(4, active.Length);

        Assert.All(active, document => Assert.Empty(FormerIdentificationIn(document)));
    }

    [Theory]
    [InlineData("Operated by someone, an individual.", ", an individual")]
    [InlineData("mantido por PESSOA FÍSICA", "pessoa física")]
    public void TheIdentificationGuardReadsCaseVariations(string text, string expected)
    {
        Assert.Equal(expected, Assert.Single(FormerIdentificationIn(text)));
    }

    [Fact]
    public void TheIdentificationGuardAcceptsTheNeutralText()
    {
        Assert.Empty(FormerIdentificationIn($"{ActiveController}, support@meetduora.com"));
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

    // Golden master of every published text: a published version never changes. Editing a text, the controller
    // or the contact without creating a new version must break this test.
    [Theory]
    [InlineData("terms", "2026-11-07", "pt-BR", "101bd6102a89bc32135e2cfda3f7c2bbbe739ca6e7e4a3d6024124348960e8e8")]
    [InlineData("terms", "2026-11-07", "en-US", "b07c56cc2655f8625d5221521eaba32cfa874bd55fe6609ab66cec187f64d80e")]
    [InlineData("privacy", "2026-11-07", "pt-BR", "3f045d877474bb9f5510fa0fdac7833ac0d7e0e5b4fd572d6f68712c2058e073")]
    [InlineData("privacy", "2026-11-07", "en-US", "e1b3642bdadd51033dd7aff072be8be4bf849d3dc7d5072da417683d43d38d3f")]
    [InlineData("terms", "2026-10-25", "pt-BR", "07fd125aa37cea239d0dc0bf702a32892e66e2d16ed0e5a0c1908c5034f30227")]
    [InlineData("terms", "2026-10-25", "en-US", "fc36a782961b51fe8ab5b2b49ae71596763f4d365736855cd916cff38e27c8b6")]
    [InlineData("terms", "2026-10-29", "pt-BR", "7b7c64f8bb387fe287aa06018225ddda597401ce72a8b88a7742958391312ae6")]
    [InlineData("terms", "2026-10-29", "en-US", "825198ad949b28c33a8d790809af4fe7a745969d8abc5d506249143e9a321d5c")]
    [InlineData("privacy", "2026-10-25", "pt-BR", "ee20f316d68c3e7c08d2fd69f763358e6f8d8142164b9cf1afc8b1d722c8405a")]
    [InlineData("privacy", "2026-10-25", "en-US", "a4802a18c0f2d731784375c7bba88d70f9e8778168d36b885297f59f0b6e174d")]
    public void PublishedDocumentKeepsItsExactFingerprint(string kind, string version, string locale, string expectedSha256)
    {
        var document = Assert.Single(LegalPublicationResources.Load().Documents,
            candidate => candidate.Document == kind && candidate.Version == version && candidate.Locale == locale);

        Assert.Equal(expectedSha256, document.Sha256);
    }

    // Issue #9, item 5: product coherence with the 18+ requirement, not age verification nor legal compliance.
    // Accepting this terms version (stored with its digest) is the record of the registrant's declaration.
    [Theory]
    [InlineData("pt-BR", "Idade mínima", "exclusivamente a pessoas com 18 anos ou mais", "você declara ter 18 anos ou mais")]
    [InlineData("en-US", "Minimum age", "only for people aged 18 or older", "you declare that you are 18 or older")]
    public void CurrentTermsRestrictTheServiceToAdultsWhoDeclareTheirAge(string locale, string title, string restriction, string declaration)
    {
        var publication = LegalPublicationResources.Load();
        var terms = Assert.Single(publication.Documents,
            candidate => candidate.Document == "terms" && candidate.Version == publication.TermsVersion && candidate.Locale == locale);

        var section = Assert.Single(terms.Sections, candidate => candidate.Title == title);
        var text = string.Join(' ', section.Paragraphs);
        Assert.Contains(restriction, text, StringComparison.Ordinal);
        Assert.Contains(declaration, text, StringComparison.Ordinal);
        Assert.Equal(CurrentTermsVersion, publication.TermsVersion);
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
            confirmedAdult = true,
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

    private static bool IsActive(LegalDocumentResponse document, LegalDocumentCatalogResponse catalog) =>
        document.Version == (document.Document == "terms" ? catalog.TermsVersion : catalog.PrivacyVersion);

    private static string[] FormerIdentificationIn(LegalDocumentResponse document) =>
        FormerIdentificationIn(string.Join(' ',
            new[] { document.Title, document.ControllerName ?? "", document.PrivacyContact ?? "" }
                .Concat(document.Sections.SelectMany(section => section.Paragraphs.Prepend(section.Title)))));

    private static string[] FormerIdentificationIn(string text)
    {
        var lowered = text.ToLowerInvariant();
        var words = Word().Matches(lowered).Select(match => match.Value)
            .Where(word => FormerIdentificationWords.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(word)))));
        return words.Concat(FormerIdentificationPhrases.Where(phrase => lowered.Contains(phrase, StringComparison.Ordinal))).ToArray();
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();
}
