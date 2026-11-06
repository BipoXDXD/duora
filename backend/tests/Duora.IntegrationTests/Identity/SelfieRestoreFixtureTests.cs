using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #93: os arquivos que o ensaio de restore do sombra usa (scripts/deploy/c2), contra o esquema real migrado.
/// Só dados sintéticos em banco descartável. O ensaio real, com pgBackRest e repositório S3, roda no sombra.
/// </summary>
public sealed class SelfieRestoreFixtureTests(SelfieRestoreDatabase seeded) : IClassFixture<SelfieRestoreDatabase>
{
    private const string RestoredDataDirectory = "/tmp/duora-selfie-restore";
    private const string RestoredPort = "55433";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeedWritesOneAccountWithHistoryAndEveryStateOnTheRealSchema()
    {
        await using var connection = new NpgsqlConnection(seeded.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select v.state, count(*)::int
            from identity.selfie_verifications v
            where v.policy_version = 'c2-restore-fixture'
            group by v.state
            order by v.state
            """, connection);
        var rows = new Dictionary<string, int>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        Assert.Equal(new Dictionary<string, int>
        {
            ["Capturing"] = 1,
            ["Expired"] = 1,
            ["Inconclusive"] = 1,
            ["Passed"] = 2,
            ["Pending"] = 1,
            ["Processing"] = 1,
            ["RecaptureRequired"] = 1,
        }, rows);
        Assert.Equal(7L, await ScalarAsync(connection,
            "select count(*) from identity.users where email like 'selfie-restore-%@c2.invalid'"));
        Assert.Equal(2L, await ScalarAsync(connection,
            "select count(*) from identity.selfie_verifications v join identity.users u on u.id = v.user_id " +
            "where u.email = 'selfie-restore-1@c2.invalid'"));
    }

    [Fact]
    public async Task CheckAcceptsTheIntactFixture()
    {
        await ExecuteAsync(seeded.SuperuserConnectionString, SelfieRestoreDatabase.ReadSql("selfie-restore-check.sql"));
    }

    [Theory]
    [InlineData("row lost", "rows differ", "delete from identity.selfie_verifications where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("row added to a fixture account", "rows differ", "insert into identity.selfie_verifications(id,user_id,state,policy_version,requested_at,expires_at,version) values('c2000000-0000-4000-8000-00000000b0ff','c2000000-0000-4000-8000-00000000a002','Expired','other','2099-01-01 00:00:00+00','2099-01-01 00:00:00+00',0)")]
    [InlineData("row with the fixture policy elsewhere", "rows differ", "insert into identity.users(id,status,created_at,user_name,normalized_user_name,email,normalized_email,email_confirmed,security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count) values('c2000000-0000-4000-8000-00000000a0ff','Active','2026-11-05 12:00:00+00','other@c2.invalid','OTHER@C2.INVALID','other@c2.invalid','OTHER@C2.INVALID',true,'s','c',false,false,true,0); insert into identity.selfie_verifications(id,user_id,state,policy_version,requested_at,expires_at,version) values('c2000000-0000-4000-8000-00000000b0ff','c2000000-0000-4000-8000-00000000a0ff','Expired','c2-restore-fixture','2099-01-01 00:00:00+00','2099-01-01 00:00:00+00',0)")]
    [InlineData("state", "rows differ", "update identity.selfie_verifications set state = 'Expired' where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("policy", "rows differ", "update identity.selfie_verifications set policy_version = 'other' where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("provider reference", "rows differ", "update identity.selfie_verifications set provider_reference = null where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("provider reference that was empty", "rows differ", "update identity.selfie_verifications set provider_reference = 'late' where id = 'c2000000-0000-4000-8000-00000000b003'")]
    [InlineData("feedback", "rows differ", "update identity.selfie_verifications set feedback_code = null where id = 'c2000000-0000-4000-8000-00000000b001'")]
    [InlineData("requested at, one microsecond", "rows differ", "update identity.selfie_verifications set requested_at = requested_at + interval '1 microsecond' where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("expires at, one microsecond", "rows differ", "update identity.selfie_verifications set expires_at = expires_at - interval '1 microsecond' where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("completed at", "rows differ", "update identity.selfie_verifications set completed_at = null where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("version", "rows differ", "update identity.selfie_verifications set version = version + 1 where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("attempt moved to another account", "rows differ", "update identity.selfie_verifications set user_id = 'c2000000-0000-4000-8000-00000000a002' where id = 'c2000000-0000-4000-8000-00000000b002'")]
    [InlineData("account gone with its attempts", "rows differ", "delete from identity.users where id = 'c2000000-0000-4000-8000-00000000a001'")]
    [InlineData("account e-mail", "rows differ", "update identity.users set email = 'other@c2.invalid', normalized_email = 'OTHER@C2.INVALID', user_name = 'other@c2.invalid', normalized_user_name = 'OTHER@C2.INVALID' where id = 'c2000000-0000-4000-8000-00000000a001'")]
    [InlineData("orphan attempts", "rows differ", "set local session_replication_role = replica; delete from identity.users where id = 'c2000000-0000-4000-8000-00000000a001'")]
    [InlineData("foreign key gone", "constraints differ", "alter table identity.selfie_verifications drop constraint fk_selfie_verifications_users")]
    [InlineData("foreign key without cascade", "constraints differ", "alter table identity.selfie_verifications drop constraint fk_selfie_verifications_users; alter table identity.selfie_verifications add constraint fk_selfie_verifications_users foreign key (user_id) references identity.users (id) on delete restrict")]
    [InlineData("state constraint gone", "constraints differ", "alter table identity.selfie_verifications drop constraint ck_selfie_state")]
    [InlineData("one open attempt index gone", "indexes differ", "drop index identity.ux_selfie_open_user")]
    [InlineData("provider reference index gone", "indexes differ", "drop index identity.ux_selfie_provider_reference")]
    [InlineData("invalid index", "indexes differ", "update pg_index set indisvalid = false where indexrelid = 'identity.ix_selfie_user_requested_at'::regclass")]
    public async Task CheckRejectsDamage(string damage, string reported, string sabotage)
    {
        await using var connection = new NpgsqlConnection(seeded.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var breaking = new NpgsqlCommand(sabotage, connection, transaction))
        {
            await breaking.ExecuteNonQueryAsync(Ct);
        }
        await using var check = new NpgsqlCommand(SelfieRestoreDatabase.ReadSql("selfie-restore-check.sql"), connection, transaction);

        var error = await Assert.ThrowsAsync<PostgresException>(() => check.ExecuteNonQueryAsync(Ct));

        Assert.True(error.MessageText.Contains(reported, StringComparison.Ordinal), $"{damage}: {error.MessageText}");
    }

    [Fact]
    public async Task PhysicalBackupRestoresEveryRowAndIgnoresDamageMadeAfterIt()
    {
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        await using var database = await isolated.CreateMigratedScratchDatabaseAsync(Ct);
        var administrator = isolated.ScratchSuperuserConnectionString(database);
        var check = SelfieRestoreDatabase.ReadSql("selfie-restore-check.sql");
        await ExecuteAsync(administrator, SelfieRestoreDatabase.ReadSql("selfie-restore-seed.sql"));
        await ExecuteAsync(administrator, check);

        await ExecuteAsPostgresAsync(isolated, "pg_basebackup", "--pgdata", RestoredDataDirectory, "--wal-method=stream",
            "--checkpoint=fast", "--username", "postgres");
        await ExecuteAsync(administrator, "delete from identity.selfie_verifications where policy_version = 'c2-restore-fixture'");
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(administrator, check));
        await ExecuteAsPostgresAsync(isolated, "pg_ctl", "--pgdata", RestoredDataDirectory, "--wait", "--log",
            RestoredDataDirectory + ".log", "--options", $"-p {RestoredPort} -k /tmp -c listen_addresses=''", "start");

        await RestoredPsqlAsync(isolated, database, check);
        var rows = await RestoredPsqlAsync(isolated, database, "select count(*) from identity.selfie_verifications");

        Assert.Equal("8", rows.Trim());
    }

    private static Task<string> RestoredPsqlAsync(PostgresFixture server, ScratchDatabase database, string sql) =>
        ExecuteAsPostgresAsync(server, "psql", "--no-psqlrc", "--quiet", "--tuples-only", "--no-align", "--host", "/tmp",
            "--port", RestoredPort, "--username", "postgres", "--dbname", database.Name, "--set", "ON_ERROR_STOP=1",
            "--command", sql);

    private static async Task<string> ExecuteAsPostgresAsync(PostgresFixture server, params string[] command)
    {
        var result = await server.ExecuteInContainerAsync(["gosu", "postgres", .. command], Ct);
        Assert.True(result.ExitCode == 0, $"{command[0]} terminou com {result.ExitCode}: {result.Stderr}");
        return result.Stdout;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct);
    }
}

/// <summary>Banco migrado e semeado uma vez; cada caso de dano roda numa transação desfeita.</summary>
public sealed class SelfieRestoreDatabase(PostgresFixture postgres) : IAsyncLifetime
{
    private ScratchDatabase? _database;

    internal string SuperuserConnectionString => postgres.ScratchSuperuserConnectionString(
        _database ?? throw new InvalidOperationException("O banco ainda não foi criado."));

    internal static string ReadSql(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "scripts", "deploy", "c2", fileName));

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateMigratedScratchDatabaseAsync(TestContext.Current.CancellationToken);
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var seed = new NpgsqlCommand(ReadSql("selfie-restore-seed.sql"), connection);
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
