using System.Net;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Bootstrap administrativo (spec 11.1, D4): <c>duora-web admin grant</c> cria a conta privilegiada com uma senha
/// temporária mostrada uma vez, que expira em 24 h e só serve para trocá-la; depois da troca, a sessão é a restrita
/// do cadastro de MFA. Pré-condições I5 e TM-66 de auth.md. A senha gerada é o canário: ela só aparece na saída do
/// comando, nunca em log, banco, outbox ou arquivo.
/// </summary>
public sealed class AdminBootstrapTests : IAsyncDisposable
{
    private const string Operator = "ana.ops";
    private const string NewPassword = "uma frase nova e longa para o admin";
    private const string ChangePassword = "/api/v1/me/password-changes";

    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _time = TestClock.StartingNow();
    private readonly DuoraWebFactory _factory;

    public AdminBootstrapTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GrantCreatesAConfirmedPrivilegedAccountThatMustChangeItsPasswordWithin24Hours()
    {
        var email = IdentityScenario.NewEmail();

        var grant = await GrantAsync(email, AccountRoles.ProductAdmin);

        Assert.Equal(0, grant.ExitCode);
        Assert.Equal([AccountRoles.ProductAdmin], await RolesAsync(grant.UserId));
        Assert.Equal(1L, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and email_confirmed and not two_factor_enabled", grant.UserId));
        Assert.Equal(_time.GetUtcNow().AddHours(24), await TemporaryPasswordExpiryAsync(grant.UserId));
    }

    [Fact]
    public async Task GrantIsAuditedWithTheOperatorWhoRanIt()
    {
        var grant = await GrantAsync(IdentityScenario.NewEmail());

        Assert.Equal(1L, await Scenario.ScalarAsync(
            """
            select count(*) from ops.audit_events
            where action = 'identity.admin_granted' and actor_kind = 'Operator' and actor_id is null
              and actor_label = $1 and target_type = 'user' and target_id = $2
            """,
            Operator,
            grant.UserId));
    }

    [Fact]
    [Trait("Threat", "TM-05")]
    public async Task TheTemporaryPasswordAppearsOnlyOnceInTheOperatorTerminal()
    {
        var email = IdentityScenario.NewEmail();

        var grant = await GrantAsync(email);
        using (var browser = new Browser(_factory))
        {
            using var login = await IdentityScenario.LoginAsync(browser, email, grant.Password);
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        Assert.Equal(1, Occurrences(grant.Output, grant.Password));
        Assert.DoesNotContain(grant.Password, grant.Error, StringComparison.Ordinal);
        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(grant.Password, StringComparison.Ordinal));
        Assert.Equal(0L, await RowsContainingAsync(grant.Password));
        Assert.Empty(FilesContaining(grant.Password, _factory.MediaRoot, KeyRingDirectory()));
    }

    [Fact]
    public async Task EachGrantGeneratesADifferentStrongPassword()
    {
        var first = await GrantAsync(IdentityScenario.NewEmail());
        var second = await GrantAsync(IdentityScenario.NewEmail());

        Assert.NotEqual(first.Password, second.Password);
        Assert.True(first.Password.Length >= 24, $"Senha temporária com {first.Password.Length} caracteres.");
    }

    [Fact]
    public async Task AnExpiredTemporaryPasswordIsRefusedLikeAWrongOne()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        _time.Advance(TimeSpan.FromHours(24));
        using var browser = new Browser(_factory);

        using var login = await IdentityScenario.LoginAsync(browser, email, grant.Password);

        await ProblemAssert.IsProblemAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task ASessionOpenedWithTheTemporaryPasswordEndsWhenThePasswordExpires()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(58));
        using var browser = await SignedInAsync(email, grant.Password);
        _time.Advance(TimeSpan.FromMinutes(2));

        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = grant.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }

    [Fact]
    public async Task TheFirstSessionOnlyAllowsChangingThePassword()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using var browser = await SignedInAsync(email, grant.Password);
        var otherRoutes = Routes()
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(route => route.Name != $"POST {ChangePassword}")
            .ToList();

        Assert.NotEmpty(otherRoutes);
        foreach (var route in otherRoutes)
        {
            using var response = await CallAsync(browser, route.Method, route.Path);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route.Name} respondeu {(int)response.StatusCode}.");
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED");
        }
    }

    [Fact]
    public async Task ChangingTheTemporaryPasswordLeadsToTheMfaEnrollmentSessionOnly()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using var browser = await SignedInAsync(email, grant.Password);

        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = grant.Password, newPassword = NewPassword });
        await browser.FetchCsrfTokenAsync();
        using var me = await browser.GetAsync("/api/v1/me");
        using var sessions = await browser.GetAsync("/api/v1/me/sessions");
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        await ProblemAssert.IsProblemAsync(sessions, HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED");
        Assert.NotEmpty(sharedKey);
        Assert.Null(await TemporaryPasswordExpiryAsync(grant.UserId));
    }

    [Fact]
    public async Task TheTemporaryPasswordStopsWorkingOnceChanged()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using (var browser = await SignedInAsync(email, grant.Password))
        {
            using var change = await browser.PostJsonAsync(
                ChangePassword,
                new { currentPassword = grant.Password, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        using var again = new Browser(_factory);
        using var withTemporary = await IdentityScenario.LoginAsync(again, email, grant.Password);
        using var withNew = await IdentityScenario.LoginAsync(again, email, NewPassword);

        await ProblemAssert.IsProblemAsync(withTemporary, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, withNew.StatusCode);
    }

    [Fact]
    public async Task TheTemporaryPasswordCannotBeKeptAsTheNewOne()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using var browser = await SignedInAsync(email, grant.Password);

        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = grant.Password, newPassword = grant.Password });

        await ProblemAssert.IsProblemAsync(change, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.NotNull(await TemporaryPasswordExpiryAsync(grant.UserId));
    }

    [Fact]
    [Trait("Threat", "TM-22")]
    public async Task APasswordResetRequestSendsNoLinkBeforeTheFirstMfaActivation()
    {
        var email = IdentityScenario.NewEmail();
        await GrantAsync(email);
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Empty(await Scenario.ReadLinksAsync(email, "PasswordReset"));
        Assert.Single(await Scenario.ReadLinksAsync(email, "PasswordResetUnavailable"));
    }

    [Fact]
    [Trait("Threat", "TM-22")]
    public async Task APasswordResetTokenIsRefusedBeforeTheFirstMfaActivation()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        var token = await ResetLinkTokenAsync(grant.UserId);
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = grant.UserId, token, newPassword = NewPassword });
        using var login = await IdentityScenario.LoginAsync(browser, email, grant.Password);

        await ProblemAssert.IsProblemAsync(reset, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    [Trait("Threat", "TM-66")]
    public async Task APublicRegistrationWithTheSameEmailDoesNotTouchTheAccount()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        var before = await AccountRowAsync(grant.UserId);
        using var browser = new Browser(_factory);

        using var registered = await IdentityScenario.RegisterAsync(browser, email, "senha do atacante bem longa");
        var after = await AccountRowAsync(grant.UserId);
        using var login = await IdentityScenario.LoginAsync(browser, email, grant.Password);

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.Equal(before, after);
        Assert.Equal([AccountRoles.Moderator], await RolesAsync(grant.UserId));
        Assert.Equal(0L, await Scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1", grant.UserId));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    [Trait("Threat", "TM-66")]
    public async Task AnAccountWithATemporaryPasswordIsAlwaysConfirmedSoTheUpsertCannotReplaceIt()
    {
        var grant = await GrantAsync(IdentityScenario.NewEmail());
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "update identity.users set email_confirmed = false where id = $1",
            connection);
        command.Parameters.AddWithValue(grant.UserId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_users_temporary_password_requires_confirmed_email", error.ConstraintName);
    }

    [Fact]
    public async Task AConfirmedParticipantIsNeverPromoted()
    {
        var participant = await Scenario.CreateConfirmedAccountAsync();

        var grant = await RunAsync("admin", "grant", "--email", participant.Email, "--role", "moderator", "--operator", Operator);

        Assert.Equal(AdminCommand.RefusedExitCode, grant.ExitCode);
        Assert.Contains("já existe", grant.Error, StringComparison.Ordinal);
        Assert.Equal([AccountRoles.Participant], await RolesAsync(participant.UserId));
        Assert.Equal(0L, await AdminGrantedEventsAsync(participant.UserId));
        using var browser = await SignedInAsync(participant.Email, participant.Password);
    }

    [Fact]
    public async Task APendingParticipantRegistrationIsNeverPromotedNorReplaced()
    {
        var email = IdentityScenario.NewEmail();
        using (var browser = new Browser(_factory))
        {
            using var registered = await IdentityScenario.RegisterAsync(browser, email);
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        }

        var userId = (await Scenario.ReadLatestLinkAsync(email, "EmailConfirmation")).UserId;
        var before = await AccountRowAsync(userId);

        var grant = await RunAsync("admin", "grant", "--email", email, "--role", "operator", "--operator", Operator);

        Assert.Equal(AdminCommand.RefusedExitCode, grant.ExitCode);
        Assert.Equal(before, await AccountRowAsync(userId));
        Assert.Equal([AccountRoles.Participant], await RolesAsync(userId));
    }

    [Fact]
    public async Task AnUnusedBootstrapGetsANewTemporaryPasswordAndTheOldOneStopsWorking()
    {
        var email = IdentityScenario.NewEmail();
        var first = await GrantAsync(email);
        _time.Advance(TimeSpan.FromHours(30));

        var second = await GrantAsync(email);
        using var browser = new Browser(_factory);
        using var withOld = await IdentityScenario.LoginAsync(browser, email, first.Password);
        using var withNew = await IdentityScenario.LoginAsync(browser, email, second.Password);

        Assert.Equal(first.UserId, second.UserId);
        Assert.Contains("reemitida", second.Output, StringComparison.Ordinal);
        await ProblemAssert.IsProblemAsync(withOld, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, withNew.StatusCode);
        Assert.Equal(_time.GetUtcNow().AddHours(24), await TemporaryPasswordExpiryAsync(first.UserId));
        Assert.Equal(2L, await AdminGrantedEventsAsync(first.UserId));
    }

    [Fact]
    public async Task ReissuingEndsTheSessionsOpenedWithThePreviousPassword()
    {
        var email = IdentityScenario.NewEmail();
        var first = await GrantAsync(email);
        using var browser = await SignedInAsync(email, first.Password);

        await GrantAsync(email);
        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = first.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }

    [Fact]
    public async Task ABootstrapIsNotReissuedUnderAnotherRole()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email, AccountRoles.Moderator);
        var before = await AccountRowAsync(grant.UserId);

        var again = await RunAsync("admin", "grant", "--email", email, "--role", "operator", "--operator", Operator);

        Assert.Equal(AdminCommand.RefusedExitCode, again.ExitCode);
        Assert.Equal(before, await AccountRowAsync(grant.UserId));
        Assert.Equal([AccountRoles.Moderator], await RolesAsync(grant.UserId));
    }

    [Fact]
    public async Task AnActivatedPrivilegedAccountIsNotReissued()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using (var browser = await SignedInAsync(email, grant.Password))
        {
            using var change = await browser.PostJsonAsync(
                ChangePassword,
                new { currentPassword = grant.Password, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        var before = await AccountRowAsync(grant.UserId);

        var again = await RunAsync("admin", "grant", "--email", email, "--role", "moderator", "--operator", Operator);

        Assert.Equal(AdminCommand.RefusedExitCode, again.ExitCode);
        Assert.Equal(before, await AccountRowAsync(grant.UserId));
    }

    [Theory]
    [InlineData("admin", "grant", "--role", "moderator", "--operator", Operator)]
    [InlineData("admin", "grant", "--email", "x@example.com", "--operator", Operator)]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "participant", "--operator", Operator)]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "Moderator", "--operator", Operator)]
    [InlineData("admin", "grant", "--email", "não é e-mail", "--role", "moderator", "--operator", Operator)]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", "Ana Silva")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", "")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", "ana.ops\n")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", Operator, "--extra", "1")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--email", "y@example.com", "--role", "moderator", "--operator", Operator)]
    [InlineData("admin", "revoke", "--email", "x@example.com")]
    [InlineData("admin")]
    public async Task InvalidArgumentsChangeNothingAndExitWithUsage(params string[] args)
    {
        var grantsBefore = await GrantsByThisOperatorAsync();

        var result = await RunAsync(args);

        Assert.Equal(AdminCommand.UsageExitCode, result.ExitCode);
        Assert.Contains("admin grant --email", result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.Output);
        Assert.Equal(grantsBefore, await GrantsByThisOperatorAsync());
        Assert.Equal(0L, await Scenario.ScalarAsync(
            "select count(*) from identity.users where normalized_email in ('X@EXAMPLE.COM', 'Y@EXAMPLE.COM')"));
    }

    [Theory]
    [InlineData(new[] { "admin", "grant" }, true)]
    [InlineData(new[] { "admin" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--urls", "http://+:8080" }, false)]
    public void OnlyTheAdminVerbRunsTheCommandInsteadOfTheServer(string[] args, bool expected) =>
        Assert.Equal(expected, AdminCommand.IsInvocation(args));

    private sealed record GrantResult(int ExitCode, string Output, string Error, Guid UserId, string Password);

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private async Task<GrantResult> GrantAsync(string email, string role = AccountRoles.Moderator)
    {
        var result = await RunAsync("admin", "grant", "--email", email, "--role", role, "--operator", Operator);
        Assert.True(result.ExitCode == 0, $"Saída {result.ExitCode}: {result.Error}");
        var password = result.Output.Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith(AdminCommand.PasswordLinePrefix, StringComparison.Ordinal))
            [AdminCommand.PasswordLinePrefix.Length..];
        var userId = await Scenario.ScalarTextAsync(
            "select id::text from identity.users where normalized_email = upper($1)", email);
        return new GrantResult(result.ExitCode, result.Output, result.Error, Guid.Parse(userId), password);
    }

    private async Task<CommandResult> RunAsync(params string[] args)
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var exitCode = await AdminCommand.ExecuteAsync(_factory.Services, args, output, error, Ct);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private async Task<Browser> SignedInAsync(string email, string password)
    {
        var browser = new Browser(_factory);
        using var login = await IdentityScenario.LoginAsync(browser, email, password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return browser;
    }

    private async Task<string> ResetLinkTokenAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DuoraUser>>();
        var user = await users.FindByIdAsync(userId.ToString("D")) ?? throw new InvalidOperationException("Conta sumiu.");
        var token = await users.GeneratePasswordResetTokenAsync(user);
        return System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(token));
    }

    private async Task<IReadOnlyList<string>> RolesAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select r.name from identity.user_roles ur join identity.roles r on r.id = ur.role_id
            where ur.user_id = $1 order by r.name
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var roles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    private async Task<DateTimeOffset?> TemporaryPasswordExpiryAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select temporary_password_expires_at from identity.users where id = $1",
            connection);
        command.Parameters.AddWithValue(userId);
        return await command.ExecuteScalarAsync(Ct) is DateTime value ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    /// <summary>A linha da conta inteira como texto: qualquer mudança feita pelo cadastro aparece na comparação.</summary>
    private Task<string> AccountRowAsync(Guid userId) =>
        Scenario.ScalarTextAsync("select u::text from identity.users u where id = $1", userId);

    /// <summary>Concessões auditadas com o operador destes testes, que só esta classe usa (o banco é da suíte toda).</summary>
    private Task<long> GrantsByThisOperatorAsync() =>
        Scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = 'identity.admin_granted' and actor_label = $1",
            Operator);

    private Task<long> AdminGrantedEventsAsync(Guid userId) =>
        Scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = 'identity.admin_granted' and target_id = $1",
            userId);

    /// <summary>Procura a senha em todo texto que o comando grava: conta, papéis, auditoria e outbox.</summary>
    private Task<long> RowsContainingAsync(string secret) =>
        Scenario.ScalarAsync(
            """
            select (select count(*) from identity.users u where strpos(u::text, $1) > 0)
                 + (select count(*) from identity.user_tokens t where strpos(t::text, $1) > 0)
                 + (select count(*) from ops.audit_events a where strpos(a::text, $1) > 0)
                 + (select count(*) from ops.outbox_events o where strpos(o::text, $1) > 0)
            """,
            secret);

    private string KeyRingDirectory() =>
        _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["DataProtection:KeysDirectory"]
        ?? throw new InvalidOperationException("Key ring sem diretório.");

    private static List<string> FilesContaining(string secret, params string[] directories) =>
        directories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains(secret, StringComparison.Ordinal))
            .ToList();

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static Task<HttpResponseMessage> CallAsync(Browser browser, string method, string routePath)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(routePath, "{[^}]+}", Guid.NewGuid().ToString());
        return method switch
        {
            "GET" => browser.GetAsync(path),
            "DELETE" => browser.DeleteAsync(path),
            "POST" => browser.PostJsonAsync(path, new { }),
            "PUT" => browser.PutAsync(path, "{}"),
            "PATCH" => browser.PatchRawJsonAsync(path, "{}", ifMatch: "\"1\""),
            _ => throw new InvalidOperationException($"Método {method} sem caso no teste."),
        };
    }

    private IEnumerable<(string Name, string Method, string Path, RouteEndpoint Endpoint)> Routes()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                yield return ($"{method} {path}", method, path, endpoint);
            }
        }
    }
}
