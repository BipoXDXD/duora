using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-ADM26 e API-ADM27 (issue #321, ADR-0049, Compliance/Manifesto), num banco próprio com corte sintético: o run
/// nasce da prévia, recalculado pelo servidor numa transação REPEATABLE READ. Digest divergente é 412 sem gravar nada,
/// só um run fica aberto, inclusive em criações concorrentes, e criar um run não altera Events nem Safety.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationRunManifestTests(PostgresFixture postgres)
{
    private const string Path = RegistrationReconciliationRunHttpTests.Path;
    private const string PreviewPath = RegistrationReconciliationPreviewHttpTests.Path;
    private const string Justification = "Prévia revisada pelo moderador.";
    private const string Reference = "AUTH-321-1";

    // A sessão é tocada a cada pedido, em identity; os demais schemas têm de ficar como estavam.
    private const string BusinessSchemas = "'profiles','events','experiences','social','safety','media','privacy'";
    private const string AllButSessions = BusinessSchemas + ",'ops'";

    private static readonly string[] ProblemKeys = ["code", "status", "title", "traceId", "type"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SuspendedRegistrationScenario> ScenarioAsync()
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, s.Clock.GetUtcNow().AddHours(1));
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        return s;
    }

    private static DuoraWebFactory Host(SuspendedRegistrationScenario s) => RegistrationReconciliationPreviewEffectsTests.Host(s);

    private static Task<Browser> ModeratorAsync(SuspendedRegistrationScenario s, DuoraWebFactory host) =>
        RegistrationReconciliationPreviewEffectsTests.ModeratorAsync(s, host);

    private static Task<Guid> SuspendAsync(SuspendedRegistrationScenario s, Guid subject) =>
        RegistrationReconciliationPreviewEffectsTests.SuspendBeforePolicyAsync(s, subject);

    private static Task<string> FingerprintAsync(SuspendedRegistrationScenario s, string schemas, bool includeSequences = true) =>
        RegistrationReconciliationPreviewEffectsTests.FingerprintAsync(s, schemas, includeSequences);

    /// <summary>O caminho real: a página da prévia, com o digest que o moderador viu.</summary>
    private static async Task<JsonObject[]> PreviewAsync(Browser moderator)
    {
        using var response = await moderator.GetAsync(PreviewPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await MfaScenario.ReadObjectAsync(response))["page"]!.AsArray().Select(unit => unit!.AsObject())];
    }

    private static object BodyOf(params JsonObject[] units) => new
    {
        units = units.Select(unit => new
        {
            subjectUserId = (string?)unit["subjectUserId"],
            originalMeasureId = (string?)unit["originalMeasureId"],
            unitDigest = (string?)unit["unitDigest"],
        }).ToArray(),
        justification = Justification,
        authorizationReference = Reference,
    };

    private static Task<HttpResponseMessage> CreateAsync(Browser moderator, object body, string? key = null) =>
        moderator.PostJsonAsync(Path, body, AccountSuspensionScenario.WithKey(key ?? AccountSuspensionScenario.NewKey()));

    private static async Task<Guid> CreatedRunAsync(Browser moderator, object body)
    {
        using var response = await CreateAsync(moderator, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((string)(await MfaScenario.ReadObjectAsync(response))["id"]!);
    }

    private static Task CloseAsync(SuspendedRegistrationScenario s, Guid run) =>
        s.ExecuteAsync("update ops.registration_reconciliation_runs set state='Closed',version=2 where id=$1", run);

    private static Task<long> RunsAsync(SuspendedRegistrationScenario s) =>
        s.CountAsync("select count(*) from ops.registration_reconciliation_runs");

    private static async Task AssertNothingWrittenAsync(HttpResponseMessage response, string code, HttpStatusCode status)
    {
        var body = await ProblemAssert.IsProblemAsync(response, status, code);
        Assert.Equal(ProblemKeys, body.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheModeratorCreatesARunFromThePreviewWithOnlyTheAllowlistedFields()
    {
        await using var s = await ScenarioAsync();
        var measure = await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (account, moderator) = await scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        var unit = Assert.Single(await PreviewAsync(moderator));

        using var response = await CreateAsync(moderator, BodyOf(unit));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(
            ["authorizationReference", "createdAt", "createdByUserId", "cutoverLower", "cutoverUpper", "eTag", "evidenceId", "id",
                "justification", "selectorVersion", "state", "units", "version"],
            body.Select(p => p.Key).Order(StringComparer.Ordinal));
        var run = Guid.Parse((string)body["id"]!);
        Assert.Equal(7, run.Version);
        Assert.Equal($"{Path}/{run}", response.Headers.Location?.OriginalString);
        Assert.Equal($"\"reconciliation-run-{run:N}-1\"", response.Headers.ETag?.ToString());
        Assert.Equal(response.Headers.ETag?.ToString(), (string?)body["eTag"]);
        Assert.Equal(("Open", 1L, 1, "synthetic-321-r1", account.UserId, Justification, Reference),
            ((string?)body["state"], (long)body["version"]!, (int)body["selectorVersion"]!, (string?)body["evidenceId"],
                Guid.Parse((string)body["createdByUserId"]!), (string?)body["justification"], (string?)body["authorizationReference"]));
        Assert.EndsWith("Z", (string)body["cutoverLower"]!, StringComparison.Ordinal);
        Assert.EndsWith("Z", (string)body["createdAt"]!, StringComparison.Ordinal);
        var created = Assert.Single(body["units"]!.AsArray())!.AsObject();
        Assert.Equal(["id", "items", "measureVersion", "originalMeasureId", "receipt", "state", "subjectUserId"],
            created.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((s.Seed.Alice, measure, 1L, "Pending"),
            (Guid.Parse((string)created["subjectUserId"]!), Guid.Parse((string)created["originalMeasureId"]!),
                (long)created["measureVersion"]!, (string?)created["state"]));
        var item = Assert.Single(created["items"]!.AsArray())!.AsObject();
        Assert.Equal(["eventId", "maxPromotions", "maxReleasedSeats", "registrationVersion", "state"],
            item.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((s.EventId, 1L, "Confirmed", 1, 1),
            (Guid.Parse((string)item["eventId"]!), (long)item["registrationVersion"]!, (string?)item["state"],
                (int)item["maxReleasedSeats"]!, (int)item["maxPromotions"]!));
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third })
        {
            Assert.DoesNotContain(await s.TextAsync("select email from identity.users where id=$1", user), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Synthetic legacy fixture", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunAndItsPendingUnitsArePersistedWithTheAudit()
    {
        await using var s = await ScenarioAsync();
        var measure = await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (account, moderator) = await scenario.PrivilegedBrowserAsync();
        using var _ = moderator;

        var run = await CreatedRunAsync(moderator, BodyOf(await PreviewAsync(moderator)));

        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.registration_reconciliation_runs where id=$1 and state='Open' and version=1
              and selector_version=1 and cutover_evidence_id='synthetic-321-r1' and created_by_user_id=$2
              and justification=$3 and authorization_reference=$4 and created_at=$5
              and octet_length(intent_hash)=32
            """, run, account.UserId, Justification, Reference, s.Clock.GetUtcNow()));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.registration_reconciliation_units u, jsonb_array_elements(u.items) i
            where u.run_id=$1 and u.subject_user_id=$2 and u.original_measure_id=$3 and u.measure_version=1
              and u.state='Pending' and u.receipt_fact_ids is null and i->>'eventId'=$4::text
              and (i->>'registrationVersion')::bigint=1 and i->>'state'='Confirmed'
              and (i->>'maxReleasedSeats')::int=1 and (i->>'maxPromotions')::int=1
            """, run, s.Seed.Alice, measure, s.EventId));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_created'
              and actor_kind='Moderator' and actor_id=$1 and target_type='registration_reconciliation_run'
              and target_id=$2 and target_version=1 and occurred_at=$3
            """, account.UserId, run, s.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task CreatingARunChangesNeitherEventsNorSafety()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var preview = await PreviewAsync(moderator);
        // A sequência da auditoria avança: ela não é de Events nem de Safety, e a linha auditada é o efeito esperado.
        var before = await FingerprintAsync(s, BusinessSchemas, includeSequences: false);
        var outbox = await s.CountAsync("select count(*) from ops.outbox_events");

        await CreatedRunAsync(moderator, BodyOf(preview));

        Assert.Equal(before, await FingerprintAsync(s, BusinessSchemas, includeSequences: false));
        Assert.Equal(outbox, await s.CountAsync("select count(*) from ops.outbox_events"));
    }

    [Fact]
    public async Task ADigestThatDoesNotMatchIsPreconditionFailedAndWritesNothing()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var unit = Assert.Single(await PreviewAsync(moderator));
        unit["unitDigest"] = new string('0', 64);
        var before = await FingerprintAsync(s, AllButSessions);

        using var response = await CreateAsync(moderator, BodyOf(unit));

        await AssertNothingWrittenAsync(response, "PRECONDITION_FAILED", HttpStatusCode.PreconditionFailed);
        Assert.Equal(before, await FingerprintAsync(s, AllButSessions));
    }

    public static TheoryData<string> ChangesAfterThePreview =>
    [
        "update events.registrations set version=version+1 where event_id=$1 and user_id=$2",
        "update safety.moderation_actions set version=2,lifted_at=decided_at,lifted_by_kind='Automated',lift_reason='Sintética' where subject_user_id=$2 and $1=$1",
        "update events.registrations set state='Withdrawn' where event_id=$1 and user_id=$2",
        "update events.events set state='Cancelled' where id=$1 and $2=$2",
        "update events.events set capacity=capacity+1 where id=$1 and $2=$2",
    ];

    [Theory]
    [MemberData(nameof(ChangesAfterThePreview))]
    public async Task AChangeAfterThePreviewForcesANewPreview(string change)
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var unit = Assert.Single(await PreviewAsync(moderator));
        await s.ExecuteAsync(change, s.EventId, s.Seed.Alice);
        var before = await FingerprintAsync(s, AllButSessions);

        using var response = await CreateAsync(moderator, BodyOf(unit));

        await AssertNothingWrittenAsync(response, "PRECONDITION_FAILED", HttpStatusCode.PreconditionFailed);
        Assert.Equal(before, await FingerprintAsync(s, AllButSessions));
    }

    [Fact]
    public async Task AQueueChangeThatLowersTheCeilingForcesANewPreview()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var unit = Assert.Single(await PreviewAsync(moderator));
        await s.ExecuteAsync("delete from identity.selfie_verifications where user_id=$1", s.Seed.Bob);
        await s.ExecuteAsync("delete from identity.selfie_verifications where user_id=$1", s.Seed.Third);

        using var response = await CreateAsync(moderator, BodyOf(unit));

        await AssertNothingWrittenAsync(response, "PRECONDITION_FAILED", HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task AUnitThatStoppedBeingEligibleIsPreconditionFailed()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var unit = Assert.Single(await PreviewAsync(moderator));
        await s.ExecuteAsync("update identity.users set status_before_suspension=null where id=$1", s.Seed.Alice);

        using var response = await CreateAsync(moderator, BodyOf(unit));

        await AssertNothingWrittenAsync(response, "PRECONDITION_FAILED", HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task ADifferentConfiguredCutoverForcesANewPreview()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        JsonObject unit;
        await using (var host = Host(s))
        {
            using var moderator = await ModeratorAsync(s, host);
            unit = Assert.Single(await PreviewAsync(moderator));
        }

        await using var changed = new DuoraWebFactory(s.Database.AppConnectionString)
        {
            Time = s.Clock,
            Settings = new Dictionary<string, string?>
            {
                ["RegistrationReconciliation:Cutover:EvidenceId"] = "synthetic-321-r2",
                ["RegistrationReconciliation:Cutover:Lower"] = "2099-01-01T00:00:00Z",
                ["RegistrationReconciliation:Cutover:Upper"] = "2099-01-01T00:01:00Z",
            },
        };
        using var second = await ModeratorAsync(s, changed);

        using var response = await CreateAsync(second, BodyOf(unit));

        await AssertNothingWrittenAsync(response, "PRECONDITION_FAILED", HttpStatusCode.PreconditionFailed);
        Assert.Equal(0, await RunsAsync(s));
    }

    [Fact]
    public async Task OneStaleUnitRefusesTheWholeRun()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await SuspendAsync(s, s.Seed.Bob);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var units = await PreviewAsync(moderator);
        Assert.Equal(2, units.Length);
        units[1]["unitDigest"] = new string('0', 64);
        var before = await FingerprintAsync(s, AllButSessions);

        using var response = await CreateAsync(moderator, BodyOf(units));

        await AssertNothingWrittenAsync(response, "PRECONDITION_FAILED", HttpStatusCode.PreconditionFailed);
        Assert.Equal(before, await FingerprintAsync(s, AllButSessions));
    }

    [Fact]
    public async Task ARunHoldsEveryRequestedUnit()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await SuspendAsync(s, s.Seed.Bob);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);

        var run = await CreatedRunAsync(moderator, BodyOf(await PreviewAsync(moderator)));

        Assert.Equal(2, await s.CountAsync("select count(*) from ops.registration_reconciliation_units where run_id=$1", run));
    }

    [Fact]
    public async Task ASecondOpenRunIsRefusedWithoutWriting()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        await CreatedRunAsync(moderator, body);
        var before = await FingerprintAsync(s, AllButSessions);

        using var second = await CreateAsync(moderator, body);

        await AssertNothingWrittenAsync(second, "RUN_ALREADY_OPEN", HttpStatusCode.Conflict);
        Assert.Equal(before, await FingerprintAsync(s, AllButSessions));
    }

    [Fact]
    public async Task AfterTheCloseAnotherRunCanBeCreated()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        await CloseAsync(s, await CreatedRunAsync(moderator, body));

        await CreatedRunAsync(moderator, body);

        Assert.Equal(2, await RunsAsync(s));
    }

    [Fact]
    public async Task AReplayReturnsTheSameRunEvenAfterItIsClosed()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        var key = AccountSuspensionScenario.NewKey();
        using var first = await CreateAsync(moderator, body, key);
        var run = Guid.Parse((string)(await MfaScenario.ReadObjectAsync(first))["id"]!);

        using var replay = await CreateAsync(moderator, body, key);
        await CloseAsync(s, run);
        await s.ExecuteAsync("update events.registrations set version=version+1 where event_id=$1 and user_id=$2", s.EventId, s.Seed.Alice);
        using var afterClose = await CreateAsync(moderator, body, key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(run, Guid.Parse((string)(await MfaScenario.ReadObjectAsync(replay))["id"]!));
        var closed = await MfaScenario.ReadObjectAsync(afterClose);
        Assert.Equal(HttpStatusCode.Created, afterClose.StatusCode);
        Assert.Equal((run, "Closed", 2L), (Guid.Parse((string)closed["id"]!), (string?)closed["state"], (long)closed["version"]!));
        Assert.Equal(1, await RunsAsync(s));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_created'"));
    }

    [Fact]
    public async Task TheSameKeyWithAnotherBodyIsAConflict()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var preview = await PreviewAsync(moderator);
        var key = AccountSuspensionScenario.NewKey();
        using var first = await CreateAsync(moderator, BodyOf(preview), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var other = new
        {
            units = preview.Select(unit => new
            {
                subjectUserId = (string?)unit["subjectUserId"],
                originalMeasureId = (string?)unit["originalMeasureId"],
                unitDigest = (string?)unit["unitDigest"],
            }).ToArray(),
            justification = "Outra justificativa.",
            authorizationReference = Reference,
        };

        using var conflict = await CreateAsync(moderator, other, key);

        await AssertNothingWrittenAsync(conflict, "IDEMPOTENCY_KEY_REUSED", HttpStatusCode.Conflict);
        Assert.Equal(1, await RunsAsync(s));
    }

    [Fact]
    public async Task TheKeyIsScopedToTheActor()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        using var other = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        var key = AccountSuspensionScenario.NewKey();
        using var first = await CreateAsync(moderator, body, key);
        var run = Guid.Parse((string)(await MfaScenario.ReadObjectAsync(first))["id"]!);
        await CloseAsync(s, run);

        using var second = await CreateAsync(other, body, key);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(run, Guid.Parse((string)(await MfaScenario.ReadObjectAsync(second))["id"]!));
    }

    [Fact]
    public async Task TwoConcurrentCreationsLeaveOneOpenRun()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var first = await ModeratorAsync(s, host);
        using var second = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(first));

        var responses = await Task.WhenAll(CreateAsync(first, body), CreateAsync(second, body));

        using var a = responses[0];
        using var b = responses[1];
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        await ProblemAssert.IsProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "RUN_ALREADY_OPEN");
        Assert.Equal(1, await RunsAsync(s));
    }

    [Fact]
    public async Task TwoConcurrentCreationsWithTheSameKeyGiveTheSameRun()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        var key = AccountSuspensionScenario.NewKey();

        var responses = await Task.WhenAll(CreateAsync(moderator, body, key), CreateAsync(moderator, body, key));

        using var a = responses[0];
        using var b = responses[1];
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(1, await RunsAsync(s));
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_created'"));
    }

    /// <summary>
    /// Barreira determinística: outra transação já inseriu um run aberto sem confirmar. A criação não o enxerga no
    /// snapshot, passa da consulta e espera no índice único; o desfecho da outra transação decide.
    /// </summary>
    [Theory]
    [InlineData(true, HttpStatusCode.Conflict)]
    [InlineData(false, HttpStatusCode.Created)]
    public async Task TheUniqueIndexDecidesARaceThatTheSnapshotCannotSee(bool commit, HttpStatusCode expected)
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        await using var holder = await s.Source.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand("""
            insert into ops.registration_reconciliation_runs(id,selector_version,cutover_evidence_id,cutover_lower,cutover_upper,
              created_by_user_id,justification,authorization_reference,state,version,created_at,idempotency_key,intent_hash)
            values(gen_random_uuid(),1,'other',now(),now(),$1,'Outro','AUTH-2','Open',1,now(),'holder',sha256('h'::bytea))
            """, holder, transaction))
        {
            insert.Parameters.AddWithValue(s.Seed.Third);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var creating = CreateAsync(moderator, body);
        await WaitUntilBlockedAsync(s, "insert into ops.registration_reconciliation_runs");
        if (commit)
        {
            await transaction.CommitAsync(Ct);
        }
        else
        {
            await transaction.RollbackAsync(Ct);
        }

        using var response = await creating;

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, await RunsAsync(s));
        if (commit)
        {
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "RUN_ALREADY_OPEN");
        }
    }

    [Fact]
    public async Task ContentionIsTemporarilyUnavailableWithRetryAfterAndWritesNothing()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));
        var before = await FingerprintAsync(s, BusinessSchemas);

        HttpResponseMessage response;
        await using (var holder = new NpgsqlConnection(s.Database.MigratorConnectionString))
        {
            await holder.OpenAsync(Ct);
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using (var exclusive = new NpgsqlCommand(
                "lock table ops.registration_reconciliation_runs in access exclusive mode", holder, transaction))
            {
                await exclusive.ExecuteNonQueryAsync(Ct);
            }
            response = await CreateAsync(moderator, body);
            await transaction.RollbackAsync(Ct);
        }

        using (response)
        {
            await AssertNothingWrittenAsync(response, "TEMPORARILY_UNAVAILABLE", HttpStatusCode.ServiceUnavailable);
            Assert.Equal(2, ProblemAssert.RetryAfterSeconds(response));
        }
        Assert.Equal(before, await FingerprintAsync(s, BusinessSchemas));
        Assert.Equal(0, await RunsAsync(s));
    }

    /// <summary>O teto injetado fica abaixo do <c>lock_timeout</c> de 2 s: quem estoura é o <c>statement_timeout</c>.</summary>
    [Fact]
    public async Task ATimedOutCreationAsksToWaitLongerAndWritesNothing()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = RegistrationReconciliationPreviewEffectsTests.Host(s, TimeSpan.FromSeconds(1));
        using var moderator = await ModeratorAsync(s, host);
        var body = BodyOf(await PreviewAsync(moderator));

        using var response = await WhileLockedAsync(s, "events.registrations", () => CreateAsync(moderator, body));

        await AssertNothingWrittenAsync(response, "TEMPORARILY_UNAVAILABLE", HttpStatusCode.ServiceUnavailable);
        Assert.Equal(30, ProblemAssert.RetryAfterSeconds(response));
        Assert.Equal(0, await RunsAsync(s));
    }

    [Fact]
    public async Task ATimedOutReadAsksToWaitLonger()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = RegistrationReconciliationPreviewEffectsTests.Host(s, TimeSpan.FromSeconds(1));
        using var moderator = await ModeratorAsync(s, host);
        var run = await CreatedRunAsync(moderator, BodyOf(await PreviewAsync(moderator)));

        using var response = await WhileLockedAsync(s, "ops.registration_reconciliation_units",
            () => moderator.GetAsync($"{Path}/{run}"));

        await AssertNothingWrittenAsync(response, "TEMPORARILY_UNAVAILABLE", HttpStatusCode.ServiceUnavailable);
        Assert.Equal(30, ProblemAssert.RetryAfterSeconds(response));
    }

    private static async Task<HttpResponseMessage> WhileLockedAsync(SuspendedRegistrationScenario s, string table,
        Func<Task<HttpResponseMessage>> request)
    {
        await using var holder = new NpgsqlConnection(s.Database.MigratorConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var exclusive = new NpgsqlCommand($"lock table {table} in access exclusive mode", holder, transaction))
        {
            await exclusive.ExecuteNonQueryAsync(Ct);
        }

        var response = await request();
        await transaction.RollbackAsync(Ct);
        return response;
    }

    [Fact]
    public async Task TheAuthorizationIsCheckedAgainInsideTheTransaction()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (account, browser) = await scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var session = new ModeratorSession(account.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        var unit = Assert.Single(await PreviewAsync(browser));
        var order = ReconciliationRunOrder.Create(
            [new ReconciliationRunUnitRef(s.Seed.Alice, Guid.Parse((string)unit["originalMeasureId"]!), (string)unit["unitDigest"]!)],
            Justification, Reference);
        var runs = host.Services.GetRequiredService<RegistrationReconciliationRuns>();
        Assert.True(IdempotencyKey.TryParse(AccountSuspensionScenario.NewKey(), out var key));

        Assert.IsType<ReconciliationRunOutcome.Forbidden>(await runs.CreateAsModeratorAsync(session with { SessionId = Guid.NewGuid() }, order, null, key, Ct));
        Assert.IsType<ReconciliationRunOutcome.Forbidden>(await runs.CreateAsModeratorAsync(session with { UserId = s.Seed.Alice }, order, null, key, Ct));
        await s.ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1",
            session.SessionId, s.Clock.GetUtcNow() - RecentAuthentication.Window - TimeSpan.FromTicks(10));
        Assert.IsType<ReconciliationRunOutcome.Forbidden>(await runs.CreateAsModeratorAsync(session, order, null, key, Ct));
        Assert.IsType<ReconciliationRunReadOutcome.Forbidden>(await runs.ReadAsModeratorAsync(session, Guid.NewGuid(), Ct));

        Assert.Equal(0, await RunsAsync(s));
    }

    /// <summary>Replay devolve o manifesto: quem perdeu a autorização não o recebe, nem com a própria chave.</summary>
    [Fact]
    public async Task AReplayWithARevokedProofIsForbidden()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (account, browser) = await scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var session = new ModeratorSession(account.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        var unit = Assert.Single(await PreviewAsync(browser));
        var order = ReconciliationRunOrder.Create(
            [new ReconciliationRunUnitRef(s.Seed.Alice, Guid.Parse((string)unit["originalMeasureId"]!), (string)unit["unitDigest"]!)],
            Justification, Reference);
        var runs = host.Services.GetRequiredService<RegistrationReconciliationRuns>();
        Assert.True(IdempotencyKey.TryParse(AccountSuspensionScenario.NewKey(), out var key));
        var evidence = host.Services.GetRequiredService<IOptions<RegistrationReconciliationCutoverOptions>>().Value.Evidence();
        Assert.IsType<ReconciliationRunOutcome.Created>(await runs.CreateAsModeratorAsync(session, order, evidence, key, Ct));
        await s.ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1",
            session.SessionId, s.Clock.GetUtcNow() - RecentAuthentication.Window - TimeSpan.FromTicks(10));

        var replay = await runs.CreateAsModeratorAsync(session, order, evidence, key, Ct);

        Assert.IsType<ReconciliationRunOutcome.Forbidden>(replay);
    }

    [Fact]
    public async Task TheReadReturnsTheRunWithTheAllowlistedFieldsAndTouchesTheSession()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var run = await CreatedRunAsync(moderator, BodyOf(await PreviewAsync(moderator)));
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var now = s.Clock.GetUtcNow();
        var idle = SessionLifetime.For(SessionPolicy.Administrative).Idle;

        using var response = await moderator.GetAsync($"{Path}/{run}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal($"\"reconciliation-run-{run:N}-1\"", response.Headers.ETag?.ToString());
        var body = await MfaScenario.ReadObjectAsync(response);
        Assert.Equal(
            ["authorizationReference", "createdAt", "createdByUserId", "cutoverLower", "cutoverUpper", "eTag", "evidenceId", "id",
                "justification", "selectorVersion", "state", "units", "version"],
            body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(run, Guid.Parse((string)body["id"]!));
        Assert.Single(body["units"]!.AsArray());
        Assert.Equal(1, await s.CountAsync(
            "select count(*) from identity.login_sessions where revoked_at is null and last_seen_at=$1 and idle_expires_at=$2",
            now, now + idle));
    }

    [Fact]
    public async Task AnotherModeratorReadsTheRunWhileAnUnknownOneIsNotFound()
    {
        await using var s = await ScenarioAsync();
        await SuspendAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        using var other = await ModeratorAsync(s, host);
        var run = await CreatedRunAsync(moderator, BodyOf(await PreviewAsync(moderator)));

        using var response = await other.GetAsync($"{Path}/{run}");
        using var unknown = await other.GetAsync($"{Path}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertNothingWrittenAsync(unknown, "NOT_FOUND", HttpStatusCode.NotFound);
    }

    private static async Task WaitUntilBlockedAsync(SuspendedRegistrationScenario s, string queryPart)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = await s.Source.OpenConnectionAsync(timeout.Token);
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                select exists(select 1 from pg_stat_activity
                  where cardinality(pg_blocking_pids(pid))>0 and query like '%'||$1||'%')
                """, connection);
            query.Parameters.AddWithValue(queryPart);
            if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }
            await Task.Delay(10, timeout.Token);
        }
    }
}
