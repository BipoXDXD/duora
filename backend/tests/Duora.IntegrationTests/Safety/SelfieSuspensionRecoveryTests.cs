using System.Net;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

[Trait("Acceptance", "T-06")]
[Trait("Acceptance", "T-08")]
[Trait("Acceptance", "T-10")]
[Trait("Acceptance", "T-29")]
public sealed partial class SelfieSuspensionRecoveryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PathFor(Guid id) => $"/api/v1/admin/selfie-suspension-recoveries/{id:D}";
    private static object Body(string incident = "INC-319", string reason = "Revisão humana do incidente sintético.") =>
        new { incidentReference = incident, internalReason = reason };

    [Fact]
    public async Task ScrubbedDeliveryWithoutTheAttemptIsRecoveredAndReviewedWithHumanAudit()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        using var diagnosis = await moderator.GetAsync(PathFor(h.Delivery));
        Assert.Equal(HttpStatusCode.OK, diagnosis.StatusCode);
        var snapshot = await MfaScenario.ReadObjectAsync(diagnosis);
        Assert.Equal("Exhausted", (string?)snapshot["standing"]);
        Assert.Equal(h.User.ToString(), (string?)snapshot["subjectUserId"]);
        Assert.DoesNotContain("payload", snapshot.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        using var response = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("incident-319"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await MfaScenario.ReadObjectAsync(response);
        Assert.Equal("Suspended", (string?)result["disposition"]);
        Assert.Equal("Suspended", await h.Text("select status from identity.users where id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1 and author_kind='Human' and author_user_id=$2 and reason_code='SelfieVerificationFailed'", h.User, actor.UserId));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='safety.account_suspended' and actor_kind='Moderator' and actor_id=$1", actor.UserId));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='ops.outbox_failure_resolved' and actor_kind='Moderator' and actor_id=$1 and target_id=$2", actor.UserId, h.Delivery));
        Assert.Equal(1, await h.Count("select count(*) from safety.selfie_suspension_recoveries where delivery_id=$1 and incident_reference='INC-319' and moderator_user_id=$2", h.Delivery, actor.UserId));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and status='Failed' and attempts=10 and reviewed_at is not null", h.Delivery));
        Assert.Equal("{}", await h.Text("select payload::text from ops.outbox_events where id=$1", h.Event));
        Assert.Equal(0, await h.Count("select count(*) from identity.selfie_verifications where id=$1", h.Verification));
    }

    [Fact]
    public async Task StableReplaySurvivesRetentionAndLiftingWithoutApplyingAnotherMeasure()
    {
        await using var h = await Harness.Create(postgres);
        var (_, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        var key = AccountSuspensionScenario.WithKey("stable-incident");
        using var first = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = await MfaScenario.ReadObjectAsync(first);
        var measure = Guid.Parse((string)original["suspensionId"]!);
        using var lift = await moderator.PostJsonAsync(AccountSuspensionScenario.LiftPath(h.User, measure),
            new { internalReason = "Revogação humana confirmada." }, AccountSuspensionScenario.WithIfMatch($"\"suspension-{measure:N}-1\""));
        Assert.Equal(HttpStatusCode.OK, lift.StatusCode);
        await h.Sql("delete from ops.outbox_deliveries where id=$1", h.Delivery);
        await h.Sql("delete from ops.outbox_events where id=$1", h.Event);
        using var repeated = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), key);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var replay = await MfaScenario.ReadObjectAsync(repeated);
        Assert.True((bool)replay["replayed"]!);
        Assert.Equal(original["suspensionId"]!.ToString(), replay["suspensionId"]!.ToString());
        Assert.Equal("PendingEligibility", await h.Text("select status from identity.users where id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        using var changed = await moderator.PostJsonAsync(PathFor(h.Delivery), Body("INC-other"), key);
        await ProblemAssert.IsProblemAsync(changed, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
    }

    [Theory]
    [InlineData("missing-link", HttpStatusCode.Conflict, "SELFIE_RECOVERY_LINK_MISSING")]
    [InlineData("reviewed", HttpStatusCode.Conflict, "SELFIE_RECOVERY_ALREADY_REVIEWED")]
    [InlineData("wrong-type", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("pending", HttpStatusCode.NotFound, "NOT_FOUND")]
    public async Task InvalidOrAlreadyReviewedDeliveriesNeverSuspend(string variant, HttpStatusCode status, string code)
    {
        await using var h = await Harness.Create(postgres);
        if (variant == "missing-link")
        {
            await h.Sql("delete from identity.selfie_failures where verification_id=$1", h.Verification);
        }
        if (variant == "reviewed")
        {
            await h.Sql("update ops.outbox_deliveries set reviewed_at=$2 where id=$1", h.Delivery, h.Time.GetUtcNow());
        }
        if (variant == "wrong-type")
        {
            await h.Sql("update ops.outbox_events set type='other' where id=$1", h.Event);
        }
        if (variant == "pending")
        {
            await h.Sql("update ops.outbox_deliveries set status='Pending' where id=$1", h.Delivery);
        }
        var (_, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        using var response = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("invalid-case"));
        await ProblemAssert.IsProblemAsync(response, status, code);
        Assert.Equal(0, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        Assert.Equal(0, await h.Count("select count(*) from safety.selfie_suspension_recoveries"));
    }

    [Fact]
    public async Task AnotherMeasureRemainsPendingAndDoesNotReviewTheDeadDelivery()
    {
        await using var h = await Harness.Create(postgres);
        var (_, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        await AccountSuspensionScenario.SuspendAsync(moderator, h.User);
        using var response = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("other-measure"));
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "SELFIE_RECOVERY_AWAITING_OTHER_MEASURE");
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and reviewed_at is null", h.Delivery));
        Assert.Equal(0, await h.Count("select count(*) from safety.selfie_suspension_recoveries"));
    }

    [Fact]
    public async Task ACountThatIsNoLongerExhaustedResolvesWithoutSuspending()
    {
        await using var h = await Harness.Create(postgres);
        await h.Sql("delete from identity.selfie_failures where user_id=$1 and verification_id<>$2", h.User, h.Verification);
        var (_, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        using var response = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("count-ended"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NoLongerNeeded", (string?)(await MfaScenario.ReadObjectAsync(response))["disposition"]);
        Assert.Equal(0, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and reviewed_at is not null", h.Delivery));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ScratchDatabase _database;
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        public DuoraWebFactory Factory { get; }
        public AccountSuspensionScenario Scenario { get; }
        public Guid User { get; private set; }
        public Guid Delivery { get; } = Guid.NewGuid();
        public Guid Event { get; } = Guid.NewGuid();
        public Guid Verification { get; } = Guid.NewGuid();
        public string ConnectionString => _database.AppConnectionString;
        private Harness(ScratchDatabase database)
        {
            _database = database;
            Factory = new(database.AppConnectionString) { Time = Time };
            Scenario = new(database.AppConnectionString, Factory, Time);
        }
        public static async Task<Harness> Create(PostgresFixture postgres)
        {
            var h = new Harness(await postgres.CreateMigratedScratchDatabaseAsync(Ct));
            h.User = (await h.Scenario.Identity.CreateConfirmedAccountAsync()).UserId;
            await h.Sql("""
                insert into identity.selfie_failures(verification_id,user_id,failed_at)
                select case when n=10 then $2 else gen_random_uuid() end,$1,$3-interval '2 hours'+n*interval '1 minute'
                from generate_series(1,10) n
                """, h.User, h.Verification, h.Time.GetUtcNow());
            await h.Sql("""
                insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,payload,occurred_at)
                values($1,'identity.selfie-attempts-exhausted',1,'selfie_verification',$2,'{}',$3-interval '40 days')
                """, h.Event, h.Verification, h.Time.GetUtcNow());
            await h.Sql("""
                insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,attempts,available_at,deadline_at,
                  fencing_token,created_at,completed_at)
                values($1,$2,'selfie-attempts-suspension','synthetic','Failed',10,$3,$3+interval '3650 days',10,
                  $3-interval '40 days',$3-interval '31 days')
                """, h.Delivery, h.Event, h.Time.GetUtcNow());
            return h;
        }
        public async Task UseSubject(Guid user)
        {
            await Sql("delete from identity.selfie_failures where user_id=$1", User);
            User = user;
            await Sql("""
                insert into identity.selfie_failures(verification_id,user_id,failed_at)
                select case when n=10 then $2 else gen_random_uuid() end,$1,$3-interval '2 hours'+n*interval '1 minute'
                from generate_series(1,10) n
                """, User, Verification, Time.GetUtcNow());
        }
        public Task Sql(string sql, params object[] args) => Scenario.ExecuteAsync(sql, args);
        public Task<long> Count(string sql, params object[] args) => Scenario.CountAsync(sql, args);
        public Task<string> Text(string sql, params object[] args) => Scenario.TextAsync(sql, args);
        public async Task AdminSql(string sql)
        {
            await using var connection = new NpgsqlConnection(_database.MigratorConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await _database.DisposeAsync();
        }
    }
}
