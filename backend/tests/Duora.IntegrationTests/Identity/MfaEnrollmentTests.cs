using System.Net;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Cadastro do TOTP (API-A11) e ativação por prova de posse (API-A12), para qualquer conta (ADR-0017). A chave e os
/// códigos de recuperação nunca ficam em claro no banco (decisões D2 e D3 do auth.md).
/// </summary>
public sealed class MfaEnrollmentTests : IAsyncDisposable
{
    private const string SessionCookie = "__Host-duora";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public MfaEnrollmentTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task EnrollmentNeedsARecentAuthentication()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        _time.Advance(TimeSpan.FromMinutes(5));

        using var response = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(0, await CountTokensAsync(account.UserId));
    }

    [Fact]
    public async Task EnrollmentReturnsTheKeyAndUriOnceWithoutActivatingMfa()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await MfaScenario.ReadObjectAsync(response);
        Assert.Equal(["authenticatorUri", "sharedKey"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        var sharedKey = (string)body["sharedKey"]!;
        var uri = new Uri((string)body["authenticatorUri"]!);
        Assert.Equal("otpauth", uri.Scheme);
        Assert.Equal("totp", uri.Host);
        Assert.Contains($"secret={sharedKey}", uri.Query, StringComparison.Ordinal);
        Assert.Contains("issuer=Duora", uri.Query, StringComparison.Ordinal);
        Assert.False(await MfaEnabledAsync(browser));
    }

    [Fact]
    public async Task StartingEnrollmentKeepsTheCurrentSessionButEndsTheOthers()
    {
        // Trocar a chave do autenticador troca o security stamp (comportamento do Identity); só a sessão que pediu
        // o cadastro adota o stamp novo.
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var other = await Scenario.SignedInBrowserAsync(account);
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await MfaScenario.StartEnrollmentAsync(browser);
        using var current = await browser.GetAsync("/api/v1/me");
        using var otherMe = await other.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherMe.StatusCode);
    }

    [Fact]
    public async Task AWrongCodeDoesNotActivateMfa()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);

        using var response = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = Totp.Wrong(sharedKey) });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "MFA_CODE_INVALID");
        Assert.False(await MfaEnabledAsync(browser));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("")]
    public async Task AMalformedCodeIsAValidationError(string code)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.StartEnrollmentAsync(browser);

        using var response = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task ConfirmationWithoutEnrollmentIsAConflict()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = "123456" });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "MFA_ENROLLMENT_NOT_STARTED");
    }

    [Fact]
    public async Task ConfirmationActivatesMfaReturnsTenCodesAndRenewsTheSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var other = await Scenario.SignedInBrowserAsync(account);
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);
        var cookieBefore = browser.Cookie(SessionCookie);

        using var response = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = Totp.Now(sharedKey) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await MfaScenario.ReadObjectAsync(response);
        Assert.Equal(["recoveryCodes"], body.Select(p => p.Key));
        var codes = body["recoveryCodes"]!.AsArray().Select(code => (string)code!).ToList();
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(cookieBefore, browser.Cookie(SessionCookie));
        Assert.True(await MfaEnabledAsync(browser));
        using var otherMe = await other.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, otherMe.StatusCode);
        var active = Assert.Single(await Scenario.SessionRowsAsync(account.UserId), row => !row.Revoked);
        Assert.Equal(_time.GetUtcNow(), active.MfaVerifiedAt);
    }

    [Fact]
    public async Task MfaAlreadyActiveCannotBeEnrolledOrConfirmedAgain()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(browser);

        using var enroll = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);
        using var confirm = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = Totp.Now(mfa.SharedKey) });

        await ProblemAssert.IsProblemAsync(enroll, HttpStatusCode.Conflict, "MFA_ALREADY_ENABLED");
        await ProblemAssert.IsProblemAsync(confirm, HttpStatusCode.Conflict, "MFA_ALREADY_ENABLED");
    }

    [Fact]
    public async Task TheDatabaseNeverHoldsTheKeyOrTheRecoveryCodesInClear()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        var mfa = await MfaScenario.EnableAsync(browser);

        var stored = await TokenValuesAsync(account.UserId);
        Assert.NotEmpty(stored);
        Assert.DoesNotContain(stored, value => value.Contains(mfa.SharedKey, StringComparison.OrdinalIgnoreCase));
        foreach (var code in mfa.RecoveryCodes)
        {
            Assert.DoesNotContain(stored, value => value.Contains(code, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(stored, value => value.Contains(code.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task EnrollmentAndActivationAreAudited()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await MfaScenario.EnableAsync(browser);

        Assert.Equal(
            ["identity.mfa_enabled", "identity.mfa_enrollment_started"],
            await AuditActionsAsync(account.UserId));
    }

    private static async Task<bool> MfaEnabledAsync(Browser browser)
    {
        using var me = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        return (bool)(await MfaScenario.ReadObjectAsync(me))["mfaEnabled"]!;
    }

    private Task<long> CountTokensAsync(Guid userId) =>
        Scenario.ScalarAsync("select count(*) from identity.user_tokens where user_id = $1", userId);

    private async Task<List<string>> TokenValuesAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select coalesce(value, '') from identity.user_tokens where user_id = $1",
            connection);
        command.Parameters.AddWithValue(userId);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    internal static async Task<List<string>> AuditActionsAsync(PostgresFixture postgres, Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select action from ops.audit_events
            where target_type = 'user' and target_id = $1 and actor_id = $1 and actor_kind = 'User'
            order by action
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var actions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            actions.Add(reader.GetString(0));
        }

        return actions;
    }

    private Task<List<string>> AuditActionsAsync(Guid userId) => AuditActionsAsync(_postgres, userId);
}
