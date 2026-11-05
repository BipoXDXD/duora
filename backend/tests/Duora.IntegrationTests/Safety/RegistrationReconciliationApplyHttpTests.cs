using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-ADM28 e API-ADM29 (issue #321, ADR-0049, Compliance/HTTP), com cookie real: barreira, códigos e allowlist dos corpos.
/// Toda recusa sai só com campos de Problem Details e sem gravar.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationApplyHttpTests(PostgresFixture postgres)
{
    private static readonly string[] ProblemKeys = ["code", "status", "title", "traceId", "type"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertBareProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await ProblemAssert.IsProblemAsync(response, status, code);
        Assert.Equal(ProblemKeys, body.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheApplyAnswersOnlyTheAllowlistedReceipt()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;

        using var response = await a.PostApplyAsync(key: "apply-http");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(["correlationId", "executedAt", "executedByUserId", "factIds", "reason", "runId", "state", "unitId"],
            body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((a.Run, a.Unit, "Applied", a.ModeratorId), (Guid.Parse((string)body["runId"]!), Guid.Parse((string)body["unitId"]!),
            (string?)body["state"], Guid.Parse((string)body["executedByUserId"]!)));
        Assert.Null(body["reason"]);
        Assert.EndsWith("Z", (string)body["executedAt"]!, StringComparison.Ordinal);
        Assert.Single(body["factIds"]!.AsArray());
        Assert.False(string.IsNullOrEmpty((string?)body["correlationId"]));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events a join ops.registration_reconciliation_units u on u.id=a.target_id
            where a.action='events.legacy_registration_reconciliation_applied' and a.correlation_id=u.receipt_correlation_id
              and a.correlation_id=$1
            """, (string)body["correlationId"]!));
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob })
        {
            Assert.DoesNotContain(await s.TextAsync("select email from identity.users where id=$1", user), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(user.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Synthetic legacy fixture", text, StringComparison.Ordinal);
        Assert.DoesNotContain("apply-http", text, StringComparison.Ordinal);
    }

    /// <summary>A evidência não depende de um POST: a leitura traz o mesmo recibo, e nada antes de a unidade terminar.</summary>
    [Fact]
    public async Task TheRunReadExposesTheReceiptOnlyOfATerminalUnit()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        using var pending = await a.Moderator.GetAsync($"{ReconciliationApplyScenario.Path}/{a.Run}");
        var unit = (await MfaScenario.ReadObjectAsync(pending))["units"]![0]!.AsObject();
        Assert.Equal(["id", "items", "measureVersion", "originalMeasureId", "receipt", "state", "subjectUserId"],
            unit.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Null(unit["receipt"]);
        using var applied = await a.PostApplyAsync(key: "receipt-read");
        var receipt = await MfaScenario.ReadObjectAsync(applied);

        using var terminal = await a.Moderator.GetAsync($"{ReconciliationApplyScenario.Path}/{a.Run}");

        var text = await terminal.Content.ReadAsStringAsync(Ct);
        var read = JsonNode.Parse(text)!["units"]![0]!["receipt"]!;
        Assert.True(JsonNode.DeepEquals(receipt, read), read.ToJsonString());
        Assert.DoesNotContain("receipt-read", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplayAnswersTheSameReceiptEvenAfterTheClose()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        using var first = await a.PostApplyAsync();
        var original = await first.Content.ReadAsStringAsync(Ct);
        using var closed = await a.PostCloseAsync();
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        using var replay = await a.PostApplyAsync();

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task TheCloseAnswersTheRunAtTheNextVersionAndAudits()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;

        using var response = await a.PostCloseAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal($"\"reconciliation-run-{a.Run:N}-2\"", response.Headers.ETag?.ToString());
        var body = await MfaScenario.ReadObjectAsync(response);
        Assert.Equal(
            ["authorizationReference", "createdAt", "createdByUserId", "cutoverLower", "cutoverUpper", "eTag", "evidenceId", "id",
                "justification", "selectorVersion", "state", "units", "version"],
            body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("Closed", 2L), ((string?)body["state"], (long)body["version"]!));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_closed' and actor_kind='Moderator'
              and actor_id=$1 and target_type='registration_reconciliation_run' and target_id=$2 and target_version=2
            """, a.ModeratorId, a.Run));
    }

    [Fact]
    public async Task WithoutASessionBothRoutesAreUnauthorized()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        using var stranger = new Browser(a.Host);
        await stranger.FetchCsrfTokenAsync();
        var before = await a.FingerprintAsync();

        using var apply = await a.PostApplyAsync(browser: stranger);
        using var close = await a.PostCloseAsync(browser: stranger);

        await AssertBareProblemAsync(apply, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertBareProblemAsync(close, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await a.AssertNoEffectAsync(before);
    }

    [Theory]
    [InlineData(AccountRoles.Operator)]
    [InlineData(AccountRoles.ProductAdmin)]
    public async Task AnotherAdministrativeRoleIsForbiddenBeforeAnyValidation(string role)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var (_, browser) = await a.Accounts.PrivilegedBrowserAsync(role);
        using var _ = browser;
        var before = await a.FingerprintAsync();

        using var apply = await browser.PostJsonAsync(a.ApplyPath(), null);
        using var close = await browser.PostJsonAsync(a.ClosePath(), null);

        await AssertBareProblemAsync(apply, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        await AssertBareProblemAsync(close, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        await a.AssertNoEffectAsync(before);
    }

    [Fact]
    public async Task AnExpiredTotpProofRequiresTheExistingStepUp()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        await StepUpClock.ExpireRecentAuthenticationAsync(a.Moderator, a.S.Clock);
        var before = await a.FingerprintAsync();

        using var apply = await a.PostApplyAsync();
        using var close = await a.PostCloseAsync();

        await AssertBareProblemAsync(apply, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        await AssertBareProblemAsync(close, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        await a.AssertNoEffectAsync(before);
    }

    [Fact]
    public async Task WithoutTheCsrfProofBothRoutesAreRefused()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var before = await a.FingerprintAsync();

        using var apply = await a.Moderator.PostJsonAsync(a.ApplyPath(), null, new MutationHeaders(
            SendCsrfToken: false, Extra: new Dictionary<string, string> { ["Idempotency-Key"] = AccountSuspensionScenario.NewKey() }));
        using var close = await a.Moderator.PostJsonAsync(a.ClosePath(), null, new MutationHeaders(
            SendCsrfToken: false, Extra: new Dictionary<string, string> { ["If-Match"] = a.RunTag }));

        await AssertBareProblemAsync(apply, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        await AssertBareProblemAsync(close, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        await a.AssertNoEffectAsync(before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tem espaço")]
    [InlineData("kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")]
    public async Task AMissingOrInvalidIdempotencyKeyIsABadRequest(string? key)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var before = await a.FingerprintAsync();

        using var response = await a.Moderator.PostJsonAsync(a.ApplyPath(), null,
            key is null ? MutationHeaders.SameOrigin : AccountSuspensionScenario.WithKey(key));

        await AssertBareProblemAsync(response, HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
        await a.AssertNoEffectAsync(before);
    }

    [Fact]
    public async Task AnUnknownRunOrUnitIsNotFound()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var headers = AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey());

        using var run = await a.Moderator.PostJsonAsync(a.ApplyPath(run: Guid.NewGuid()), null, headers);
        using var unit = await a.Moderator.PostJsonAsync(a.ApplyPath(unit: Guid.NewGuid()), null, headers);
        var unknown = Guid.NewGuid();
        using var close = await a.Moderator.PostJsonAsync(a.ClosePath(unknown), null,
            AccountSuspensionScenario.WithIfMatch($"\"reconciliation-run-{unknown:N}-1\""));

        await AssertBareProblemAsync(run, HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertBareProblemAsync(unit, HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertBareProblemAsync(close, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task APendingUnitOfAClosedRunAndASecondCloseAreConflicts()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        using var closed = await a.PostCloseAsync();
        var tag = closed.Headers.ETag!.ToString();

        using var apply = await a.PostApplyAsync();
        using var again = await a.PostCloseAsync(tag);

        await AssertBareProblemAsync(apply, HttpStatusCode.Conflict, "RUN_CLOSED");
        await AssertBareProblemAsync(again, HttpStatusCode.Conflict, "RUN_CLOSED");
        Assert.Equal(1, await a.S.CountAsync("select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_closed'"));
    }

    [Fact]
    public async Task TheSameKeyInAnotherUnitIsAConflict()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, pick: s => Task.FromResult(new[] { s.Seed.Alice, s.Seed.Third }));
        var s = a.S;
        using var first = await a.PostApplyAsync(a.Units[s.Seed.Alice], key: "shared");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await a.PostApplyAsync(a.Units[s.Seed.Third], key: "shared");

        await AssertBareProblemAsync(second, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        Assert.Equal("Pending", await a.UnitStateAsync(a.Units[s.Seed.Third]));
    }

    [Fact]
    public async Task AStaleOrForeignIfMatchIsPreconditionFailedAndAMissingOneIsRequired()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);

        using var missing = await a.Moderator.PostJsonAsync(a.ClosePath(), null);
        using var wildcard = await a.PostCloseAsync("*");
        using var stale = await a.PostCloseAsync($"\"reconciliation-run-{a.Run:N}-7\"");
        using var foreign = await a.PostCloseAsync($"\"reconciliation-run-{Guid.NewGuid():N}-1\"");
        using var malformed = await a.PostCloseAsync("1");

        await AssertBareProblemAsync(missing, HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        await AssertBareProblemAsync(wildcard, HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        await AssertBareProblemAsync(stale, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        await AssertBareProblemAsync(foreign, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        await AssertBareProblemAsync(malformed, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal("Open", await a.S.TextAsync("select state from ops.registration_reconciliation_runs where id=$1", a.Run));
    }

    [Fact]
    public async Task AnOccupiedUnitIsBusyWithRetryAfter()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        await using var hold = await a.HoldAsync("select 1 from ops.registration_reconciliation_units where id=$1 for update", a.Unit);

        using var response = await a.PostApplyAsync();

        await AssertBareProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(2, ProblemAssert.RetryAfterSeconds(response));
        Assert.Equal("Pending", await a.UnitStateAsync());
    }

    [Fact]
    public async Task ACloseThatOutlastsTheLockTimeoutIsBusyWithRetryAfter()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        await using var hold = await a.HoldAsync("select 1 from ops.registration_reconciliation_runs where id=$1 for share", a.Run);

        using var response = await a.PostCloseAsync();

        await AssertBareProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(2, ProblemAssert.RetryAfterSeconds(response));
    }

    /// <summary>O teto injetado fica abaixo do <c>lock_timeout</c> de 2 s: quem estoura é o <c>statement_timeout</c>.</summary>
    [Fact]
    public async Task ATimedOutApplyOrCloseAsksToWaitLonger()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres, statementTimeout: TimeSpan.FromSeconds(1));
        await using var holder = new Npgsql.NpgsqlConnection(a.S.Database.MigratorConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var exclusive = new Npgsql.NpgsqlCommand("lock table ops.registration_reconciliation_runs in access exclusive mode", holder, transaction))
        {
            await exclusive.ExecuteNonQueryAsync(Ct);
        }

        using var apply = await a.PostApplyAsync();
        using var close = await a.PostCloseAsync();
        await transaction.RollbackAsync(Ct);

        await AssertBareProblemAsync(apply, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        await AssertBareProblemAsync(close, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(30, ProblemAssert.RetryAfterSeconds(apply));
        Assert.Equal(30, ProblemAssert.RetryAfterSeconds(close));
        Assert.Equal("Pending", await a.UnitStateAsync());
    }

    [Fact]
    public async Task BothRoutesUseTheSessionWithTouch()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var s = a.S;
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var now = s.Clock.GetUtcNow();

        using var response = await a.PostApplyAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await s.CountAsync("select count(*) from identity.login_sessions where id=$1 and last_seen_at=$2",
            a.Session.SessionId, now));
    }

    [Fact]
    public async Task TheContractDeclaresBothRoutesWithTheirHeaders()
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "docs", "api", "openapi.json"), Ct))!;
        var paths = document["paths"]!;
        var apply = paths["/api/v1/admin/registration-reconciliations/runs/{runId}/units/{unitId}:apply"]!["post"]!;
        var close = paths["/api/v1/admin/registration-reconciliations/runs/{runId}:close"]!["post"]!;

        Assert.Contains(apply["parameters"]!.AsArray(), p => (string?)p!["name"] == "Idempotency-Key" && (bool)p["required"]!);
        Assert.Contains(close["parameters"]!.AsArray(), p => (string?)p!["name"] == "If-Match" && (bool)p["required"]!);
        Assert.NotNull(close["responses"]!["428"]);
        Assert.NotNull(apply["responses"]!["409"]);
    }
}
