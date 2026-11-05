using Duora.Infrastructure.Participation;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// ADR-0049, persistência: o banco garante sozinho o que a aplicação não pode esquecer. Um run aberto, manifesto
/// imutável, recibo imutável quando terminal e estado terminal nascido só de <c>Pending</c>. Tudo pelo papel
/// <c>duora_app</c>, com dados sintéticos; a retenção de um fato leva o vínculo junto e nunca a unidade.
/// </summary>
public sealed class RegistrationReconciliationRunSchemaTests(PostgresFixture postgres)
{
    private const string Immutable = "23514";
    private const string Unique = "23505";
    private const string ForeignKey = "23503";

    private const string Receipt = """
        update ops.registration_reconciliation_units set state=$2,receipt_reason=$3,receipt_fact_ids='{}',
          receipt_executed_by=$4,receipt_executed_at=now(),receipt_correlation_id='trace-1',
          apply_idempotency_key=$5,apply_intent_hash=sha256('apply'::bytea)
        where id=$1
        """;

    private const string InsertRun = """
        insert into ops.registration_reconciliation_runs(id,selector_version,cutover_evidence_id,cutover_lower,cutover_upper,
          created_by_user_id,justification,authorization_reference,state,version,created_at,idempotency_key,intent_hash)
        values($1,1,'synthetic-321',now()-interval '2 minutes',now()-interval '1 minute',$2,'Sintético','AUTH-1',$3,$4,now(),$5,
          sha256('intent'::bytea))
        """;

    private async Task<Fixture> FixtureAsync()
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        var measure = Guid.NewGuid();
        await s.ExecuteAsync("""
            insert into safety.moderation_actions(id,subject_user_id,measure,reason_code,internal_reason,
              author_kind,decided_at,command_key,command_intent,source_code,source_id)
            values($1,$2,'Suspension','SelfieVerificationFailed','Synthetic legacy fixture',
              'Automated',$3,encode(sha256($1::text::bytea),'hex'),sha256($2::text::bytea),'synthetic',$1)
            """, measure, s.Seed.Alice, s.Clock.GetUtcNow());
        return new Fixture(s, measure);
    }

    private static async Task<Guid> InsertRunAsync(Fixture f, string state = "Open", long version = 1, string? key = null)
    {
        var run = Guid.CreateVersion7();
        await f.S.ExecuteAsync(InsertRun, run, f.S.Seed.Bob, state, version, key ?? Guid.NewGuid().ToString("N"));
        return run;
    }

    /// <summary>Uma unidade só nasce na transação que criou o run: é o fluxo real da criação.</summary>
    private static async Task<(Guid Run, Guid Unit)> CreateRunWithUnitAsync(Fixture f)
    {
        var unit = Guid.Empty;
        var run = await InRunTransactionAsync(f, async (connection, id) => unit = await InsertUnitAsync(connection, f, id));
        return (run, unit);
    }

    private static async Task<Guid> InRunTransactionAsync(Fixture f, Func<NpgsqlConnection, Guid, Task> work)
    {
        await using var connection = await f.S.Source.OpenConnectionAsync(f.S.Token);
        await using var transaction = await connection.BeginTransactionAsync(f.S.Token);
        var run = Guid.CreateVersion7();
        await ParticipationSql.Execute(connection, InsertRun, f.S.Token, run, f.S.Seed.Bob, "Open", 1L, Guid.NewGuid().ToString("N"));
        await work(connection, run);
        await transaction.CommitAsync(f.S.Token);
        return run;
    }

    private static async Task<Guid> InsertUnitAsync(NpgsqlConnection connection, Fixture f, Guid run, string state = "Pending",
        Guid? subject = null, Guid? measure = null)
    {
        var unit = Guid.CreateVersion7();
        await ParticipationSql.Execute(connection, """
            insert into ops.registration_reconciliation_units(id,run_id,subject_user_id,original_measure_id,measure_version,items,state)
            values($1,$2,$3,$4,1,'[{"eventId":"00000000-0000-0000-0000-000000000001"}]'::jsonb,$5)
            """, f.S.Token, unit, run, subject ?? f.S.Seed.Alice, measure ?? f.Measure, state);
        return unit;
    }

    private static async Task InsertUnitInAnotherTransactionAsync(Fixture f, Guid run)
    {
        await using var connection = await f.S.Source.OpenConnectionAsync(f.S.Token);
        await InsertUnitAsync(connection, f, run);
    }

    private static async Task<string> RefusedAsync(Fixture f, string sql, params object[] values)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.S.ExecuteAsync(sql, values));
        return error.SqlState;
    }

    private static async Task TerminalAsync(Fixture f, Guid unit, string state = "Applied", string? reason = null, string? key = null)
    {
        await using var connection = await f.S.Source.OpenConnectionAsync(f.S.Token);
        await using var command = ParticipationSql.Command(connection, Receipt, unit, state, reason ?? (object)DBNull.Value,
            f.S.Seed.Bob, key ?? Guid.NewGuid().ToString("N"));
        command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        await command.ExecuteNonQueryAsync(f.S.Token);
    }

    [Fact]
    public async Task OnlyOneRunIsOpenAtATime()
    {
        await using var f = await FixtureAsync();
        await InsertRunAsync(f);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(f));

        Assert.Equal((Unique, "ux_registration_reconciliation_runs_open"), (error.SqlState, error.ConstraintName));
    }

    [Fact]
    public async Task ClosingTheRunAllowsAnotherOne()
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);
        await f.S.ExecuteAsync("update ops.registration_reconciliation_runs set state='Closed',version=2 where id=$1", run);

        await InsertRunAsync(f);

        Assert.Equal(2, await f.S.CountAsync("select count(*) from ops.registration_reconciliation_runs"));
    }

    [Fact]
    public async Task TheIdempotencyKeyIsUniquePerActor()
    {
        await using var f = await FixtureAsync();
        var first = await InsertRunAsync(f, key: "same-key");
        await f.S.ExecuteAsync("update ops.registration_reconciliation_runs set state='Closed',version=2 where id=$1", first);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(f, key: "same-key"));

        Assert.Equal((Unique, "ux_registration_reconciliation_runs_command"), (error.SqlState, error.ConstraintName));
    }

    [Theory]
    [InlineData("Closed", 1)]
    [InlineData("Open", 2)]
    public async Task ARunBornInAnyOtherShapeIsRefused(string state, long version)
    {
        await using var f = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(f, state, version));

        Assert.Equal(Immutable, error.SqlState);
    }

    [Theory]
    [InlineData("selector_version=2")]
    [InlineData("cutover_evidence_id='other'")]
    [InlineData("cutover_lower=cutover_lower-interval '1 second'")]
    [InlineData("cutover_upper=cutover_upper+interval '1 second'")]
    [InlineData("justification='Outra'")]
    [InlineData("authorization_reference='AUTH-2'")]
    [InlineData("created_at=created_at+interval '1 second'")]
    [InlineData("idempotency_key='other'")]
    [InlineData("intent_hash=sha256('other'::bytea)")]
    [InlineData("id=gen_random_uuid()")]
    public async Task TheManifestOfARunIsImmutable(string assignment)
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);

        Assert.Equal(Immutable, await RefusedAsync(f, $"update ops.registration_reconciliation_runs set {assignment} where id=$1", run));
    }

    [Fact]
    public async Task TheCreatorOfARunIsImmutable()
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);

        Assert.Equal(Immutable, await RefusedAsync(f,
            "update ops.registration_reconciliation_runs set created_by_user_id=$2 where id=$1", run, f.S.Seed.Third));
    }

    [Theory]
    [InlineData("state='Open',version=2")]
    [InlineData("state='Closed',version=1")]
    [InlineData("state='Closed',version=3")]
    public async Task AnOpenRunOnlyClosesWithTheNextVersion(string assignment)
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);

        Assert.Equal(Immutable, await RefusedAsync(f, $"update ops.registration_reconciliation_runs set {assignment} where id=$1", run));
    }

    [Fact]
    public async Task AClosedRunNeverChangesAgain()
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);
        await f.S.ExecuteAsync("update ops.registration_reconciliation_runs set state='Closed',version=2 where id=$1", run);

        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_runs set state='Open',version=3 where id=$1", run));
        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_runs set version=3 where id=$1", run));
    }

    [Theory]
    [InlineData("subject_user_id=gen_random_uuid()")]
    [InlineData("original_measure_id=gen_random_uuid()")]
    [InlineData("measure_version=2")]
    [InlineData("items='[{\"eventId\":\"other\"}]'::jsonb")]
    [InlineData("run_id=gen_random_uuid()")]
    [InlineData("id=gen_random_uuid()")]
    public async Task TheManifestOfAUnitIsImmutable(string assignment)
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);

        Assert.Equal(Immutable, await RefusedAsync(f, $"update ops.registration_reconciliation_units set {assignment} where id=$1", unit));
    }

    [Theory]
    [InlineData("Applied")]
    [InlineData("SkippedChanged")]
    [InlineData("NeedsReview")]
    public async Task AUnitIsNeverBornTerminal(string state)
    {
        await using var f = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InRunTransactionAsync(f, (connection, run) => InsertUnitAsync(connection, f, run, state)));

        Assert.Equal(Immutable, error.SqlState);
    }

    [Theory]
    [InlineData("Applied", null)]
    [InlineData("SkippedChanged", "RegistrationChanged")]
    [InlineData("NeedsReview", "PlanTooLarge")]
    public async Task APendingUnitBecomesTerminalOnceAndItsReceiptFreezes(string state, string? reason)
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);

        await TerminalAsync(f, unit, state, reason);

        Assert.Equal(state, await f.S.TextAsync("select state from ops.registration_reconciliation_units where id=$1", unit));
        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_units set state='Pending' where id=$1", unit));
        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_units set receipt_correlation_id='other' where id=$1", unit));
        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_units set receipt_fact_ids='{}' where id=$1", unit));
        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_units set state='NeedsReview' where id=$1", unit));
    }

    [Theory]
    [InlineData("receipt_correlation_id='early'")]
    [InlineData("receipt_reason='Early'")]
    [InlineData("state='Pending'")]
    public async Task APendingUnitHasNoReceiptNorTransitionToItself(string assignment)
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);

        Assert.Equal(Immutable, await RefusedAsync(f, $"update ops.registration_reconciliation_units set {assignment} where id=$1", unit));
    }

    [Theory]
    [InlineData("Applied", "NotApplied")]
    [InlineData("SkippedChanged", null)]
    [InlineData("NeedsReview", null)]
    [InlineData("Unknown", "Reason")]
    public async Task TheReceiptFollowsTheOutcome(string state, string? reason)
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);

        var error = await Assert.ThrowsAsync<PostgresException>(() => TerminalAsync(f, unit, state, reason));

        Assert.Equal(Immutable, error.SqlState);
    }

    [Fact]
    public async Task AnUnfinishedReceiptIsRefused()
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);

        Assert.Equal(Immutable, await RefusedAsync(f, "update ops.registration_reconciliation_units set state='Applied' where id=$1", unit));
    }

    [Theory]
    [InlineData("receipt_fact_ids")]
    [InlineData("receipt_executed_by")]
    [InlineData("receipt_executed_at")]
    [InlineData("receipt_correlation_id")]
    [InlineData("apply_idempotency_key")]
    [InlineData("apply_intent_hash")]
    public async Task EveryPartOfTheReceiptIsRequired(string column)
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);
        Dictionary<string, string> values = new()
        {
            ["receipt_fact_ids"] = "'{}'",
            ["receipt_executed_by"] = $"'{f.S.Seed.Bob}'",
            ["receipt_executed_at"] = "now()",
            ["receipt_correlation_id"] = "'trace-1'",
            ["apply_idempotency_key"] = "'apply-key'",
            ["apply_intent_hash"] = "sha256('apply'::bytea)",
        };
        values[column] = "null";
        var assignments = string.Join(',', values.Select(entry => $"{entry.Key}={entry.Value}"));

        Assert.Equal(Immutable, await RefusedAsync(f,
            $"update ops.registration_reconciliation_units set state='Applied',{assignments} where id=$1", unit));
    }

    [Fact]
    public async Task AnApplyKeyIsUniquePerActorAcrossUnits()
    {
        await using var f = await FixtureAsync();
        var measure = Guid.NewGuid();
        await f.S.ExecuteAsync("""
            insert into safety.moderation_actions(id,subject_user_id,measure,reason_code,internal_reason,
              author_kind,decided_at,command_key,command_intent,source_code,source_id)
            values($1,$2,'Suspension','SelfieVerificationFailed','Synthetic legacy fixture',
              'Automated',$3,encode(sha256($1::text::bytea),'hex'),sha256($2::text::bytea),'synthetic',$1)
            """, measure, f.S.Seed.Third, f.S.Clock.GetUtcNow());
        var first = Guid.Empty;
        var other = Guid.Empty;
        await InRunTransactionAsync(f, async (connection, run) =>
        {
            first = await InsertUnitAsync(connection, f, run);
            other = await InsertUnitAsync(connection, f, run, subject: f.S.Seed.Third, measure: measure);
        });
        await TerminalAsync(f, first, key: "apply-key");

        var error = await Assert.ThrowsAsync<PostgresException>(() => TerminalAsync(f, other, key: "apply-key"));

        Assert.Equal((Unique, "ux_registration_reconciliation_units_apply"), (error.SqlState, error.ConstraintName));
    }

    /// <summary>Sem predicado, o índice também serve à FK do executor; NULLs continuam distintos entre as pendentes.</summary>
    [Fact]
    public async Task TheApplyIndexHasNoPredicate()
    {
        await using var f = await FixtureAsync();

        Assert.Equal(1, await f.S.CountAsync("""
            select count(*) from pg_index
            where indexrelid='ops.ux_registration_reconciliation_units_apply'::regclass and indpred is null and indisunique
            """));
    }

    [Fact]
    public async Task TheSameUnitCannotAppearTwiceInARun()
    {
        await using var f = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => InRunTransactionAsync(f, async (connection, run) =>
        {
            await InsertUnitAsync(connection, f, run);
            await InsertUnitAsync(connection, f, run);
        }));

        Assert.Equal((Unique, "ux_registration_reconciliation_units_manifest"), (error.SqlState, error.ConstraintName));
    }

    [Fact]
    public async Task NoUnitJoinsARunAfterTheTransactionThatCreatedIt()
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertUnitInAnotherTransactionAsync(f, run));

        Assert.Equal(Immutable, error.SqlState);
        Assert.Equal(0, await f.S.CountAsync("select count(*) from ops.registration_reconciliation_units where run_id=$1", run));
    }

    [Fact]
    public async Task NoUnitJoinsAClosedRun()
    {
        await using var f = await FixtureAsync();
        var run = await InsertRunAsync(f);
        await f.S.ExecuteAsync("update ops.registration_reconciliation_runs set state='Closed',version=2 where id=$1", run);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertUnitInAnotherTransactionAsync(f, run));

        Assert.Equal(Immutable, error.SqlState);
    }

    [Theory]
    [InlineData("'{}'::jsonb")]
    [InlineData("'[]'::jsonb")]
    public async Task AUnitAlwaysHasApprovedItems(string items)
    {
        await using var f = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => InRunTransactionAsync(f, (connection, run) =>
            ParticipationSql.Execute(connection, $"""
                insert into ops.registration_reconciliation_units(id,run_id,subject_user_id,original_measure_id,measure_version,items)
                values(gen_random_uuid(),$1,$2,$3,1,{items})
                """, f.S.Token, run, f.S.Seed.Alice, f.Measure)));

        Assert.Equal(Immutable, error.SqlState);
    }

    [Theory]
    [InlineData("com\nquebra")]
    [InlineData("com\ttab")]
    [InlineData("com\u007fdel")]
    [InlineData("   ")]
    public async Task TheDatabaseAlsoRefusesControlCharactersAndBlankTexts(string text)
    {
        await using var f = await FixtureAsync();

        Assert.Equal(Immutable, await RefusedAsync(f, """
            insert into ops.registration_reconciliation_runs(id,selector_version,cutover_evidence_id,cutover_lower,cutover_upper,
              created_by_user_id,justification,authorization_reference,state,version,created_at,idempotency_key,intent_hash)
            values(gen_random_uuid(),1,'synthetic-321',now(),now(),$1,$2,'AUTH-1','Open',1,now(),'k',sha256('i'::bytea))
            """, f.S.Seed.Bob, text));
    }

    [Theory]
    [InlineData("delete from ops.registration_reconciliation_runs where id=$1", true)]
    [InlineData("delete from ops.registration_reconciliation_units where run_id=$1", true)]
    [InlineData("truncate ops.registration_reconciliation_units cascade", false)]
    public async Task TheApplicationCannotRemoveRunsOrUnits(string sql, bool bound)
    {
        await using var f = await FixtureAsync();
        var (run, _) = await CreateRunWithUnitAsync(f);

        var error = bound
            ? await Assert.ThrowsAsync<PostgresException>(() => f.S.ExecuteAsync(sql, run))
            : await Assert.ThrowsAsync<PostgresException>(() => f.S.ExecuteAsync(sql));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task TheOwnerAlsoCannotDeleteARunOrAUnit()
    {
        await using var f = await FixtureAsync();
        await CreateRunWithUnitAsync(f);
        await using var migrator = new NpgsqlConnection(f.S.Database.MigratorConnectionString);
        await migrator.OpenAsync(f.S.Token);

        await using var units = new NpgsqlCommand("delete from ops.registration_reconciliation_units", migrator);
        await using var runs = new NpgsqlCommand("delete from ops.registration_reconciliation_runs", migrator);

        Assert.Equal(Immutable, (await Assert.ThrowsAsync<PostgresException>(() => units.ExecuteNonQueryAsync(f.S.Token))).SqlState);
        Assert.Equal(Immutable, (await Assert.ThrowsAsync<PostgresException>(() => runs.ExecuteNonQueryAsync(f.S.Token))).SqlState);
    }

    [Fact]
    public async Task RetentionRemovesALinkedFactAndItsLinkButNeverTheUnit()
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);
        var fact = await InsertFactAsync(f);
        await f.S.ExecuteAsync("insert into ops.registration_reconciliation_unit_facts(fact_id,unit_id) values($1,$2)", fact, unit);

        await f.S.ExecuteAsync("delete from ops.outbox_events where id=$1", fact);

        Assert.Equal(0, await f.S.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts where fact_id=$1", fact));
        Assert.Equal(1, await f.S.CountAsync("select count(*) from ops.registration_reconciliation_units where id=$1", unit));
    }

    [Fact]
    public async Task ALinkOnlyPointsToAnExistingFactAndUnit()
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);
        var fact = await InsertFactAsync(f);

        Assert.Equal(ForeignKey, await RefusedAsync(f,
            "insert into ops.registration_reconciliation_unit_facts(fact_id,unit_id) values($1,$2)", Guid.NewGuid(), unit));
        Assert.Equal(ForeignKey, await RefusedAsync(f,
            "insert into ops.registration_reconciliation_unit_facts(fact_id,unit_id) values($1,$2)", fact, Guid.NewGuid()));
    }

    [Fact]
    public async Task TheApplicationCannotRewriteOrRemoveALinkDirectly()
    {
        await using var f = await FixtureAsync();
        var (_, unit) = await CreateRunWithUnitAsync(f);
        var fact = await InsertFactAsync(f);
        await f.S.ExecuteAsync("insert into ops.registration_reconciliation_unit_facts(fact_id,unit_id) values($1,$2)", fact, unit);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await RefusedAsync(f,
            "update ops.registration_reconciliation_unit_facts set unit_id=unit_id where fact_id=$1", fact));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await RefusedAsync(f,
            "delete from ops.registration_reconciliation_unit_facts where fact_id=$1", fact));
    }

    [Theory]
    [InlineData("ops.registration_reconciliation_runs", "delete")]
    [InlineData("ops.registration_reconciliation_runs", "truncate")]
    [InlineData("ops.registration_reconciliation_units", "delete")]
    [InlineData("ops.registration_reconciliation_units", "truncate")]
    [InlineData("ops.registration_reconciliation_unit_facts", "update")]
    [InlineData("ops.registration_reconciliation_unit_facts", "delete")]
    [InlineData("ops.registration_reconciliation_unit_facts", "truncate")]
    public async Task TheDeployReconciliationRevokesAPrivilegeThatCameBack(string table, string privilege)
    {
        await using var f = await FixtureAsync();
        await GrantAsync(f, $"grant {privilege} on table {table} to duora_app");

        await postgres.ReconcilePrivilegesAsync(f.S.Database.Name, f.S.Token);

        Assert.Equal(0, await f.S.CountAsync("select count(*) filter (where has_table_privilege('duora_app',$1,$2)) from (values(1)) v", table, privilege));
    }

    [Theory]
    [InlineData("ops.registration_reconciliation_runs", "delete")]
    [InlineData("ops.registration_reconciliation_units", "truncate")]
    [InlineData("ops.registration_reconciliation_unit_facts", "update")]
    public async Task TheDeployStopsIfThePrivilegeArrivesThroughPublic(string table, string privilege)
    {
        await using var f = await FixtureAsync();
        await GrantAsync(f, $"grant {privilege} on table {table} to public");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => postgres.ReconcilePrivilegesAsync(f.S.Database.Name, f.S.Token));

        Assert.Contains("registration reconciliation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task GrantAsync(Fixture f, string sql)
    {
        await using var migrator = new NpgsqlConnection(f.S.Database.MigratorConnectionString);
        await migrator.OpenAsync(f.S.Token);
        await using var grant = new NpgsqlCommand(sql, migrator);
        await grant.ExecuteNonQueryAsync(f.S.Token);
    }

    private static async Task<Guid> InsertFactAsync(Fixture f)
    {
        var fact = Guid.CreateVersion7();
        await f.S.ExecuteAsync("""
            insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,resource_version,payload,occurred_at)
            values($1,'events.waitlisted_registration_promoted',1,'event',$2,2,'{}'::jsonb,now())
            """, fact, f.S.EventId);
        return fact;
    }

    private sealed record Fixture(SuspendedRegistrationScenario S, Guid Measure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => S.DisposeAsync();
    }
}
