using System.Diagnostics.Metrics;
using System.Net;
using Duora.Infrastructure.Identity;
using Duora.Web.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// TM-05: login, falha de senha (A04, A13, A16), início do bloqueio, tentativa durante o bloqueio, desbloqueio pelo
/// reset, logout e revogação de sessão deixam exatamente um registro em <c>ops.audit_events</c>, na transação do
/// efeito, também sob pedidos simultâneos. O registro só tem ator, ação e alvo: nada de e-mail, senha, IP ou user
/// agent. Login com e-mail sem conta ou não confirmado não grava linha (não há ator legítimo, e a tabela cresceria
/// sem limite): conta numa métrica e num log sem e-mail nem IP, o mesmo nos dois casos, e a resposta é a mesma da
/// senha errada (TM-06).
/// </summary>
/// <remarks>Cada teste usa um banco migrado só dele, para contar todas as linhas da tabela sem interferência.</remarks>
public sealed class LoginAuditTests(PostgresFixture postgres)
{
    private const string WrongPassword = "não é a senha da conta";
    private const string NewPassword = "uma frase nova e bem longa";
    private const string UnknownAccountLogMessage = "Login sem conta confirmada";
    private const string PasswordChanges = "/api/v1/me/password-changes";

    /// <summary>Trace id que um cliente mandaria no <c>traceparent</c> para escolher o <c>correlation_id</c>.</summary>
    private const string ForgedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

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
    public async Task AnUnknownEmailLeavesNoRowIsCountedAndGetsTheSameAnswerAsAWrongPassword()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(context.Factory);
        using var wrongPassword = await IdentityScenario.LoginAsync(browser, account.Email, WrongPassword);
        var rowsBefore = await context.CountAllAuditRowsAsync();
        var unknownEmail = IdentityScenario.NewEmail();
        using var counter = new UnknownAccountLoginCounter(context.Factory);

        using var response = await IdentityScenario.LoginAsync(browser, unknownEmail, WrongPassword);

        Assert.Equal(wrongPassword.StatusCode, response.StatusCode);
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongPassword),
            await ProblemAssert.BodyWithoutTraceIdAsync(response));
        Assert.Equal(rowsBefore, await context.CountAllAuditRowsAsync());
        Assert.Equal(1, counter.Count);
        Assert.Single(context.Logs.Entries, entry => entry.Text.Contains(UnknownAccountLogMessage, StringComparison.Ordinal));
        Assert.DoesNotContain(context.Logs.Entries, entry => entry.Text.Contains(unknownEmail, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnUnconfirmedAccountIsTreatedExactlyLikeAnUnknownEmail()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var pending = IdentityScenario.NewEmail();
        using var browser = new Browser(context.Factory);
        using (var registration = await IdentityScenario.RegisterAsync(browser, pending))
        {
            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        }

        var rowsBefore = await context.CountAllAuditRowsAsync();
        using var counter = new UnknownAccountLoginCounter(context.Factory);

        using var login = await IdentityScenario.LoginAsync(browser, pending, IdentityScenario.ValidPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(rowsBefore, await context.CountAllAuditRowsAsync());
        Assert.Equal(1, counter.Count);
        Assert.Single(context.Logs.Entries, entry => entry.Text.Contains(UnknownAccountLogMessage, StringComparison.Ordinal));
        Assert.DoesNotContain(context.Logs.Entries, entry => entry.Text.Contains(pending, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARightPasswordWithoutTheSecondFactorIsAuditedAsSecondFactorRequired()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using (var enrolling = await context.Scenario.SignedInBrowserAsync(account))
        {
            await MfaScenario.EnableAsync(enrolling, context.Time);
        }

        var before = await context.UserActionsAsync(account.UserId);
        using var browser = new Browser(context.Factory);

        using var response = await MfaScenario.LoginAsync(browser, account);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "MFA_REQUIRED");
        Assert.Equal(
            ["identity.login_second_factor_required"],
            Added(before, await context.UserActionsAsync(account.UserId)));
    }

    [Fact]
    public async Task ARightPasswordWithoutTheSecondFactorOnReauthenticationIsAudited()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, context.Time);
        var before = await context.UserActionsAsync(account.UserId);

        using var response = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            ["identity.reauthentication_second_factor_required"],
            Added(before, await context.UserActionsAsync(account.UserId)));
    }

    [Fact]
    public async Task TheCorrelationIdIsNotTakenFromTheClientsTraceparent()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(context.Factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = WrongPassword },
            new MutationHeaders(Extra: new Dictionary<string, string>
            {
                ["traceparent"] = $"00-{ForgedTraceId}-00f067aa0ba902b7-01",
                ["tracestate"] = "duora=forjado",
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var correlationId = await context.CorrelationIdOfAsync("identity.login_failed");
        Assert.NotNull(correlationId);
        Assert.NotEqual(ForgedTraceId, correlationId);
    }

    [Fact]
    public async Task AWrongPasswordOnReauthenticationIsAuditedAndTheLockoutIsOnTheRecord()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        await context.SetFailedAttemptsAsync(account.UserId, LockoutThreshold - 1);

        using var locking = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = WrongPassword });
        using var whileLocked = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, locking.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, whileLocked.StatusCode);
        Assert.Equal(
            [
                "identity.account_locked_out",
                "identity.login_succeeded",
                "identity.reauthentication_failed",
                "identity.reauthentication_refused_locked_out",
            ],
            await context.UserActionsAsync(account.UserId));
    }

    [Fact]
    public async Task AWrongCurrentPasswordOnPasswordChangeIsAuditedAndTheLockoutIsOnTheRecord()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        await context.SetFailedAttemptsAsync(account.UserId, LockoutThreshold - 1);

        using var locking = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = WrongPassword, newPassword = NewPassword });
        using var whileLocked = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, locking.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, whileLocked.StatusCode);
        Assert.Equal(
            [
                "identity.account_locked_out",
                "identity.login_succeeded",
                "identity.password_change_failed",
                "identity.password_change_refused_locked_out",
            ],
            await context.UserActionsAsync(account.UserId));
    }

    /// <summary>
    /// Duas falhas cabem antes do teto; as outras chegam com a conta já bloqueada. Com a conta travada durante a
    /// verificação, cada pedido vê o estado deixado pelo anterior: nenhum registro duplicado nem perdido.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrongPasswordsLeaveExactlyOneRecordEachAndOneLockout()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        await context.SetFailedAttemptsAsync(account.UserId, LockoutThreshold - 2);
        var browsers = Enumerable.Range(0, 6).Select(_ => new Browser(context.Factory)).ToList();

        var responses = await Task.WhenAll(browsers.Select(browser =>
            IdentityScenario.LoginAsync(browser, account.Email, WrongPassword)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(
            [
                "identity.account_locked_out",
                "identity.login_failed",
                "identity.login_failed",
                "identity.login_refused_locked_out",
                "identity.login_refused_locked_out",
                "identity.login_refused_locked_out",
                "identity.login_refused_locked_out",
            ],
            await context.UserActionsAsync(account.UserId));
        DisposeAll(responses, browsers);
    }

    [Fact]
    public async Task ConcurrentSuccessfulLoginsAreEachAuditedOnce()
    {
        await using var context = await AuditContext.CreateAsync(postgres);
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        var browsers = Enumerable.Range(0, 4).Select(_ => new Browser(context.Factory)).ToList();

        var responses = await Task.WhenAll(browsers.Select(browser =>
            IdentityScenario.LoginAsync(browser, account.Email, account.Password)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        Assert.Equal(Enumerable.Repeat("identity.login_succeeded", 4), await context.UserActionsAsync(account.UserId));
        DisposeAll(responses, browsers);
    }

    /// <summary>
    /// Outra transação segura a sessão; as revogações só seguem quando o banco mostra todas esperando pelo lock. Com
    /// a linha travada na leitura, quem vem depois do primeiro vê a sessão já revogada e não registra de novo.
    /// </summary>
    [Fact]
    public async Task RevocationsWaitingOnTheSameSessionAreAuditedOnce()
    {
        const int Revocations = 4;
        await using var context = await AuditContext.CreateAsync(postgres, accountLockTimeout: "00:00:30");
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        var sessionId = await context.LatestSessionIdAsync(account.UserId, skip: 0);
        bool[] results;
        await using (var holder = await context.HoldSessionAsync(sessionId))
        {
            var revocations = Enumerable.Range(0, Revocations)
                .Select(_ => Task.Run(() => context.RevokeOwnSessionAsync(account.UserId, sessionId)))
                .ToList();
            await context.WaitForSessionLockWaitersAsync(Revocations);
            await holder.ReleaseAsync();
            results = await Task.WhenAll(revocations);
        }

        Assert.All(results, Assert.True);
        var revocation = Assert.Single(await context.RowsAsync("identity.session_revoked"));
        Assert.Equal(("User", account.UserId, "login_session", sessionId), revocation);
    }

    [Fact]
    public async Task ARevocationThatWaitsTooLongForTheSessionAnswers503AndRecordsNothing()
    {
        await using var context = await AuditContext.CreateAsync(postgres, accountLockTimeout: "00:00:00.300");
        var account = await context.Scenario.CreateConfirmedAccountAsync();
        using var other = await context.Scenario.SignedInBrowserAsync(account);
        context.Time.Advance(TimeSpan.FromSeconds(1));
        using var browser = await context.Scenario.SignedInBrowserAsync(account);
        var otherSessionId = await context.LatestSessionIdAsync(account.UserId, skip: 1);

        await using (var holder = await context.HoldSessionAsync(otherSessionId))
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            using var response = await browser.DeleteAsync($"/api/v1/me/sessions/{otherSessionId}");

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");

            // Desistiu no teto (300 ms), não no timeout de 30 s do comando, que também viraria 503.
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"Esperou {waited.Elapsed}.");
        }

        using var stillSignedIn = await other.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        Assert.Empty(await context.RowsAsync("identity.session_revoked"));
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

    /// <summary>Ações gravadas depois de <paramref name="before"/>, que é prefixo multiconjunto de <paramref name="after"/>.</summary>
    private static List<string> Added(List<string> before, List<string> after)
    {
        var added = new List<string>(after);
        foreach (var action in before)
        {
            Assert.True(added.Remove(action), $"Registro {action} sumiu.");
        }

        return added;
    }

    private static void DisposeAll(HttpResponseMessage[] responses, List<Browser> browsers)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }

        browsers.ForEach(browser => browser.Dispose());
    }

    /// <summary>Transação de fora segurando uma linha até <see cref="ReleaseAsync"/>.</summary>
    private sealed class RowHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool _released;

        public async Task ReleaseAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await transaction.RollbackAsync(Ct);
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }

        public async ValueTask DisposeAsync() => await ReleaseAsync();
    }

    /// <summary>
    /// Conta as medições do contador de login sem conta confirmada desta aplicação (o escopo do meter é o
    /// <see cref="IMeterFactory"/> dela), sem somar as de outros testes rodando em paralelo.
    /// </summary>
    private sealed class UnknownAccountLoginCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public UnknownAccountLoginCounter(DuoraWebFactory factory)
        {
            var meters = factory.Services.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters)
                    && instrument.Meter.Name == LoginMetrics.MeterName
                    && instrument.Name == LoginMetrics.FailedUnknownAccount)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Banco só do teste, aplicação sobre ele e consultas à auditoria.</summary>
    private sealed class AuditContext : IAsyncDisposable
    {
        private readonly string _connectionString;

        private AuditContext(string connectionString, string? accountLockTimeout)
        {
            _connectionString = connectionString;
            // O log do login sem conta é Debug (a métrica faz a contagem); o teste confere o conteúdo dele.
            var settings = new Dictionary<string, string?>
            {
                [$"Logging:LogLevel:{typeof(CredentialVerifier).FullName}"] = "Debug",
            };
            if (accountLockTimeout is not null)
            {
                settings["Identity:AccountLockTimeout"] = accountLockTimeout;
            }

            Factory = new DuoraWebFactory(connectionString, Logs) { Time = Time, Settings = settings };
            Scenario = new IdentityScenario(connectionString, Factory);
        }

        public CapturingLoggerProvider Logs { get; } = new();

        public FakeTimeProvider Time { get; } = TestClock.StartingNow();

        public DuoraWebFactory Factory { get; }

        public IdentityScenario Scenario { get; }

        public static async Task<AuditContext> CreateAsync(PostgresFixture postgres, string? accountLockTimeout = null) =>
            new(await postgres.CreateMigratedDatabaseAsync(Ct), accountLockTimeout);

        /// <summary>Chama o serviço direto, num escopo próprio: sem sessão HTTP nem antiforgery para serializar.</summary>
        public async Task<bool> RevokeOwnSessionAsync(Guid userId, Guid sessionId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AccountSecurityService>()
                .RevokeOwnSessionAsync(userId, sessionId, Ct);
        }

        /// <summary>Outra transação com a sessão travada, como um pedido em andamento.</summary>
        public async Task<RowHolder> HoldSessionAsync(Guid sessionId)
        {
            var connection = await OpenAsync();
            var transaction = await connection.BeginTransactionAsync(Ct);
            await using var command = new NpgsqlCommand(
                "select 1 from identity.login_sessions where id = $1 for no key update",
                connection,
                transaction);
            command.Parameters.AddWithValue(sessionId);
            await command.ExecuteNonQueryAsync(Ct);
            return new RowHolder(connection, transaction);
        }

        /// <summary>Espera o banco mostrar <paramref name="count"/> comandos na sessão parados num lock.</summary>
        public async Task WaitForSessionLockWaitersAsync(int count)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (await ScalarAsync(
                       """
                       select count(*) from pg_stat_activity
                       where datname = current_database() and wait_event_type = 'Lock'
                         and query like '%identity.login_sessions%'
                       """) < count)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
        }

        public async Task<string?> CorrelationIdOfAsync(string action)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                "select correlation_id from ops.audit_events where action = $1",
                connection);
            command.Parameters.AddWithValue(action);
            return await command.ExecuteScalarAsync(Ct) as string;
        }

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
                new { userId = link.UserId, token = link.Token, newPassword = NewPassword });
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
