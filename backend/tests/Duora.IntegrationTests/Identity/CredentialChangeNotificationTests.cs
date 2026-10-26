using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// TM-21: toda mudança de credencial grava exatamente um aviso ao titular na outbox, na transação do efeito, para o
/// endereço gravado e no idioma da requisição. O aviso é neutro: nenhum segredo (senha, token, código, chave) entra no
/// payload. Mudança que não acontece (código errado, senha atual errada, MFA já inativo) não avisa nada.
/// </summary>
public sealed class CredentialChangeNotificationTests : IAsyncDisposable
{
    private const string PasswordChanges = "/api/v1/me/password-changes";
    private const string PasswordResets = "/api/v1/auth/password-resets";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public CredentialChangeNotificationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = TestClock.StartingNow();
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task ConsumingARecoveryCodeNotifiesOnceWithoutSecrets(string locale)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.ScalarAsync("with changed as (update identity.users set preferred_locale=$2 where id=$1 returning id) select count(*) from changed", account.UserId, locale);
        using var enrolled = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(enrolled, _time);
        using var browser = new Browser(_factory);
        using var login = await MfaScenario.LoginAsync(browser, account, recoveryCode: mfa.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var notice = Assert.Single(await NoticesAsync(account.UserId), n => n.Kind == "RecoveryCodeUsedNotice");
        Assert.Equal(account.Email, notice.To);
        Assert.Equal(locale, notice.Language);
        Assert.All(mfa.RecoveryCodes, code => Assert.DoesNotContain(code, notice.Payload, StringComparison.Ordinal));
        Assert.DoesNotContain(mfa.SharedKey, notice.Payload, StringComparison.Ordinal);
        using var replayBrowser = new Browser(_factory);
        using var replay = await MfaScenario.LoginAsync(replayBrowser, account, recoveryCode: mfa.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Single(await NoticesAsync(account.UserId), n => n.Kind == "RecoveryCodeUsedNotice");
    }

    [Fact]
    public async Task StartingEnrollmentOnlyAuditsWithoutSendingANotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.StartEnrollmentAsync(browser);
        Assert.Empty(await NoticesAsync(account.UserId));
        Assert.Equal(1, await Scenario.ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='identity.mfa_enrollment_started'", account.UserId));
    }

    [Fact]
    public async Task SecurityNoticeSnapshotsTheExplicitZoneBeforeAChangeOrRemoval()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        using var saved = await browser.PutAsync("/api/v1/me/time-zone", """{"timeZone":"America/New_York"}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await MfaScenario.EnableAsync(browser, _time);
        await browser.FetchCsrfTokenAsync();
        using var removed = await browser.PutAsync("/api/v1/me/time-zone", """{"timeZone":null}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = "\"1\"" }));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var notice = Assert.Single(await NoticesAsync(account.UserId));
        using var payload = JsonDocument.Parse(notice.Payload);
        Assert.Equal("America/New_York", payload.RootElement.GetProperty("preferredTimeZone").GetString());
        using var own = await browser.GetAsync("/api/v1/me/time-zone");
        Assert.Null((await MfaScenario.ReadObjectAsync(own))["timeZone"]);
    }

    [Fact]
    public async Task EnablingMfaSendsOneNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await MfaScenario.EnableAsync(browser, _time);

        var notice = Assert.Single(await NoticesAsync(account.UserId));
        Assert.Equal("MfaEnabledNotice", notice.Kind);
        Assert.Equal(account.Email, notice.To);
        // Sem Accept-Language, o fallback é en-US (ADR-0026).
        Assert.Equal("en-US", notice.Language);
    }

    [Fact]
    public async Task AWrongActivationCodeSendsNoNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);

        using var response = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = Totp.Wrong(_time, sharedKey) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await NoticesAsync(account.UserId));
    }

    [Fact]
    public async Task RegeneratingRecoveryCodesSendsOneNoticeWithoutTheCodes()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        using var response = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var codes = (await MfaScenario.ReadObjectAsync(response))["recoveryCodes"]!.AsArray().Select(code => (string)code!);
        var notices = await NoticesAsync(account.UserId);
        // Os dois avisos saem no mesmo instante do relógio falso: a ordem é a do tipo, não a da gravação.
        Assert.Equal(["MfaEnabledNotice", "RecoveryCodesRegeneratedNotice"], notices.Select(notice => notice.Kind));
        var regenerated = notices.Single(notice => notice.Kind == "RecoveryCodesRegeneratedNotice");
        Assert.All(codes, code => Assert.DoesNotContain(code, regenerated.Payload, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisablingMfaSendsOneNoticeAndDisablingAgainSendsNone()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(browser, _time);

        using var disabled = await browser.DeleteAsync(MfaScenario.Mfa);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        await browser.FetchPrivateContextAsync();
        await browser.FetchCsrfTokenAsync();
        using var again = await browser.DeleteAsync(MfaScenario.Mfa);

        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var notices = await NoticesAsync(account.UserId);
        Assert.Equal(["MfaDisabledNotice", "MfaEnabledNotice"], notices.Select(notice => notice.Kind));
        var disabledNotice = notices.Single(notice => notice.Kind == "MfaDisabledNotice");
        Assert.DoesNotContain(mfa.SharedKey, disabledNotice.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingThePasswordSendsOneNoticeInTheLanguageOfTheRequest()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var canary = $"CANARY-{Guid.NewGuid():N}";

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword = canary },
            InEnglish());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var notice = Assert.Single(await NoticesAsync(account.UserId));
        Assert.Equal("PasswordChangedNotice", notice.Kind);
        Assert.Equal("en-US", notice.Language);
        Assert.Equal(0, await CountOutboxPayloadsContainingAsync(canary));
        Assert.Equal(0, await CountOutboxPayloadsContainingAsync(account.Password));
    }

    [Fact]
    public async Task AWrongCurrentPasswordSendsNoNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = "não é a senha atual", newPassword = "uma frase nova e bem longa" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await NoticesAsync(account.UserId));
    }

    [Fact]
    public async Task ResettingThePasswordSendsOneNoticeWithoutTheTokenOrThePassword()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);
        var canary = $"CANARY-{Guid.NewGuid():N}";

        using var response = await browser.PostJsonAsync(
            PasswordResets,
            new { userId = link.UserId, token = link.Token, newPassword = canary },
            InEnglish());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var notice = Assert.Single(await NoticesAsync(account.UserId));
        Assert.Equal("PasswordResetNotice", notice.Kind);
        Assert.Equal("en-US", notice.Language);
        Assert.DoesNotContain(link.Token, notice.Payload, StringComparison.Ordinal);
        Assert.Equal(0, await CountOutboxPayloadsContainingAsync(canary));
    }

    [Fact]
    public async Task AnInvalidResetTokenSendsNoNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);

        using var response = await browser.PostJsonAsync(
            PasswordResets,
            new { userId = link.UserId, token = link.Token + "x", newPassword = "uma frase nova e bem longa" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await NoticesAsync(account.UserId));
    }

    [Fact]
    public async Task AResetForAnUnknownAccountAnswersLikeAnInvalidTokenAndSendsNoNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);
        var unknownUserId = Guid.CreateVersion7();

        var unknown = await ResetFailureBodyAsync(browser, unknownUserId, link.Token);

        Assert.Equal(await InvalidTokenBodyAsync(browser, link), unknown);
        Assert.Empty(await NoticesAsync(unknownUserId));
        Assert.Empty(await NoticesAsync(account.UserId));
    }

    [Fact]
    public async Task AResetForAPendingAccountAnswersLikeAnInvalidTokenAndSendsNoNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);
        var pendingEmail = IdentityScenario.NewEmail();
        using var registered = await IdentityScenario.RegisterAsync(browser, pendingEmail);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var pendingUserId = Guid.Parse(await Scenario.ScalarTextAsync(
            "select id::text from identity.users where normalized_email = upper($1) and not email_confirmed",
            pendingEmail));

        var pending = await ResetFailureBodyAsync(browser, pendingUserId, link.Token);

        Assert.Equal(await InvalidTokenBodyAsync(browser, link), pending);
        Assert.Empty(await NoticesAsync(pendingUserId));
        Assert.Empty(await NoticesAsync(account.UserId));
    }

    [Fact]
    public async Task ReusingAResetTokenKeepsASingleNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);
        using var first = await browser.PostJsonAsync(
            PasswordResets,
            new { userId = link.UserId, token = link.Token, newPassword = "uma frase nova e bem longa" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using var again = await browser.PostJsonAsync(
            PasswordResets,
            new { userId = link.UserId, token = link.Token, newPassword = "outra frase nova e bem longa" });

        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        var notice = Assert.Single(await NoticesAsync(account.UserId));
        Assert.Equal("PasswordResetNotice", notice.Kind);
    }

    [Fact]
    public async Task NoticePayloadCarriesOnlyTheRequestFields()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword = "uma frase nova e bem longa" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var payload = JsonDocument.Parse(Assert.Single(await NoticesAsync(account.UserId)).Payload);
        Assert.Equal(
            ["kind", "language", "to", "token", "userId"],
            payload.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("token").ValueKind);
    }

    private static MutationHeaders InEnglish() =>
        new(Extra: new Dictionary<string, string> { ["Accept-Language"] = "en-US" });

    private async Task<AccountLink> RequestResetLinkAsync(Browser browser, string email)
    {
        await browser.FetchCsrfTokenAsync();
        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        return await Scenario.ReadLatestLinkAsync(email, "PasswordReset");
    }

    /// <summary>Corpo do erro de reset sem o <c>traceId</c>, que muda a cada requisição.</summary>
    private static async Task<string> ResetFailureBodyAsync(Browser browser, Guid userId, string token)
    {
        using var response = await browser.PostJsonAsync(
            PasswordResets,
            new { userId, token, newPassword = "uma frase nova e bem longa" });
        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        body.Remove("traceId");
        return body.ToJsonString();
    }

    private static Task<string> InvalidTokenBodyAsync(Browser browser, AccountLink link) =>
        ResetFailureBodyAsync(browser, link.UserId, link.Token + "x");

    private async Task<List<Notice>> NoticesAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select payload->>'kind', payload->>'to', payload->>'language', payload::text
            from ops.outbox_events
            where type = 'identity.account-email-requested'
              and payload->>'userId' = $1 and payload->>'kind' like '%Notice'
            order by payload->>'kind'
            """,
            connection);
        command.Parameters.AddWithValue(userId.ToString("D"));
        var notices = new List<Notice>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            notices.Add(new Notice(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return notices;
    }

    private Task<long> CountOutboxPayloadsContainingAsync(string secret) =>
        Scenario.ScalarAsync("select count(*) from ops.outbox_events where strpos(payload::text, $1) > 0", secret);

    private sealed record Notice(string Kind, string To, string Language, string Payload);
}
