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

    [Fact]
    public async Task EnablingMfaSendsOneNotice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await MfaScenario.EnableAsync(browser, _time);

        var notice = Assert.Single(await NoticesAsync(account.UserId));
        Assert.Equal("MfaEnabledNotice", notice.Kind);
        Assert.Equal(account.Email, notice.To);
        Assert.Equal("pt-BR", notice.Language);
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
        Assert.Equal("en", notice.Language);
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
        Assert.Equal("en", notice.Language);
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
        new(Extra: new Dictionary<string, string> { ["Accept-Language"] = "en" });

    private async Task<AccountLink> RequestResetLinkAsync(Browser browser, string email)
    {
        await browser.FetchCsrfTokenAsync();
        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        return await Scenario.ReadLatestLinkAsync(email, "PasswordReset");
    }

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
