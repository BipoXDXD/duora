using System.Net;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// TM-05: login, falha de senha, início do bloqueio, tentativa durante o bloqueio, desbloqueio pelo reset, logout e
/// revogação de sessão deixam exatamente um registro em <c>ops.audit_events</c>, na transação do efeito. O registro
/// só tem ator, ação e alvo: nada de e-mail, senha, IP ou user agent. Login com e-mail sem conta não grava nada
/// (não há alvo, e o registro não pode virar indício de existência), e a resposta é a mesma da senha errada.
/// </summary>
/// <remarks>Cada teste usa um banco migrado só dele, para contar todas as linhas da tabela sem interferência.</remarks>
public sealed class LoginAuditTests(PostgresFixture postgres)
{
    private const string WrongPassword = "não é a senha da conta";

    /// <summary>Igual a <c>Lockout.MaxFailedAccessAttempts</c> da aplicação.</summary>
    private const int LockoutThreshold = 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASuccessfulLoginIsAuditedOnce()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();

        using var browser = await context.Scenario.SignedInBrowserAsync(account);

        Assert.Equal(["identity.login_succeeded"], await context.UserActionsAsync(account.UserId));
    }

    [Fact]
    public async Task AWrongPasswordIsAuditedOnceAsAFailedLogin()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(context.Factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["identity.login_failed"], await context.UserActionsAsync(account.UserId));
    }

    [Fact]
    public async Task TheFailureThatStartsTheLockoutIsAuditedAndLaterAttemptsAreRefusedOnTheRecord()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        await context.SetFailedAttemptsAsync(account.UserId, LockoutThreshold - 1);
        using var browser = new Browser(context.Factory);

        using var locking = await IdentityScenario.LoginAsync(browser, account.Email, WrongPassword);
        using var whileLocked = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, locking.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, whileLocked.StatusCode);
        Assert.Equal(
            ["identity.account_locked_out", "identity.login_failed", "identity.login_refused_locked_out"],
            await context.UserActionsAsync(account.UserId));
    }

    [Fact]
    public async Task AWrongSecondFactorThatStartsTheLockoutIsAudited()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using (var enrolling = await context.Scenario.SignedInBrowserAsync(account))
        {
            var mfa = await MfaScenario.EnableAsync(enrolling, context.Time);
            await context.SetFailedAttemptsAsync(account.UserId, LockoutThreshold - 1);
            using var browser = new Browser(context.Factory);

            using var response = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Wrong(context.Time, mfa.SharedKey));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var actions = await context.UserActionsAsync(account.UserId);
        Assert.Single(actions, action => action == "identity.second_factor_failed");
        Assert.Single(actions, action => action == "identity.account_locked_out");
    }

    [Fact]
    public async Task AResetThatEndsALockoutIsAuditedAsResetAndUnlock()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        await context.LockOutAsync(account.UserId);

        using var reset = await context.ResetPasswordAsync(account.Email);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(
            ["identity.lockout_cleared", "identity.password_reset"],
            await context.UserActionsAsync(account.UserId));
    }

    [Fact]
    public async Task AResetWithoutLockoutIsAuditedOnlyAsReset()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();

        using var reset = await context.ResetPasswordAsync(account.Email);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(["identity.password_reset"], await context.UserActionsAsync(account.UserId));
    }

    [Fact]
    public async Task RevokingASessionIsAuditedOnceWithTheSessionAsTarget()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var other = await context.Scenario.SignedInBrowserAsync(account);
        // Instantes de criação distintos: a ordem das sessões não pode depender do desempate por id.
        context.Time.Advance(TimeSpan.FromSeconds(1));
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        var otherSessionId = await context.LatestSessionIdAsync(account.UserId, skip: 1);

        using var first = await browser.DeleteAsync($"/api/v1/me/sessions/{otherSessionId}");
        using var second = await browser.DeleteAsync($"/api/v1/me/sessions/{otherSessionId}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var revocation = Assert.Single(await context.RowsAsync("identity.session_revoked"));
        Assert.Equal(("User", account.UserId, "login_session", otherSessionId), revocation);
    }

    [Fact]
    public async Task RevokingAnotherPersonsSessionIs404AndLeavesNoRecord()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var owner = await context.Scenario.CreateConfirmedAccountAsync();
        var stranger = await context.Scenario.CreateConfirmedAccountAsync();
        using var ownerBrowser = await context.Scenario.SignedInBrowserAsync(owner);
        using var strangerBrowser = await context.Scenario.SignedInBrowserAsync(stranger);
        var ownerSessionId = await context.LatestSessionIdAsync(owner.UserId, skip: 0);

        using var response = await strangerBrowser.DeleteAsync($"/api/v1/me/sessions/{ownerSessionId}");
        using var stillSignedIn = await ownerBrowser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        Assert.Empty(await context.RowsAsync("identity.session_revoked"));
    }

    [Fact]
    public async Task LogoutIsAuditedOnceAndLogoutWithoutSessionLeavesNoRecord()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        var sessionId = await context.LatestSessionIdAsync(account.UserId, skip: 0);

        using var logout = await browser.PostJsonAsync("/api/v1/auth/logout", body: null);
        using var again = await browser.PostJsonAsync("/api/v1/auth/logout", body: null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var row = Assert.Single(await context.RowsAsync("identity.logged_out"));
        Assert.Equal(("User", account.UserId, "login_session", sessionId), row);
    }

    [Fact]
    public async Task AnUnknownEmailLeavesNoRecordAndGetsTheSameAnswerAsAWrongPassword()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(context.Factory);
        using var wrongPassword = await IdentityScenario.LoginAsync(browser, account.Email, WrongPassword);
        var rowsBefore = await context.CountAllAuditRowsAsync();

        using var unknownEmail = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), WrongPassword);

        Assert.Equal(wrongPassword.StatusCode, unknownEmail.StatusCode);
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongPassword),
            await ProblemAssert.BodyWithoutTraceIdAsync(unknownEmail));
        Assert.Equal(rowsBefore, await context.CountAllAuditRowsAsync());
    }

    [Fact]
    public async Task NoAuditRowCarriesThePasswordTheEmailOrTheClientAddress()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        var canary = $"CANARY-{Guid.NewGuid():N}";
        using var browser = new Browser(context.Factory);
        using var failed = await IdentityScenario.LoginAsync(browser, account.Email, canary);
        using var succeeded = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        Assert.Equal(HttpStatusCode.NoContent, succeeded.StatusCode);
        Assert.Equal(0, await context.CountAuditRowsContainingAsync(canary));
        Assert.Equal(0, await context.CountAuditRowsContainingAsync(account.Email));
        Assert.Equal(0, await context.CountAuditRowsContainingAsync("127.0.0.1"));
        Assert.Equal(2, (await context.UserActionsAsync(account.UserId)).Count);
    }

    /// <summary>Banco só do teste, aplicação sobre ele e consultas à auditoria.</summary>
    private sealed class AuditContext : IAsyncDisposable
    {
        private readonly string _connectionString;

        private AuditContext(string connectionString)
        {
            _connectionString = connectionString;
            Factory = new DuoraWebFactory(connectionString) { Time = Time };
            Scenario = new IdentityScenario(connectionString, Factory);
        }

        public FakeTimeProvider Time { get; } = TestClock.StartingNow();

        public DuoraWebFactory Factory { get; }

        public IdentityScenario Scenario { get; }

        public static async Task<AuditContext> CreateAsync(PostgresFixture postgres) =>
            new(await postgres.CreateMigratedDatabaseAsync(Ct));

        public Task SetFailedAttemptsAsync(Guid userId, int count) =>
            ExecuteAsync("update identity.users set access_failed_count = $2 where id = $1", userId, count);

        public Task LockOutAsync(Guid userId) =>
            ExecuteAsync(
                "update identity.users set lockout_end = $2 where id = $1",
                userId,
                Time.GetUtcNow() + TimeSpan.FromMinutes(10));

        public async Task<HttpResponseMessage> ResetPasswordAsync(string email)
        {
            using var browser = new Browser(Factory);
            await browser.FetchCsrfTokenAsync();
            using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
            var link = await Scenario.ReadLatestLinkAsync(email, "PasswordReset");
            return await browser.PostJsonAsync(
                "/api/v1/auth/password-resets",
                new { userId = link.UserId, token = link.Token, newPassword = "uma frase nova e bem longa" });
        }

        /// <summary>Id da sessão ativa, da mais nova para a mais antiga, pulando <paramref name="skip"/>.</summary>
        public async Task<Guid> LatestSessionIdAsync(Guid userId, int skip)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                select id from identity.login_sessions
                where user_id = $1 and revoked_at is null
                order by created_at desc, id desc
                offset $2 limit 1
                """,
                connection);
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(skip);
            return (Guid)(await command.ExecuteScalarAsync(Ct) ?? throw new InvalidOperationException("Sem sessão."));
        }

        /// <summary>Ações em que a conta é ator e alvo, em ordem alfabética (o relógio falso não anda entre elas).</summary>
        public async Task<List<string>> UserActionsAsync(Guid userId)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                select action from ops.audit_events
                where actor_kind = 'User' and actor_id = $1 and target_type = 'user' and target_id = $1
                order by action
                """,
                connection);
            command.Parameters.AddWithValue(userId);
            var actions = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                actions.Add(reader.GetString(0));
            }

            return actions;
        }

        public async Task<List<(string ActorKind, Guid? ActorId, string TargetType, Guid TargetId)>> RowsAsync(string action)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                "select actor_kind, actor_id, target_type, target_id from ops.audit_events where action = $1 order by id",
                connection);
            command.Parameters.AddWithValue(action);
            var rows = new List<(string, Guid?, string, Guid)>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetGuid(3)));
            }

            return rows;
        }

        public async Task<long> CountAllAuditRowsAsync() => await ScalarAsync("select count(*) from ops.audit_events");

        public async Task<long> CountAuditRowsContainingAsync(string text) =>
            await ScalarAsync("select count(*) from ops.audit_events a where strpos(row_to_json(a)::text, $1) > 0", text);

        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        private async Task<long> ScalarAsync(string sql, params object[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            return (long)(await command.ExecuteScalarAsync(Ct) ?? 0L);
        }

        private async Task ExecuteAsync(string sql, params object[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        }

        private async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(Ct);
            return connection;
        }
    }
}
