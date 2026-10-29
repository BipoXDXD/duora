using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Novo aceite dos Termos por conta existente (issue #296, API-A17). Não bloqueia o uso: <c>GET /me</c> só informa
/// se o aceite da conta é o da versão ativa, e <c>POST /me/terms-acceptances</c> grava o aceite com a declaração de
/// 18 anos ou mais pelo mesmo mecanismo do cadastro (versão, idioma e SHA-256), sempre para a conta da sessão.
/// </summary>
public sealed class TermsReacceptanceTests : IAsyncDisposable
{
    private const string Route = "/api/v1/me/terms-acceptances";
    private const string OlderTermsVersion = "2026-09-01";

    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = TestClock.StartingNow();
    private readonly DuoraWebFactory _factory;
    private readonly IdentityScenario _scenario;

    public TermsReacceptanceTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new IdentityScenario(postgres, _factory);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AccountThatAcceptedTheActiveTermsAtRegistrationIsReportedAsCurrent()
    {
        var account = await _scenario.CreateConfirmedAccountAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);

        var me = await ReadMeAsync(browser);

        Assert.True((bool?)me["currentTermsAccepted"]);
    }

    [Fact]
    public async Task ParticipantWithoutAnyTermsAcceptanceIsReportedAsNotCurrent()
    {
        var account = await _scenario.CreateConfirmedAccountAsync();
        await RemoveAllConsentsAsync(account.UserId);
        using var browser = await _scenario.SignedInBrowserAsync(account);

        var me = await ReadMeAsync(browser);

        Assert.False((bool?)me["currentTermsAccepted"]);
    }

    // Os Termos de participante não se aplicam a conta privilegiada (D13): o bootstrap a cria sem aceite, e /me
    // responde true ("nada pendente") para a SPA não pedir um aceite que a conta não pode dar.
    [Theory]
    [InlineData(Duora.Core.Identity.AccountRoles.Moderator)]
    [InlineData(Duora.Core.Identity.AccountRoles.ProductAdmin)]
    [InlineData(Duora.Core.Identity.AccountRoles.Operator)]
    public async Task PrivilegedAccountWithoutAnyAcceptanceHasNothingPending(string role)
    {
        var account = await CreatePrivilegedAccountLikeTheBootstrapAsync(role);
        using var browser = await _scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        var me = await ReadMeAsync(browser);

        Assert.True((bool?)me["currentTermsAccepted"]);
    }

    [Fact]
    public async Task PrivilegedAccountCannotUseTheParticipantReacceptanceAndNothingIsRecorded()
    {
        var account = await CreatePrivilegedAccountLikeTheBootstrapAsync(Duora.Core.Identity.AccountRoles.ProductAdmin);
        using var browser = await _scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        using var response = await browser.PostJsonAsync(Route, Body("pt-BR"));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0, await _scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task AccountWithAnOlderAcceptanceIsReportedAndKeepsUsingTheApp()
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);

        var me = await ReadMeAsync(browser);
        using var sessions = await browser.GetAsync("/api/v1/me/sessions");
        using var profile = await browser.GetAsync("/api/v1/me/profile");

        Assert.False((bool?)me["currentTermsAccepted"]);
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task ReacceptanceRecordsTheActiveTermsWithTheAdultDeclarationAndKeepsTheHistory(string locale)
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(Route, Body(locale));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True((bool?)(await ReadMeAsync(browser))["currentTermsAccepted"]);
        Assert.Equal(
            $"Terms|{OlderTermsVersion}||,Terms|{IdentityScenario.TermsVersion}|{locale}|{ActiveTermsHash(locale)}",
            await TermsEvidenceAsync(account.UserId));
        Assert.Equal(1, await _scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1 and document = 'Privacy'", account.UserId));
    }

    [Fact]
    public async Task RepeatingTheReacceptanceIsIdempotentAndKeepsTheFirstEvidence()
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        using var first = await browser.PostJsonAsync(Route, Body("pt-BR"));
        var evidence = await TermsEvidenceWithInstantAsync(account.UserId);

        using var second = await browser.PostJsonAsync(Route, Body("pt-BR"));
        using var otherLocale = await browser.PostJsonAsync(Route, Body("en-US"));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, otherLocale.StatusCode);
        Assert.Equal(evidence, await TermsEvidenceWithInstantAsync(account.UserId));
    }

    [Fact]
    public async Task AccountThatAlreadyHasTheActiveTermsGetsNoSecondRow()
    {
        var account = await _scenario.CreateConfirmedAccountAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var evidence = await TermsEvidenceWithInstantAsync(account.UserId);

        using var response = await browser.PostJsonAsync(Route, Body("en-US"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(evidence, await TermsEvidenceWithInstantAsync(account.UserId));
    }

    [Fact]
    public async Task ConcurrentReacceptancesRecordOneAcceptance()
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => browser.PostJsonAsync(Route, Body("pt-BR"))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal(1, await _scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1 and document = 'Terms' and version = $2",
            account.UserId, IdentityScenario.TermsVersion));
    }

    // Ausente, null ou false: a declaração de 18+ é explícita, como no cadastro (API-A02).
    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task WithoutTheAdultDeclarationNothingIsRecorded(string? confirmedAdult)
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var before = await TermsEvidenceWithInstantAsync(account.UserId);

        using var response = await browser.PostRawJsonAsync(Route, Json(Body("pt-BR"), "confirmedAdult", confirmedAdult));

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("REQUIRED", (string?)problem["errors"]?["confirmedAdult"]?[0]);
        Assert.Single(problem["errors"]!.AsObject());
        Assert.Equal(before, await TermsEvidenceWithInstantAsync(account.UserId));
        Assert.False((bool?)(await ReadMeAsync(browser))["currentTermsAccepted"]);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public async Task AdultDeclarationOfTheWrongTypeIsRejectedAndNothingIsRecorded(string confirmedAdult)
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var before = await TermsEvidenceWithInstantAsync(account.UserId);

        using var response = await browser.PostRawJsonAsync(Route, Json(Body("pt-BR"), "confirmedAdult", confirmedAdult));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        Assert.Equal(before, await TermsEvidenceWithInstantAsync(account.UserId));
    }

    // Só a versão ativa, no idioma e com o SHA-256 do texto publicado; versão antiga não se "reaceita".
    [Theory]
    [InlineData(OlderTermsVersion, "pt-BR", null)]
    [InlineData("2099-01-01", "pt-BR", null)]
    [InlineData(IdentityScenario.TermsVersion, "fr-FR", null)]
    [InlineData(IdentityScenario.TermsVersion, "pt-BR", "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task ReceiptThatIsNotTheActiveTermsIsRejectedAndNothingIsRecorded(string version, string locale, string? sha256)
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var before = await TermsEvidenceWithInstantAsync(account.UserId);

        using var response = await browser.PostJsonAsync(Route, new
        {
            acceptedTermsVersion = version,
            acceptedTermsLocale = locale,
            acceptedTermsSha256 = sha256 ?? ActiveTermsHash("pt-BR"),
            confirmedAdult = true,
        });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(before, await TermsEvidenceWithInstantAsync(account.UserId));
    }

    // DTO estrito: dono, conta, instante ou documento não vêm do cliente; o aceite é sempre da conta da sessão.
    [Theory]
    [InlineData("userId", "\"0199d700-7f20-7000-8000-000000000001\"")]
    [InlineData("acceptedAt", "\"2020-01-01T00:00:00Z\"")]
    [InlineData("document", "\"Privacy\"")]
    [InlineData("age", "30")]
    public async Task UnknownFieldIsRejectedAndNothingIsRecorded(string field, string value)
    {
        var account = await CreateAccountWithOlderTermsAsync();
        var other = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var before = await TermsEvidenceWithInstantAsync(account.UserId);
        var otherBefore = await TermsEvidenceWithInstantAsync(other.UserId);

        using var response = await browser.PostRawJsonAsync(Route, Json(Body("pt-BR"), field, value));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        Assert.Equal(before, await TermsEvidenceWithInstantAsync(account.UserId));
        Assert.Equal(otherBefore, await TermsEvidenceWithInstantAsync(other.UserId));
    }

    [Fact]
    public async Task ReacceptanceChangesOnlyTheSessionAccount()
    {
        var account = await CreateAccountWithOlderTermsAsync();
        var other = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var otherBefore = await TermsEvidenceWithInstantAsync(other.UserId);

        using var response = await browser.PostJsonAsync(Route, Body("pt-BR"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(otherBefore, await TermsEvidenceWithInstantAsync(other.UserId));
    }

    [Fact]
    public async Task WithoutSessionTheAnswerIs401AndNothingIsRecorded()
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var anonymous = new Browser(_factory);
        await anonymous.FetchCsrfTokenAsync();
        var before = await TermsEvidenceWithInstantAsync(account.UserId);

        using var response = await anonymous.PostJsonAsync(Route, Body("pt-BR"));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal(before, await TermsEvidenceWithInstantAsync(account.UserId));
    }

    [Fact]
    public async Task WithoutTheAntiforgeryTokenTheAnswerIs403AndNothingIsRecorded()
    {
        var account = await CreateAccountWithOlderTermsAsync();
        using var browser = await _scenario.SignedInBrowserAsync(account);
        var before = await TermsEvidenceWithInstantAsync(account.UserId);

        using var response = await browser.PostJsonAsync(Route, Body("pt-BR"), new MutationHeaders(SendCsrfToken: false));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.Equal(before, await TermsEvidenceWithInstantAsync(account.UserId));
    }

    /// <summary>
    /// Conta confirmada cujo aceite dos Termos é de uma versão anterior à ativa, como as contas de antes de
    /// <c>2026-10-29</c>. A linha é trocada por SQL porque o cadastro só aceita a versão ativa; o gatilho do schema
    /// impede UPDATE, então a evidência sintética é apagada e gravada de novo, sem idioma nem SHA-256 (legado).
    /// </summary>
    private async Task<TestAccount> CreateAccountWithOlderTermsAsync()
    {
        var account = await _scenario.CreateConfirmedAccountAsync();
        await _scenario.ScalarAsync(
            """
            with removed as (delete from identity.consent_records where user_id = $1 and document = 'Terms' returning user_id, accepted_at),
                 inserted as (
                     insert into identity.consent_records (id, user_id, document, version, accepted_at)
                     select gen_random_uuid(), user_id, 'Terms', $2, accepted_at from removed
                     returning 1)
            select count(*) from inserted
            """,
            account.UserId, OlderTermsVersion);
        return account;
    }

    /// <summary>
    /// Conta privilegiada como o <c>AdministratorBootstrap</c> a deixa: sem nenhum aceite. A fixture parte do cadastro
    /// (<see cref="IdentityScenario.CreatePrivilegedAccountAsync"/>) e apaga os aceites que ele gravou.
    /// </summary>
    private async Task<TestAccount> CreatePrivilegedAccountLikeTheBootstrapAsync(string role)
    {
        var account = await _scenario.CreatePrivilegedAccountAsync(role);
        await RemoveAllConsentsAsync(account.UserId);
        return account;
    }

    private Task<long> RemoveAllConsentsAsync(Guid userId) => _scenario.ScalarAsync(
        "with removed as (delete from identity.consent_records where user_id = $1 returning 1) select count(*) from removed",
        userId);

    private static object Body(string locale) => new
    {
        acceptedTermsVersion = IdentityScenario.TermsVersion,
        acceptedTermsLocale = locale,
        acceptedTermsSha256 = ActiveTermsHash(locale),
        confirmedAdult = true,
    };

    private static string ActiveTermsHash(string locale) =>
        SyntheticLegalPublication.Hash(SyntheticLegalPublication.Default, "terms", locale);

    /// <summary>The body with <paramref name="field"/> set to raw JSON, or removed when the value is null.</summary>
    private static string Json(object body, string field, string? rawValue)
    {
        var node = JsonSerializer.SerializeToNode(body)!.AsObject();
        node.Remove(field);
        if (rawValue is not null)
        {
            node[field] = JsonNode.Parse(rawValue);
        }

        return node.ToJsonString();
    }

    private Task<string> TermsEvidenceAsync(Guid userId) => _scenario.ScalarTextAsync(
        """
        select string_agg(document || '|' || version || '|' || coalesce(document_locale, '') || '|' || coalesce(document_sha256, ''), ',' order by version)
        from identity.consent_records where user_id = $1 and document = 'Terms'
        """,
        userId);

    private Task<string> TermsEvidenceWithInstantAsync(Guid userId) => _scenario.ScalarTextAsync(
        """
        select string_agg(id::text || '|' || version || '|' || coalesce(document_locale, '') || '|' || coalesce(document_sha256, '') || '|' || accepted_at::text, ',' order by version)
        from identity.consent_records where user_id = $1 and document = 'Terms'
        """,
        userId);

    private static async Task<JsonObject> ReadMeAsync(Browser browser)
    {
        using var response = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)!.AsObject();
    }
}
