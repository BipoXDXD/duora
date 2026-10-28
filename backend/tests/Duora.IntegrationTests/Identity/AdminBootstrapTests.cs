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
/// do cadastro de MFA. <c>--reissue</c> emite outra senha para a conta do mesmo papel que ainda não ativou o MFA e
/// avisa o titular (TM-21). Pré-condições I5 e TM-66 de auth.md. A senha gerada é o canário: ela só aparece na saída
/// do comando, nunca em log, banco, outbox ou arquivo.
/// </summary>
public sealed class AdminBootstrapTests : IAsyncDisposable
{
    /// <summary>
    /// Exclusivo desta classe: os testes contam eventos de auditoria pelo rótulo do operador no banco compartilhado,
    /// e outras classes (CredentialChangeRollbackTests, AuditTrailTests) gravam com "ana.ops" em paralelo.
    /// </summary>
    private const string Operator = "bootstrap.tests";
    private const string NewPassword = "uma frase nova e longa para o admin";
    private const string ChangePassword = "/api/v1/me/password-changes";
    private const string CrockfordSymbols = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

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

    [Theory]
    [InlineData(AccountRoles.Moderator, "identity.admin_granted.moderator")]
    [InlineData(AccountRoles.ProductAdmin, "identity.admin_granted.product_admin")]
    [InlineData(AccountRoles.Operator, "identity.admin_granted.operator")]
    public async Task GrantIsAuditedPerRoleWithTheLabelOfWhoRanIt(string role, string action)
    {
        var grant = await GrantAsync(IdentityScenario.NewEmail(), role);

        Assert.Equal(1L, await Scenario.ScalarAsync(
            """
            select count(*) from ops.audit_events
            where action = $1 and actor_kind = 'TerminalOperator' and actor_id is null
              and actor_label = $2 and target_type = 'user' and target_id = $3
            """,
            action,
            Operator,
            grant.UserId));
        Assert.Equal(1L, await AuditEventsAsync(grant.UserId));
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

        await AssertSecretOnlyInOutputAsync(grant);
    }

    [Fact]
    [Trait("Threat", "TM-05")]
    public async Task AReissuedPasswordAppearsOnlyInTheOperatorTerminalNotInTheHoldersNotice()
    {
        var email = IdentityScenario.NewEmail();
        await GrantAsync(email);

        var reissue = await ReissueAsync(email);

        Assert.Single(await Scenario.ReadLinksAsync(email, "PasswordResetNotice"));
        await AssertSecretOnlyInOutputAsync(reissue);
    }

    [Fact]
    public async Task EachGrantGeneratesADifferentPasswordOf24CrockfordSymbols()
    {
        var first = await GrantAsync(IdentityScenario.NewEmail());
        var second = await GrantAsync(IdentityScenario.NewEmail());

        Assert.NotEqual(first.Password, second.Password);
        Assert.Equal(24, first.Password.Length);
        Assert.All(first.Password, symbol => Assert.Contains(symbol, CrockfordSymbols));
    }

    [Fact]
    public async Task TheTemporaryPasswordIsAcceptedInLowercaseAndWithLookalikeLetters()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        var typed = $" {grant.Password.ToLowerInvariant().Replace('0', 'o').Replace('1', 'l')} ";

        using var browser = await SignedInAsync(email, typed);
        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = typed, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
    }

    [Fact]
    public async Task AnOwnPasswordIsNotNormalizedLikeTheTemporaryOne()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        await ChangeTemporaryPasswordAsync(email, grant.Password);
        using var browser = new Browser(_factory);

        using var login = await IdentityScenario.LoginAsync(browser, email, NewPassword.ToUpperInvariant());

        await ProblemAssert.IsProblemAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task AnExpiredTemporaryPasswordIsRefusedAndAuditedLikeAWrongOne()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        _time.Advance(TimeSpan.FromHours(24));
        using var browser = new Browser(_factory);

        using var login = await IdentityScenario.LoginAsync(browser, email, grant.Password);

        await ProblemAssert.IsProblemAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1L, await Scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = 'identity.login_failed' and target_id = $1",
            grant.UserId));
        Assert.Equal(0L, await Scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where user_id = $1", grant.UserId));
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
    public async Task AfterTheRecentAuthenticationWindowTheTemporarySessionNeedsANewLoginToChangeThePassword()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using var browser = await SignedInAsync(email, grant.Password);
        await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);

        using var stale = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = grant.Password, newPassword = NewPassword });
        using var logout = await browser.PostJsonAsync("/api/v1/auth/logout", body: null);
        using var again = await SignedInAsync(email, grant.Password);
        using var change = await again.PostJsonAsync(
            ChangePassword,
            new { currentPassword = grant.Password, newPassword = NewPassword });

        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
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
        Assert.Single(await Scenario.ReadLinksAsync(email, "PasswordChangedNotice"));
    }

    [Fact]
    public async Task TheTemporaryPasswordStopsWorkingOnceChanged()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        await ChangeTemporaryPasswordAsync(email, grant.Password);

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
            new { currentPassword = grant.Password, newPassword = grant.Password.ToLowerInvariant() });

        var problem = await ProblemAssert.IsProblemAsync(change, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("SAME_AS_TEMPORARY_PASSWORD", (string?)problem["errors"]?["newPassword"]?[0]);
        Assert.NotNull(await TemporaryPasswordExpiryAsync(grant.UserId));
    }

    [Fact]
    public async Task AWrongCurrentPasswordIsRefusedAndCountedBeforeTheReuseCheck()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using var browser = await SignedInAsync(email, grant.Password);
        const string Guess = "um palpite qualquer bem longo";

        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = Guess, newPassword = Guess });

        await ProblemAssert.IsProblemAsync(change, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1L, await Scenario.ScalarAsync(
            "select access_failed_count from identity.users where id = $1", grant.UserId));
        Assert.Equal(1L, await Scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = 'identity.password_change_failed' and target_id = $1",
            grant.UserId));
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
    [Trait("Threat", "TM-22")]
    public async Task APasswordResetStaysBlockedAfterTheTemporaryPasswordIsChangedButBeforeMfa()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using (await ChangeTemporaryPasswordAsync(email, grant.Password))
        {
        }

        var token = await ResetLinkTokenAsync(grant.UserId);
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });
        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = grant.UserId, token, newPassword = "a senha que o atacante queria" });
        using var login = await IdentityScenario.LoginAsync(browser, email, NewPassword);

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Empty(await Scenario.ReadLinksAsync(email, "PasswordReset"));
        Assert.Single(await Scenario.ReadLinksAsync(email, "PasswordResetUnavailable"));
        await ProblemAssert.IsProblemAsync(reset, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task AChangeWithAnExpiredTemporaryPasswordIsRefusedEvenInsideTheService()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        var before = await AccountRowAsync(grant.UserId);
        _time.Advance(TimeSpan.FromHours(24));
        await using var scope = _factory.Services.CreateAsyncScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();

        var change = await security.ChangePasswordAsync(
            grant.UserId,
            grant.Password,
            NewPassword,
            Duora.Core.Notifications.EmailLanguage.BrazilianPortuguese,
            Ct);

        Assert.IsType<PasswordChange.WrongPassword>(change);
        Assert.Equal(before, await AccountRowAsync(grant.UserId));
        Assert.Empty(await Scenario.ReadLinksAsync(email, "PasswordChangedNotice"));
    }

    [Fact]
    public async Task MfaCannotBeConfirmedWhileThePasswordIsTemporary()
    {
        var grant = await GrantAsync(IdentityScenario.NewEmail());
        await using var scope = _factory.Services.CreateAsyncScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();

        var activation = await security.ConfirmMfaEnrollmentAsync(
            grant.UserId,
            "000000",
            Duora.Core.Notifications.EmailLanguage.BrazilianPortuguese,
            Ct);

        Assert.IsType<MfaActivation.PasswordChangeRequired>(activation);
        Assert.Equal(0L, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and two_factor_enabled", grant.UserId));
    }

    [Fact]
    public void TheIssuedCredentialAndThePendingIssueNeverPrintTheSecret()
    {
        const string Secret = "CANARY0SECRET0PASSWORD00";
        const string Hash = "AQAAAAIAAYagAAAAEcanaryhash";
        var issued = new AdministratorCredential.Issued(Guid.CreateVersion7(), Secret, DateTimeOffset.UnixEpoch);
        Assert.True(EmailAddress.TryParse("ana@example.com", out var email));
        var issue = new TemporaryIssue(email, "ANA@EXAMPLE.COM", AccountRoles.Moderator, Secret, Hash, default, default);

        Assert.DoesNotContain(Secret, issued.ToString(), StringComparison.Ordinal);
        Assert.Contains(issued.UserId.ToString(), issued.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, issue.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Hash, issue.ToString(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfirmedParticipantIsNeverPromotedNorReissued(bool reissue)
    {
        var participant = await Scenario.CreateConfirmedAccountAsync();
        var before = await AccountRowAsync(participant.UserId);

        var grant = await RunAsync(GrantArguments(participant.Email, AccountRoles.Moderator, reissue));

        Assert.Equal(AdminCommand.RefusedExitCode, grant.ExitCode);
        Assert.Equal("", grant.Output);
        Assert.Equal(before, await AccountRowAsync(participant.UserId));
        Assert.Equal([AccountRoles.Participant], await RolesAsync(participant.UserId));
        Assert.Equal(0L, await AuditEventsByThisOperatorAsync(participant.UserId));
        using var browser = await SignedInAsync(participant.Email, participant.Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APendingParticipantRegistrationIsNeverPromotedNorReplaced(bool reissue)
    {
        var email = IdentityScenario.NewEmail();
        using (var browser = new Browser(_factory))
        {
            using var registered = await IdentityScenario.RegisterAsync(browser, email);
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        }

        var userId = (await Scenario.ReadLatestLinkAsync(email, "EmailConfirmation")).UserId;
        var before = await AccountRowAsync(userId);

        var grant = await RunAsync(GrantArguments(email, AccountRoles.Operator, reissue));

        Assert.Equal(AdminCommand.RefusedExitCode, grant.ExitCode);
        Assert.Equal(before, await AccountRowAsync(userId));
        Assert.Equal([AccountRoles.Participant], await RolesAsync(userId));
    }

    [Fact]
    public async Task AnExistingBootstrapIsRefusedWithoutTheReissueFlag()
    {
        var email = IdentityScenario.NewEmail();
        var first = await GrantAsync(email);
        var before = await AccountRowAsync(first.UserId);

        var again = await RunAsync(GrantArguments(email, AccountRoles.Moderator, reissue: false));
        using var browser = new Browser(_factory);
        using var withFirst = await IdentityScenario.LoginAsync(browser, email, first.Password);

        Assert.Equal(AdminCommand.RefusedExitCode, again.ExitCode);
        Assert.Contains("--reissue", again.Error, StringComparison.Ordinal);
        Assert.Equal("", again.Output);
        Assert.Equal(before, await AccountRowAsync(first.UserId));
        Assert.Equal(HttpStatusCode.NoContent, withFirst.StatusCode);
    }

    [Fact]
    public async Task TwoSimultaneousGrantsOfTheSameEmailCreateOneAccount()
    {
        var email = IdentityScenario.NewEmail();
        using var barrier = new InsertBarrier(participants: 2);
        await using var factory = new DuoraWebFactory(_postgres.ConnectionString)
        {
            Time = _time,
            TestServices = services => services.ConfigureDbContext<Duora.Infrastructure.Persistence.DuoraDbContext>(
                options => options.AddInterceptors(barrier)),
        };

        var results = await Task.WhenAll(
            RunAsync(factory, GrantArguments(email, AccountRoles.Moderator, reissue: false)),
            RunAsync(factory, GrantArguments(email, AccountRoles.Moderator, reissue: false)));

        Assert.True(barrier.Overlapped, "Os dois INSERT não chegaram juntos ao banco.");

        Assert.Equal([0, AdminCommand.RefusedExitCode], results.Select(result => result.ExitCode).Order());
        Assert.Equal(1L, await Scenario.ScalarAsync(
            "select count(*) from identity.users where normalized_email = upper($1)", email));
        var userId = Guid.Parse(await Scenario.ScalarTextAsync(
            "select id::text from identity.users where normalized_email = upper($1)", email));
        Assert.Equal(1L, await AuditEventsAsync(userId));
    }

    [Fact]
    public async Task ReissueGivesANewTemporaryPasswordAndTheOldOneStopsWorking()
    {
        var email = IdentityScenario.NewEmail();
        var first = await GrantAsync(email);
        _time.Advance(TimeSpan.FromHours(30));

        var second = await ReissueAsync(email);
        using var browser = new Browser(_factory);
        using var withOld = await IdentityScenario.LoginAsync(browser, email, first.Password);
        using var withNew = await IdentityScenario.LoginAsync(browser, email, second.Password);

        Assert.Equal(first.UserId, second.UserId);
        Assert.NotEqual(first.Password, second.Password);
        Assert.Contains("reemitida", second.Output, StringComparison.Ordinal);
        await ProblemAssert.IsProblemAsync(withOld, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, withNew.StatusCode);
        Assert.Equal(_time.GetUtcNow().AddHours(24), await TemporaryPasswordExpiryAsync(first.UserId));
        Assert.Equal(1L, await Scenario.ScalarAsync(
            """
            select count(*) from ops.audit_events
            where action = 'identity.admin_temporary_password_reissued' and actor_kind = 'TerminalOperator'
              and actor_label = $1 and target_id = $2
            """,
            Operator,
            first.UserId));
    }

    [Fact]
    [Trait("Threat", "TM-21")]
    public async Task ReissueNotifiesTheHolderThatThePasswordWasReset()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        Assert.Empty(await Scenario.ReadLinksAsync(email, "PasswordResetNotice"));

        await ReissueAsync(email);

        var notice = Assert.Single(await Scenario.ReadLinksAsync(email, "PasswordResetNotice"));
        Assert.Equal(grant.UserId, notice.UserId);
        Assert.Equal("", notice.Token);
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("en-US", "en-US")]
    [InlineData(null, "en-US")]
    public async Task ReissueSnapshotsTheStoredLocaleWithoutAnHttpRequest(string? locale, string expected)
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand(
            "update identity.users set preferred_locale = $1 where id = $2", connection);
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)locale ?? DBNull.Value });
        update.Parameters.AddWithValue(grant.UserId);
        await update.ExecuteNonQueryAsync(Ct);

        await ReissueAsync(email);

        var payload = await Scenario.ScalarTextAsync(
            "select payload::text from ops.outbox_events where payload->>'userId' = $1 and payload->>'kind' = 'PasswordResetNotice'",
            grant.UserId.ToString("D"));
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal(expected, document.RootElement.GetProperty("language").GetString());
        update.Parameters[0].Value = expected == "pt-BR" ? "en-US" : "pt-BR";
        await update.ExecuteNonQueryAsync(Ct);
        Assert.Equal(payload, await Scenario.ScalarTextAsync(
            "select payload::text from ops.outbox_events where payload->>'userId' = $1 and payload->>'kind' = 'PasswordResetNotice'",
            grant.UserId.ToString("D")));
    }

    [Fact]
    public async Task ReissueWaitsForAConcurrentLocaleChangeAndCapturesTheCommittedValue()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var update = new NpgsqlCommand(
            "update identity.users set preferred_locale = 'pt-BR' where id = $1", connection, transaction);
        update.Parameters.AddWithValue(grant.UserId);
        await update.ExecuteNonQueryAsync(Ct);
        var reissuing = ReissueAsync(email);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (await Scenario.ScalarAsync(
            "select count(*) from pg_stat_activity where $1 = any(pg_blocking_pids(pid))", connection.ProcessID) == 0)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.False(reissuing.IsCompleted);
        await transaction.CommitAsync(Ct);
        await reissuing.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal("pt-BR", await Scenario.ScalarTextAsync(
            "select payload->>'language' from ops.outbox_events where payload->>'userId' = $1 and payload->>'kind' = 'PasswordResetNotice'",
            grant.UserId.ToString("D")));
    }

    [Fact]
    public async Task ReissuingEndsTheSessionsOpenedWithThePreviousPassword()
    {
        var email = IdentityScenario.NewEmail();
        var first = await GrantAsync(email);
        using var browser = await SignedInAsync(email, first.Password);

        await ReissueAsync(email);
        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = first.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
        Assert.Equal(0L, await Scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where user_id = $1 and revoked_at is null", first.UserId));
    }

    [Fact]
    public async Task ReissueAfterAPasswordChangeWithoutMfaRestartsTheTemporaryPhase()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using var browser = await ChangeTemporaryPasswordAsync(email, grant.Password);

        var reissue = await ReissueAsync(email);
        using var me = await browser.GetAsync("/api/v1/me");
        using var again = new Browser(_factory);
        using var withOwn = await IdentityScenario.LoginAsync(again, email, NewPassword);

        Assert.Equal(0, reissue.ExitCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(_time.GetUtcNow().AddHours(24), await TemporaryPasswordExpiryAsync(grant.UserId));
        await ProblemAssert.IsProblemAsync(withOwn, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        using var restricted = await SignedInAsync(email, reissue.Password);
        using var sessions = await restricted.GetAsync("/api/v1/me/sessions");
        await ProblemAssert.IsProblemAsync(sessions, HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED");
    }

    [Fact]
    public async Task AnAccountWithMfaIsNotReissued()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email);
        using (var browser = await ChangeTemporaryPasswordAsync(email, grant.Password))
        {
            await browser.FetchCsrfTokenAsync();
            await MfaScenario.EnableAsync(browser, _time);
        }

        var before = await AccountRowAsync(grant.UserId);

        var again = await RunAsync(GrantArguments(email, AccountRoles.Moderator, reissue: true));

        Assert.Equal(AdminCommand.RefusedExitCode, again.ExitCode);
        Assert.Equal(before, await AccountRowAsync(grant.UserId));
        Assert.Empty(await Scenario.ReadLinksAsync(email, "PasswordResetNotice"));
    }

    [Fact]
    public async Task ABootstrapIsNotReissuedUnderAnotherRole()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await GrantAsync(email, AccountRoles.Moderator);
        var before = await AccountRowAsync(grant.UserId);

        var again = await RunAsync(GrantArguments(email, AccountRoles.Operator, reissue: true));

        Assert.Equal(AdminCommand.RefusedExitCode, again.ExitCode);
        Assert.Equal(before, await AccountRowAsync(grant.UserId));
        Assert.Equal([AccountRoles.Moderator], await RolesAsync(grant.UserId));
    }

    [Fact]
    public async Task ReissueWithoutAnAccountCreatesNothing()
    {
        var email = IdentityScenario.NewEmail();

        var reissue = await RunAsync(GrantArguments(email, AccountRoles.Moderator, reissue: true));

        Assert.Equal(AdminCommand.RefusedExitCode, reissue.ExitCode);
        Assert.Equal("", reissue.Output);
        Assert.Equal(0L, await Scenario.ScalarAsync(
            "select count(*) from identity.users where normalized_email = upper($1)", email));
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
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", Operator, "--reissue", "yes")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", Operator, "--reissue", "--reissue")]
    [InlineData("admin", "grant", "--email", "x@example.com", "--role", "moderator", "--operator", Operator, "--password", "x")]
    [InlineData("admin", "revoke", "--email", "x@example.com")]
    [InlineData("admin")]
    public async Task InvalidArgumentsChangeNothingAndExitWithUsage(params string[] args)
    {
        var eventsBefore = await AuditEventsByThisOperatorAsync();

        var result = await RunAsync(args);

        Assert.Equal(AdminCommand.UsageExitCode, result.ExitCode);
        Assert.Contains("admin grant --email", result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.Output);
        Assert.Equal(eventsBefore, await AuditEventsByThisOperatorAsync());
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

    private static string[] GrantArguments(string email, string role, bool reissue) =>
        reissue
            ? ["admin", "grant", "--email", email, "--role", role, "--operator", Operator, "--reissue"]
            : ["admin", "grant", "--email", email, "--role", role, "--operator", Operator];

    private Task<GrantResult> GrantAsync(string email, string role = AccountRoles.Moderator) =>
        IssueAsync(email, GrantArguments(email, role, reissue: false));

    private Task<GrantResult> ReissueAsync(string email, string role = AccountRoles.Moderator) =>
        IssueAsync(email, GrantArguments(email, role, reissue: true));

    private async Task<GrantResult> IssueAsync(string email, string[] args)
    {
        var result = await RunAsync(args);
        Assert.True(result.ExitCode == 0, $"Saída {result.ExitCode}: {result.Error}");
        var password = result.Output.Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith(AdminCommand.PasswordLinePrefix, StringComparison.Ordinal))
            [AdminCommand.PasswordLinePrefix.Length..];
        var userId = await Scenario.ScalarTextAsync(
            "select id::text from identity.users where normalized_email = upper($1)", email);
        return new GrantResult(result.ExitCode, result.Output, result.Error, Guid.Parse(userId), password);
    }

    private Task<CommandResult> RunAsync(params string[] args) => RunAsync(_factory, args);

    private static async Task<CommandResult> RunAsync(DuoraWebFactory factory, params string[] args)
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var exitCode = await AdminCommand.ExecuteAsync(factory.Services, args, output, error, Ct);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Segura cada INSERT do bootstrap até os dois comandos chegarem a ele, para a corrida acontecer de fato no índice
    /// único, não por sorte do agendador. Com teto: se o segundo não chegar, o primeiro segue e o teste reprova.
    /// </summary>
    private sealed class InsertBarrier(int participants)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor, IDisposable
    {
        private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);
        private readonly Barrier _barrier = new(participants);

        public bool Overlapped { get; private set; }

        public void Dispose() => _barrier.Dispose();

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("on conflict (normalized_email) do nothing", StringComparison.Ordinal))
            {
                var bothArrived = await Task.Run(() => _barrier.SignalAndWait(Ceiling), cancellationToken);
                Overlapped = Overlapped || bothArrived;
            }

            return result;
        }
    }

    private async Task<Browser> SignedInAsync(string email, string password)
    {
        var browser = new Browser(_factory);
        using var login = await IdentityScenario.LoginAsync(browser, email, password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        await browser.FetchPrivateContextAsync();
        return browser;
    }

    /// <summary>Troca a senha temporária por <see cref="NewPassword"/>; devolve o navegador com a sessão renovada.</summary>
    private async Task<Browser> ChangeTemporaryPasswordAsync(string email, string temporaryPassword)
    {
        var browser = await SignedInAsync(email, temporaryPassword);
        using var change = await browser.PostJsonAsync(
            ChangePassword,
            new { currentPassword = temporaryPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        await browser.FetchPrivateContextAsync();
        return browser;
    }

    private async Task AssertSecretOnlyInOutputAsync(GrantResult issued)
    {
        Assert.Equal(1, Occurrences(issued.Output, issued.Password));
        Assert.DoesNotContain(issued.Password, issued.Error, StringComparison.Ordinal);
        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(issued.Password, StringComparison.Ordinal));
        Assert.Equal(0L, await RowsContainingAsync(issued.Password));
        Assert.Empty(FilesContaining(issued.Password, _factory.MediaRoot, KeyRingDirectory()));
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

    /// <summary>Eventos gravados com o rótulo destes testes, que só esta classe usa (o banco é da suíte toda).</summary>
    private Task<long> AuditEventsByThisOperatorAsync() =>
        Scenario.ScalarAsync("select count(*) from ops.audit_events where actor_label = $1", Operator);

    private Task<long> AuditEventsByThisOperatorAsync(Guid userId) =>
        Scenario.ScalarAsync(
            "select count(*) from ops.audit_events where actor_label = $1 and target_id = $2",
            Operator,
            userId);

    private Task<long> AuditEventsAsync(Guid userId) =>
        Scenario.ScalarAsync("select count(*) from ops.audit_events where target_id = $1", userId);

    /// <summary>Procura a senha em todo texto que o comando grava: conta, papéis, auditoria e outbox.</summary>
    private Task<long> RowsContainingAsync(string secret) =>
        Scenario.ScalarAsync(
            """
            select (select count(*) from identity.users u where strpos(u::text, $1) > 0)
                 + (select count(*) from identity.user_tokens t where strpos(t::text, $1) > 0)
                 + (select count(*) from identity.login_sessions s where strpos(s::text, $1) > 0)
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
