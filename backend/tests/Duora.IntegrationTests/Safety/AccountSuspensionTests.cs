using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Suspensão de conta por violação dos Termos (issue #299, ADR-0042; spec 8.2 e 8.5; INV-06 e INV-10): rotas de
/// moderação, efeito imediato na sessão aberta, revogação, concorrência e auditoria.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class AccountSuspensionTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;
    private readonly string _connectionString;

    public AccountSuspensionTests(PostgresFixture postgres)
    {
        _connectionString = postgres.ConnectionString;
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AnOpenSessionLosesParticipationOnTheVeryNextRequestAfterTheSuspension()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var session = await _scenario.Identity.SignedInBrowserAsync(participant);
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        using (var before = await session.GetAsync(AccountSuspensionScenario.ParticipationSentinel))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        using var participation = await session.GetAsync(AccountSuspensionScenario.ParticipationSentinel);
        using var profile = await session.GetAsync("/api/v1/me/profile");
        await ProblemAssert.IsProblemAsync(participation, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
        await ProblemAssert.IsProblemAsync(profile, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
        Assert.Equal("Suspended", await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
        // A sessão não é revogada: ela continua viva, restrita à allowlist da conta suspensa (spec 8.2).
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from identity.login_sessions where user_id = $1 and revoked_at is null", participant.UserId));
    }

    [Fact]
    public async Task TheSuspendedPersonReadsOnlyTheMinimalStateOfTheOwnAccount()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var session = await _scenario.Identity.SignedInBrowserAsync(participant);
        var (moderatorAccount, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var internalReason = $"CANARY-{Guid.NewGuid():N}";
        using (var suspended = await moderator.PostJsonAsync(
            AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody(internalReason),
            AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey())))
        {
            Assert.Equal(HttpStatusCode.Created, suspended.StatusCode);
        }

        using var me = await session.GetAsync("/api/v1/me");
        using var sessions = await session.GetAsync("/api/v1/me/sessions");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        var text = await me.Content.ReadAsStringAsync(Ct);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("Suspended", (string?)body["status"]);
        var suspension = body["suspension"]!.AsObject();
        Assert.Equal(["reasonCode", "suspendedAt"], suspension.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("TermsViolation", (string?)suspension["reasonCode"]);
        Assert.DoesNotContain(internalReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain(moderatorAccount.UserId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASuspendedAccountStillSignsIn()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        using var browser = new Browser(_factory);
        using var login = await IdentityScenario.LoginAsync(browser, participant.Email, participant.Password);
        await browser.FetchPrivateContextAsync();
        using var me = await browser.GetAsync("/api/v1/me");
        using var events = await browser.GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal("Suspended", (string?)(await MfaScenario.ReadObjectAsync(me))["status"]);
        await ProblemAssert.IsProblemAsync(events, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
    }

    [Fact]
    public async Task LiftingRestoresThePreviousStateAndAccessInTheSameOpenSession()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var session = await _scenario.Identity.SignedInBrowserAsync(participant);
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var suspensionId = Guid.Parse((string)suspension["suspensionId"]!);
        var eTag = (string)suspension["eTag"]!;

        using var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(participant.UserId, suspensionId),
            new { internalReason = "Conta errada; a denúncia era sobre outra pessoa." },
            AccountSuspensionScenario.WithIfMatch(eTag));
        using var participation = await session.GetAsync(AccountSuspensionScenario.ParticipationSentinel);
        using var me = await session.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        var liftedBody = await MfaScenario.ReadObjectAsync(lifted);
        Assert.NotNull((string?)liftedBody["liftedAt"]);
        Assert.NotEqual(eTag, (string?)liftedBody["eTag"]);
        Assert.Equal(HttpStatusCode.OK, participation.StatusCode);
        var meBody = await MfaScenario.ReadObjectAsync(me);
        Assert.Equal("Active", (string?)meBody["status"]);
        Assert.Null(meBody["suspension"]);
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from identity.users where id = $1 and status_before_suspension is not null", participant.UserId));
    }

    [Fact]
    public async Task ASecondLiftWithTheSameETagIsAPreconditionFailureAndChangesNothing()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var path = AccountSuspensionScenario.LiftPath(participant.UserId, Guid.Parse((string)suspension["suspensionId"]!));
        var eTag = AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!);
        using (var first = await moderator.PostJsonAsync(path, new { internalReason = "Primeira revogação." }, eTag))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await moderator.PostJsonAsync(path, new { internalReason = "Segunda revogação." }, eTag);

        await ProblemAssert.IsProblemAsync(second, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal("Primeira revogação.", await _scenario.TextAsync(
            "select lift_reason from safety.moderation_actions where subject_user_id = $1", participant.UserId));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action = 'safety.account_suspension_lifted' and target_id = $1",
            Guid.Parse((string)suspension["suspensionId"]!)));
    }

    [Fact]
    public async Task LiftWithoutIfMatchIsRequiredAndWithAnotherSuspensionsTagFails()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var path = AccountSuspensionScenario.LiftPath(participant.UserId, Guid.Parse((string)suspension["suspensionId"]!));

        using var missing = await moderator.PostJsonAsync(path, new { internalReason = "Sem If-Match." });
        using var foreign = await moderator.PostJsonAsync(path, new { internalReason = "Tag alheia." },
            AccountSuspensionScenario.WithIfMatch($"\"suspension-{Guid.NewGuid():N}-1\""));

        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        await ProblemAssert.IsProblemAsync(foreign, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal("Suspended", await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
    }

    [Fact]
    public async Task TheModeratorReadsTheActiveSuspensionWithTheTagToLiftIt()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        using (var clean = await moderator.GetAsync(AccountSuspensionScenario.StatePath(participant.UserId)))
        {
            Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
            Assert.Null((await MfaScenario.ReadObjectAsync(clean))["activeSuspension"]);
        }

        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        using var state = await moderator.GetAsync(AccountSuspensionScenario.StatePath(participant.UserId));

        var active = (await MfaScenario.ReadObjectAsync(state))["activeSuspension"]!.AsObject();
        Assert.Equal((string?)suspension["suspensionId"], (string?)active["suspensionId"]);
        Assert.Equal((string?)suspension["eTag"], (string?)active["eTag"]);
        Assert.False((bool)active["decidedAutomatically"]!);
        Assert.Equal(
            ["decidedAutomatically", "eTag", "liftedAt", "reasonCode", "suspendedAt", "suspensionId", "userId"],
            active.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SuspendingAndLiftingAreAuditedWithTheModeratorAndTheMeasure()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (moderatorAccount, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var suspensionId = Guid.Parse((string)suspension["suspensionId"]!);
        using (var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(participant.UserId, suspensionId),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!)))
        {
            Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        }

        Assert.Equal(1, await _scenario.CountAsync(
            """
            select count(*) from ops.audit_events
            where action = 'safety.account_suspended' and actor_kind = 'Moderator' and actor_id = $1
              and target_type = 'moderation_action' and target_id = $2 and target_version = 1
            """,
            moderatorAccount.UserId, suspensionId));
        Assert.Equal(1, await _scenario.CountAsync(
            """
            select count(*) from ops.audit_events
            where action = 'safety.account_suspension_lifted' and actor_kind = 'Moderator' and actor_id = $1
              and target_type = 'moderation_action' and target_id = $2 and target_version = 2
            """,
            moderatorAccount.UserId, suspensionId));
        Assert.Equal(1, await _scenario.CountAsync(
            """
            select count(*) from safety.moderation_actions
            where id = $1 and subject_user_id = $2 and author_kind = 'Human' and author_user_id = $3
              and lifted_by_kind = 'Human' and lifted_by_user_id = $3 and version = 2
            """,
            suspensionId, participant.UserId, moderatorAccount.UserId));
    }

    [Fact]
    public async Task TwoSimultaneousSuspensionsOfTheSameAccountCreateOneMeasure()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, first) = await _scenario.PrivilegedBrowserAsync();
        using var __ = first;
        var (_, second) = await _scenario.PrivilegedBrowserAsync();
        using var ___ = second;

        var responses = await Task.WhenAll(
            first.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
                AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey())),
            second.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
                AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey())));

        try
        {
            Assert.Equal(
                [HttpStatusCode.Created, HttpStatusCode.Conflict],
                responses.Select(r => r.StatusCode).Order());
            var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            await ProblemAssert.IsProblemAsync(conflict, HttpStatusCode.Conflict, "ACCOUNT_ALREADY_SUSPENDED");
            Assert.Equal(1, await _scenario.CountAsync(
                "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
            Assert.Equal(1, await _scenario.CountAsync(
                "select count(*) from ops.audit_events where action = 'safety.account_suspended' and target_id in "
                + "(select id from safety.moderation_actions where subject_user_id = $1)", participant.UserId));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondActiveSuspensionEvenWithoutTheService()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (moderatorAccount, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => _scenario.ExecuteAsync(
            """
            insert into safety.moderation_actions (id, subject_user_id, measure, reason_code, internal_reason, author_kind,
              author_user_id, decided_at, command_key, command_intent)
            values (gen_random_uuid(), $1, 'Suspension', 'TermsViolation', 'Segunda.', 'Human', $2, now(),
              encode(sha256(gen_random_uuid()::text::bytea), 'hex'), sha256('x'::bytea))
            """,
            participant.UserId, moderatorAccount.UserId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_moderation_actions_active_suspension", error.ConstraintName);
    }

    [Fact]
    public async Task TheRecordedDecisionCannotBeRewrittenNorDeletedByTheApp()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        var rewrite = await Assert.ThrowsAsync<PostgresException>(() => _scenario.ExecuteAsync(
            "update safety.moderation_actions set internal_reason = 'Outro.' where subject_user_id = $1", participant.UserId));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => _scenario.ExecuteAsync(
            "delete from safety.moderation_actions where subject_user_id = $1", participant.UserId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, rewrite.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    [Fact]
    public async Task RepeatingTheSameKeyReturnsTheSameSuspensionAndAnotherBodyIsRefused()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var key = AccountSuspensionScenario.NewKey();
        var first = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId, key);

        using var replay = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(key));
        using var reused = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody("Outro motivo."), AccountSuspensionScenario.WithKey(key));

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal((string?)first["suspensionId"], (string?)(await MfaScenario.ReadObjectAsync(replay))["suspensionId"]);
        await ProblemAssert.IsProblemAsync(reused, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action = 'safety.account_suspended' and target_id = $1",
            Guid.Parse((string)first["suspensionId"]!)));
    }

    [Fact]
    public async Task AProductAdministratorOrOperatorWithMfaCannotModerateAccounts()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        foreach (var role in new[] { AccountRoles.ProductAdmin, AccountRoles.Operator })
        {
            var (_, browser) = await _scenario.PrivilegedBrowserAsync(role);
            using var ___ = browser;

            using var read = await browser.GetAsync(AccountSuspensionScenario.StatePath(participant.UserId));
            using var suspend = await browser.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
                AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));

            await ProblemAssert.IsProblemAsync(read, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
            var body = await ProblemAssert.IsProblemAsync(suspend, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
            Assert.DoesNotContain(participant.UserId.ToString(), body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Active", await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
    }

    [Fact]
    public async Task AParticipantCannotReachTheModerationRoutes()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var target = await _scenario.CreateEligibleParticipantAsync();
        using var browser = await _scenario.Identity.SignedInBrowserAsync(participant);

        using var read = await browser.GetAsync(AccountSuspensionScenario.StatePath(target.UserId));
        using var suspend = await browser.PostJsonAsync(AccountSuspensionScenario.SuspendPath(target.UserId),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));

        await ProblemAssert.IsProblemAsync(read, HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.IsProblemAsync(suspend, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", target.UserId));
    }

    [Fact]
    public async Task WithoutARecentTotpTheModeratorMustReauthenticateAndNothingIsWritten()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        await StepUpClock.ExpireRecentAuthenticationAsync(moderator, _time);

        using var suspend = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));
        using var read = await moderator.GetAsync(AccountSuspensionScenario.StatePath(participant.UserId));

        await ProblemAssert.IsProblemAsync(suspend, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
    }

    [Fact]
    public async Task AnUnknownOrPrivilegedTargetIsTheSameNotFoundWithoutItsId()
    {
        var (otherModerator, other) = await _scenario.PrivilegedBrowserAsync();
        other.Dispose();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var absent = Guid.NewGuid();

        using var unknown = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(absent),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));
        using var privileged = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(otherModerator.UserId),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));
        using var read = await moderator.GetAsync(AccountSuspensionScenario.StatePath(otherModerator.UserId));

        await ProblemAssert.IsProblemAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(privileged, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(read, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(await ProblemAssert.BodyWithoutTraceIdAsync(unknown), await ProblemAssert.BodyWithoutTraceIdAsync(privileged));
        Assert.DoesNotContain(otherModerator.UserId.ToString(), await privileged.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Active", await _scenario.TextAsync("select status from identity.users where id = $1", otherModerator.UserId));
    }

    [Theory]
    [InlineData("PendingEmail")]
    [InlineData("DeletionPending")]
    public async Task AnAccountOutsideTheSuspendableStatesIsAConflict(string status)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        await _scenario.ExecuteAsync("update identity.users set status = $2 where id = $1", participant.UserId, status);
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;

        using var response = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "ACCOUNT_NOT_SUSPENDABLE");
        Assert.Equal(status, await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
    }

    [Fact]
    public async Task ARestrictedAccountReturnsToRestrictedWhenTheSuspensionIsLifted()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        await _scenario.ExecuteAsync("update identity.users set status = 'Restricted' where id = $1", participant.UserId);
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        using var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(participant.UserId, Guid.Parse((string)suspension["suspensionId"]!)),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal("Restricted", await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
    }

    /// <summary>
    /// B1 da revisão do #302: a selfie aprovada durante a suspensão não promove a conta (o worker só promove quem está
    /// em PendingEligibility). Revogar recalcula a elegibilidade pela mesma regra, em vez de copiar o estado antigo.
    /// </summary>
    [Fact]
    public async Task LiftingPromotesAPendingAccountWhosePresenceWasApprovedDuringTheSuspension()
    {
        var participant = await _scenario.Identity.CreateConfirmedAccountAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        await _scenario.ExecuteAsync(
            """
            insert into identity.selfie_verifications (id, user_id, state, policy_version, requested_at, expires_at, version)
            values (gen_random_uuid(), $1, 'Passed', 'test', now(), now(), 0)
            """,
            participant.UserId);

        using var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(participant.UserId, Guid.Parse((string)suspension["suspensionId"]!)),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal("Active", await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
    }

    [Fact]
    public async Task LiftingKeepsAPendingAccountPendingWithoutApprovedPresence()
    {
        var participant = await _scenario.Identity.CreateConfirmedAccountAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        using var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(participant.UserId, Guid.Parse((string)suspension["suspensionId"]!)),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal("PendingEligibility", await _scenario.TextAsync(
            "select status from identity.users where id = $1", participant.UserId));
    }

    [Fact]
    public async Task ASuspendedAccountSignsOut()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var session = await _scenario.Identity.SignedInBrowserAsync(participant);
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        using var logout = await session.PostJsonAsync("/api/v1/auth/logout", body: null);
        using var me = await session.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task TheSameKeyFromTwoModeratorsCreatesIndependentCommands()
    {
        var first = await _scenario.CreateEligibleParticipantAsync();
        var second = await _scenario.CreateEligibleParticipantAsync();
        var (_, maria) = await _scenario.PrivilegedBrowserAsync();
        using var __ = maria;
        var (_, pedro) = await _scenario.PrivilegedBrowserAsync();
        using var ___ = pedro;
        var key = AccountSuspensionScenario.NewKey();

        var byMaria = await AccountSuspensionScenario.SuspendAsync(maria, first.UserId, key);
        using var byPedro = await pedro.PostJsonAsync(AccountSuspensionScenario.SuspendPath(second.UserId),
            AccountSuspensionScenario.SuspendBody(), AccountSuspensionScenario.WithKey(key));

        Assert.Equal(HttpStatusCode.Created, byPedro.StatusCode);
        var pedroBody = await MfaScenario.ReadObjectAsync(byPedro);
        Assert.NotEqual((string?)byMaria["suspensionId"], (string?)pedroBody["suspensionId"]);
        Assert.Equal(second.UserId.ToString(), (string?)pedroBody["userId"]);
        Assert.Equal("Suspended", await _scenario.TextAsync("select status from identity.users where id = $1", second.UserId));
    }

    [Theory]
    [InlineData("{", true)]
    [InlineData("[1,2]", true)]
    [InlineData("""{"reasonCode":"TermsViolation","internalReason":"Motivo.","extra":1}""", true)]
    [InlineData("""{"reasonCode":"Unknown","internalReason":"Motivo."}""", true)]
    [InlineData("""{"reasonCode":"TermsViolation"}""", true)]
    [InlineData("""{"reasonCode":"TermsViolation","internalReason":"Motivo."}""", false)]
    public async Task AProductAdministratorIsForbiddenBeforeTheBodyOrKeyIsValidated(string body, bool withKey)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, admin) = await _scenario.PrivilegedBrowserAsync(AccountRoles.ProductAdmin);
        using var __ = admin;

        using var response = await admin.PostRawJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId), body,
            withKey ? AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()) : null);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from safety.user_guards where user_id = $1", participant.UserId));
    }

    [Fact]
    public async Task TheServiceRefusesASuspensionByANonModeratorBeforeTouchingAnyGuard()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (admin, browser) = await _scenario.PrivilegedBrowserAsync(AccountRoles.ProductAdmin);
        using var __ = browser;
        var session = new ModeratorSession(admin.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        // A guarda do alvo está travada por outra transação: se o serviço tentasse travá-la antes de conferir o papel,
        // esperaria o lock_timeout e responderia Busy em vez de Forbidden.
        await using var holder = await HoldGuardAsync(participant.UserId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<AccountSuspensions>().SuspendAsModeratorAsync(
            session,
            SuspensionOrder.ByModerator(admin.UserId, participant.UserId, SuspensionReason.TermsViolation, "Motivo."),
            ModerationCommandKey.ForModerator(admin.UserId, AccountSuspensionScenario.NewKey()),
            Ct);

        Assert.IsType<SuspensionOutcome.Forbidden>(outcome);
        await holder.DisposeAsync();
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from safety.user_guards where user_id = $1", admin.UserId));
    }

    [Fact]
    public async Task TheServiceRefusesALiftByANonModeratorBeforeTouchingItsGuard()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var (admin, browser) = await _scenario.PrivilegedBrowserAsync(AccountRoles.ProductAdmin);
        using var ___ = browser;
        var session = new ModeratorSession(admin.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        await using var holder = await HoldGuardAsync(participant.UserId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<AccountSuspensions>().LiftAsModeratorAsync(
            session, participant.UserId, Guid.Parse((string)suspension["suspensionId"]!), 1,
            SuspensionLift.ByModerator(admin.UserId, "Motivo."), Ct);

        Assert.IsType<LiftOutcome.Forbidden>(outcome);
        await holder.DisposeAsync();
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from safety.user_guards where user_id = $1", admin.UserId));
        Assert.Equal("Suspended", await _scenario.TextAsync("select status from identity.users where id = $1", participant.UserId));
    }

    [Theory]
    [InlineData("Human", "moderator", "selfie-verification")]
    [InlineData("Automated", null, null)]
    [InlineData("Automated", null, "Selfie_Verification")]
    public async Task TheDatabaseTiesTheSourceToAutomatedMeasures(string authorKind, string? author, string? source)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (moderatorAccount, moderator) = await _scenario.PrivilegedBrowserAsync();
        moderator.Dispose();

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertMeasureAsync(
            participant.UserId, authorKind, author is null ? null : moderatorAccount.UserId, source));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_moderation_actions_source", error.ConstraintName);
    }

    [Theory]
    [InlineData("source_code = 'another-source'")]
    [InlineData("source_id = gen_random_uuid()")]
    public async Task TheSourceOfAnActiveMeasureCannotBeRewritten(string change)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        await InsertMeasureAsync(participant.UserId, "Automated", null, "selfie-verification");

        var error = await Assert.ThrowsAsync<PostgresException>(() => _scenario.ExecuteAsync(
            $"update safety.moderation_actions set {change} where subject_user_id = $1", participant.UserId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Contains("immutable", error.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Cria a guarda da pessoa e a mantém travada (FOR UPDATE) até o descarte.</summary>
    private async Task<GuardLock> HoldGuardAsync(Guid userId)
    {
        await _scenario.ExecuteAsync("insert into safety.user_guards (user_id) values ($1) on conflict do nothing", userId);
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select 1 from safety.user_guards where user_id = $1 for update", connection, transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(Ct);
        return new GuardLock(connection, transaction);
    }

    private sealed class GuardLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private Task InsertMeasureAsync(Guid subject, string authorKind, Guid? author, string? source) =>
        _scenario.ExecuteAsync(
            """
            insert into safety.moderation_actions (id, subject_user_id, measure, reason_code, internal_reason, author_kind,
              author_user_id, decided_at, command_key, command_intent, source_code, source_id)
            values (gen_random_uuid(), $1, 'Suspension', 'SelfieVerificationFailed', 'Direto no banco.', $2, $3::uuid,
              now(), encode(sha256(gen_random_uuid()::text::bytea), 'hex'), sha256('x'::bytea), $4::varchar,
              case when $4::varchar is null then null else gen_random_uuid() end)
            """,
            subject, authorKind, (object?)author ?? DBNull.Value, (object?)source ?? DBNull.Value);

    [Theory]
    [InlineData("""{"reasonCode":"TermsViolation"}""")]
    [InlineData("""{"reasonCode":"TermsViolation","internalReason":"   "}""")]
    [InlineData("""{"reasonCode":"TermsViolation","internalReason":"com\u0000nulo"}""")]
    [InlineData("""{"reasonCode":"SelfieVerificationFailed","internalReason":"Motivo."}""")]
    [InlineData("""{"internalReason":"Motivo."}""")]
    [InlineData("""{"reasonCode":"TermsViolation","internalReason":"Motivo.","status":"Active"}""")]
    public async Task AnInvalidBodyIsRefusedAndNothingIsWritten(string body)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;

        using var response = await moderator.PostRawJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            body, AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
    }

    [Fact]
    public async Task AReasonOverTheCeilingIsRefused()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;

        using var atCeiling = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody(new string('a', SuspensionOrder.InternalReasonMaxLength + 1)),
            AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));

        Assert.Equal(HttpStatusCode.BadRequest, atCeiling.StatusCode);
    }

    [Fact]
    public async Task WithoutAnIdempotencyKeyTheSuspensionIsRefused()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;

        using var response = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
    }

    [Fact]
    public async Task TheSystemSuspendsThroughTheInternalApiIdempotentlyAndAudited()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var session = await _scenario.Identity.SignedInBrowserAsync(participant);
        var source = Guid.NewGuid();
        var order = SuspensionOrder.Automated(participant.UserId, SuspensionReason.SelfieVerificationFailed, "Limite de tentativas.");
        var key = ModerationCommandKey.ForSystem("selfie-verification", source);

        var first = await SuspendAutomaticallyAsync(order, key);
        var replay = await SuspendAutomaticallyAsync(order, key);
        var another = await SuspendAutomaticallyAsync(order, ModerationCommandKey.ForSystem("selfie-verification", Guid.NewGuid()));
        using var me = await session.GetAsync("/api/v1/me");

        var suspended = Assert.IsType<SuspensionOutcome.Suspended>(first);
        Assert.False(suspended.Replayed);
        Assert.True(suspended.View.DecidedAutomatically);
        var repeated = Assert.IsType<SuspensionOutcome.Suspended>(replay);
        Assert.True(repeated.Replayed);
        Assert.Equal(suspended.View.Id, repeated.View.Id);
        Assert.IsType<SuspensionOutcome.AlreadySuspended>(another);
        Assert.Equal("SelfieVerificationFailed", (string?)(await MfaScenario.ReadObjectAsync(me))["suspension"]!["reasonCode"]);
        Assert.Equal(1, await _scenario.CountAsync(
            """
            select count(*) from ops.audit_events
            where action = 'safety.account_suspended' and actor_kind = 'System' and actor_id is null and target_id = $1
            """,
            suspended.View.Id));
        // Evidência utilizada (spec 12.3): a origem da decisão, só pelo id, sem dado biométrico.
        Assert.Equal(1, await _scenario.CountAsync(
            """
            select count(*) from safety.moderation_actions
            where id = $1 and source_code = 'selfie-verification' and source_id = $2
            """,
            suspended.View.Id, source));
        Assert.Equal(1, await _scenario.CountAsync(
            """
            select count(*) from ops.audit_events
            where action = 'safety.account_suspension_source_recorded' and actor_kind = 'System'
              and target_type = 'selfie_verification' and target_id = $1
            """,
            source));
    }

    [Fact]
    public async Task AModeratorSuspensionHasNoSystemSource()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;

        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);

        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1 and source_code is null and source_id is null",
            participant.UserId));
    }

    [Fact]
    public async Task TheInternalApiRefusesAnOrderThatIsNotAutomated()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var order = SuspensionOrder.ByModerator(Guid.NewGuid(), participant.UserId, SuspensionReason.TermsViolation, "Motivo.");

        await Assert.ThrowsAsync<ArgumentException>(() => SuspendAutomaticallyAsync(
            order, ModerationCommandKey.ForSystem("selfie-verification", Guid.NewGuid())));
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
    }

    private async Task<SuspensionOutcome> SuspendAutomaticallyAsync(SuspensionOrder order, ModerationCommandKey key)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountSuspensions>()
            .SuspendAutomaticallyAsync(order, key, Ct);
    }
}
