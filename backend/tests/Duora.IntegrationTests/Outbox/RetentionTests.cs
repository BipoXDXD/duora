extern alias worker;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Participation;
using Duora.Web.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Outbox;

public sealed class RetentionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyOldTerminalRowsAreDeletedAndFailedNeedsExplicitReview()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var old = h.Time.GetUtcNow().AddDays(-31);
        var delivered = await h.Delivery("Delivered", old);
        var expired = await h.Delivery("Expired", old);
        var failed = await h.Delivery("Failed", old);
        var boundary = await h.Delivery("Delivered", h.Time.GetUtcNow().AddDays(-7));
        var pending = await h.Delivery("Pending", old);
        var inFlight = await h.Delivery("InFlight", old);
        var missingDate = await h.Delivery("Delivered", null);
        var receipt = await h.Receipt("Processed", old);
        var rejected = await h.Receipt("Rejected", old);
        var open = await h.Receipt("Received", null);
        var receiptBoundary = await h.Receipt("Processed", h.Time.GetUtcNow().AddDays(-30));
        var receiptMissingDate = await h.Receipt("Rejected", null);
        await h.Run();
        Assert.Equal(0, await h.Exists("outbox_deliveries", delivered));
        Assert.Equal(0, await h.Exists("outbox_deliveries", expired));
        foreach (var id in new[] { failed, boundary, pending, inFlight, missingDate })
        {
            Assert.Equal(1, await h.Exists("outbox_deliveries", id));
        }
        Assert.Equal(0, await h.Exists("webhook_receipts", receipt));
        Assert.Equal(0, await h.Exists("webhook_receipts", rejected));
        foreach (var id in new[] { open, receiptBoundary, receiptMissingDate })
        {
            Assert.Equal(1, await h.Exists("webhook_receipts", id));
        }
        Assert.True(OperatorName.TryParse("tester", out var actor));
        Assert.True(await h.Service.ReviewFailureAsync(failed, actor, FailureResolution.Resolved, Ct));
        Assert.False(await h.Service.ReviewFailureAsync(failed, actor, FailureResolution.Resolved, Ct));
        Assert.False(await h.Service.ReviewFailureAsync(pending, actor, FailureResolution.Resolved, Ct));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='ops.outbox_failure_resolved'"));
        h.Time.Advance(TimeSpan.FromDays(1));
        await h.Run();
        Assert.Equal(0, await h.Exists("outbox_deliveries", failed));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='ops.outbox_failure_resolved'"));
    }

    [Fact]
    public async Task BatchIsBoundedAndRecurringIdentitySurvivesCatchUp()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.Sql("""
            insert into ops.webhook_receipts(id,provider,provider_event_id,received_at,signature_verified_at,processed_at,payload_sha256,payload,state)
            select gen_random_uuid(),'test',n::text,$1,$1,$1,decode(repeat('00',32),'hex'),'{}','Processed' from generate_series(1,1001) n
            """, h.Time.GetUtcNow().AddDays(-31));
        var first = await h.Service.ClaimAsync(Ct);
        Assert.NotNull(first);
        Assert.Null(await h.Service.ClaimAsync(Ct));
        Assert.True(await h.Service.ExecuteAsync(first, Ct));
        Assert.Equal(1, await h.Count("select count(*) from ops.webhook_receipts"));
        var second = await h.Service.ClaimAsync(Ct);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.True(second.Token > first.Token);
        Assert.False(await h.Service.ExecuteAsync(first, Ct));
        Assert.True(await h.Service.ExecuteAsync(second, Ct));
        Assert.Equal(0, await h.Count("select count(*) from ops.webhook_receipts"));
        Assert.Null(await h.Service.ClaimAsync(Ct));
        h.Time.Advance(TimeSpan.FromDays(10));
        await h.Run();
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where kind='retention'"));
        Assert.Null(await h.Service.ClaimAsync(Ct));
    }

    [Fact]
    public async Task AStaleLeaseCannotDeleteAndAPartialEventKeepsItsOpenConsumer()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery("Delivered", h.Time.GetUtcNow().AddDays(-8));
        await h.Sql("""
            insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,attempts,available_at,deadline_at,fencing_token,created_at)
            select gen_random_uuid(),outbox_event_id,'other','other','Pending',0,$2,$2,0,$2 from ops.outbox_deliveries where id=$1
            """, delivery, h.Time.GetUtcNow());
        var stale = await h.Service.ClaimAsync(Ct);
        Assert.NotNull(stale);
        h.Time.Advance(TimeSpan.FromSeconds(61));
        var current = await h.Service.ClaimAsync(Ct);
        Assert.NotNull(current);
        Assert.False(await h.Service.ExecuteAsync(stale, Ct));
        Assert.Equal(1, await h.Exists("outbox_deliveries", delivery));
        Assert.True(await h.Service.ExecuteAsync(current, Ct));
        Assert.Equal(0, await h.Exists("outbox_deliveries", delivery));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_events"));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where status='Pending'"));
    }

    [Fact]
    public async Task ParallelClaimsAndParticipationCannotStealTheRecurringJob()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var claims = await Task.WhenAll(h.Service.ClaimAsync(Ct), h.Service.ClaimAsync(Ct));
        var owned = Assert.Single(claims, job => job is not null)!;
        Assert.Null(await new ParticipationService(h.Source, h.Time).ClaimJobAsync(Ct));
        Assert.True(await h.Service.ExecuteAsync(owned, Ct));
        h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Null(await new ParticipationService(h.Source, h.Time).ClaimJobAsync(Ct));
        Assert.NotNull(await h.Service.ClaimAsync(Ct));
    }

    [Fact]
    public async Task FailureAfterDeletionRollsBackRowsAuditAndSchedule()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery("Delivered", h.Time.GetUtcNow().AddDays(-8));
        var job = await h.Service.ClaimAsync(Ct);
        Assert.NotNull(job);
        var admin = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString)
        { Database = new NpgsqlConnectionStringBuilder(h.ConnectionString).Database };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            create function ops.reject_retention_completion() returns trigger language plpgsql as $$
            begin if new.kind='retention' and new.status='Pending' then raise exception 'synthetic rollback'; end if; return new; end $$;
            create trigger reject_retention_completion before update on ops.jobs for each row execute function ops.reject_retention_completion();
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<PostgresException>(() => h.Service.ExecuteAsync(job, Ct));
        Assert.Equal(1, await h.Exists("outbox_deliveries", delivery));
        Assert.Equal(0, await h.Count("select count(*) from ops.audit_events"));
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where status='InFlight'"));
        command.CommandText = "drop trigger reject_retention_completion on ops.jobs";
        await command.ExecuteNonQueryAsync(Ct);
        Assert.True(await h.Service.ExecuteAsync(job, Ct));
        Assert.Equal(0, await h.Exists("outbox_deliveries", delivery));
    }

    [Fact]
    public async Task ExhaustedJobsRequireAnAuditedOperatorResume()
    {
        await using var h = await Harness.CreateAsync(postgres);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Assert.NotNull(await h.Service.ClaimAsync(Ct));
            h.Time.Advance(TimeSpan.FromSeconds(61));
        }
        Assert.Null(await h.Service.ClaimAsync(Ct));
        Assert.Equal(1, (await h.Service.ObserveAsync(Ct)).FailedJobs);
        using var services = new ServiceCollection().AddSingleton(h.Service).BuildServiceProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await RetentionCommand.ExecuteAsync(services, ["retention", "resume", "--operator", "invalid actor"], output, error, Ct));
        Assert.Equal(0, await RetentionCommand.ExecuteAsync(services, ["retention", "resume", "--operator", "tester"], output, error, Ct));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='ops.retention_resumed' and actor_label='tester'"));
        Assert.NotNull(await h.Service.ClaimAsync(Ct));
    }

    [Fact]
    public async Task ReviewCommandDoesNotResetTheAgeOrHideUnreviewedFailures()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var failed = await h.Delivery("Failed", h.Time.GetUtcNow().AddDays(-31));
        var recent = await h.Delivery("Failed", h.Time.GetUtcNow().AddDays(-1));
        Assert.Equal(1, (await h.Service.ObserveAsync(Ct)).UnreviewedFailures);
        using var services = new ServiceCollection().AddSingleton(h.Service).BuildServiceProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        foreach (var id in new[] { failed, recent })
        {
            Assert.Equal(0, await RetentionCommand.ExecuteAsync(services,
                ["retention", "review-delivery", "--id", id.ToString(), "--operator", "tester", "--resolution", "accepted-loss"], output, error, Ct));
        }
        await h.Run();
        Assert.Equal(0, await h.Exists("outbox_deliveries", failed));
        Assert.Equal(1, await h.Exists("outbox_deliveries", recent));
        Assert.Equal(0, (await h.Service.ObserveAsync(Ct)).UnreviewedFailures);
        Assert.Equal(2, await h.Count("select count(*) from ops.audit_events where action='ops.outbox_failure_loss_accepted' and actor_label='tester'"));
    }

    [Fact]
    public async Task OnlyDoneJobsStrictlyOlderThanSevenDaysAreRemoved()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.Sql("""
            insert into ops.jobs(id,kind,aggregate_id,due_at,available_at,status,completed_at)
            values(gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Done',$1-interval '8 days'),
              (gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Done',$1-interval '7 days'),
              (gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Failed',$1-interval '40 days'),
              (gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Pending',null)
            """, h.Time.GetUtcNow());
        await h.Run();
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where status='Done'"));
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where status='Failed'"));
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where kind='retention'"));
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where kind='close-round' and status='Pending'"));
    }

    [Fact]
    public async Task ExplicitRetriesBackOffAndStopAtEightAttempts()
    {
        await using var h = await Harness.CreateAsync(postgres);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var job = await h.Service.ClaimAsync(Ct);
            Assert.NotNull(job);
            await h.Service.RetryAsync(job, Ct);
            Assert.Null(await h.Service.ClaimAsync(Ct));
            h.Time.Advance(TimeSpan.FromSeconds(14));
            Assert.Null(await h.Service.ClaimAsync(Ct));
            h.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Null(await h.Service.ClaimAsync(Ct));
        Assert.Equal(1, await h.Count("select count(*) from ops.jobs where status='Failed' and attempts=8"));
    }

    [Fact]
    public async Task AnAttachmentCommittedAfterTheCleanupSnapshotIsNeverCascaded()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery("Delivered", h.Time.GetUtcNow().AddDays(-8));
        var job = await h.Service.ClaimAsync(Ct);
        Assert.NotNull(job);
        await using var admin = await h.AdminConnection(postgres);
        await using var setup = new NpgsqlCommand("""
            create function ops.pause_event_candidate() returns boolean language plpgsql as $$
            begin perform pg_advisory_xact_lock(460077); return true; end $$;
            alter table ops.outbox_events enable row level security;
            create policy pause_candidate on ops.outbox_events using (ops.pause_event_candidate());
            select pg_advisory_lock(460077);
            """, admin);
        await setup.ExecuteNonQueryAsync(Ct);
        // Resolve the event before cleanup removes the old delivery.
        setup.CommandText = "select outbox_event_id from ops.outbox_deliveries where id=$1";
        setup.Parameters.AddWithValue(delivery);
        var evt = (Guid)(await setup.ExecuteScalarAsync(Ct))!;
        setup.Parameters.Clear();
        var cleanup = h.Service.ExecuteAsync(job, Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            setup.CommandText = "select count(*) from pg_locks where locktype='advisory' and objid=460077 and not granted";
            while ((long)(await setup.ExecuteScalarAsync(timeout.Token))! == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
            // A privileged synthetic writer bypasses the test-only RLS barrier, as the snapshot has already begun.
            setup.CommandText = """
                insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,attempts,available_at,deadline_at,fencing_token,created_at)
                values($1,$2,'late','late','Pending',0,$3,$3,0,$3)
                """;
            setup.Parameters.AddWithValue(Guid.NewGuid());
            setup.Parameters.AddWithValue(evt);
            setup.Parameters.AddWithValue(h.Time.GetUtcNow());
            await setup.ExecuteNonQueryAsync(Ct);
        }
        finally
        {
            setup.Parameters.Clear();
            setup.CommandText = "select pg_advisory_unlock(460077)";
            await setup.ExecuteNonQueryAsync(Ct);
        }
        Assert.True(await cleanup);
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where consumer='late' and status='Pending'"));
        Assert.Equal(1, await h.Exists("outbox_events", evt));
    }

    [Fact]
    public async Task WorkerStillCleansWhenMonitoringFails()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery("Delivered", h.Time.GetUtcNow().AddDays(-8));
        await using var admin = await h.AdminConnection(postgres);
        await using var setup = new NpgsqlCommand("""
            create function ops.reject_observation() returns boolean language plpgsql as $$
            begin if current_query() like '%min(occurred_at)%' then raise exception 'synthetic observation failure'; end if; return true; end $$;
            alter table ops.outbox_events enable row level security;
            create policy fail_observation on ops.outbox_events using (ops.reject_observation());
            """, admin);
        await setup.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<PostgresException>(() => h.Service.ObserveAsync(Ct));
        var builder = WorkerHost.CreateBuilder([
            "--environment", "Testing", $"--ConnectionStrings:Duora={h.ConnectionString}",
            "--Duora:PublicOrigin=https://duora.test", "--Email:PickupDirectory=/tmp/duora-unused-retention-test",
            "--Retention:Enabled=true"]);
        builder.Services.AddSingleton<TimeProvider>(h.Time);
        using var host = builder.Build();
        var worker = Assert.Single(host.Services.GetServices<IHostedService>().OfType<RetentionJobService>());
        await worker.StartAsync(Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (await h.Exists("outbox_deliveries", delivery) != 0)
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerCompositionHonorsTheExplicitRolloutGate(bool enabled)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery("Delivered", h.Time.GetUtcNow().AddDays(-8));
        var builder = WorkerHost.CreateBuilder([
            "--environment", "Testing", $"--ConnectionStrings:Duora={h.ConnectionString}",
            "--Duora:PublicOrigin=https://duora.test", "--Email:PickupDirectory=/tmp/duora-unused-retention-test",
            $"--Retention:Enabled={enabled}"]);
        builder.Services.AddSingleton<TimeProvider>(h.Time);
        using var host = builder.Build();
        var worker = Assert.Single(host.Services.GetServices<IHostedService>().OfType<RetentionJobService>());
        await worker.StartAsync(Ct);
        try
        {
            if (!enabled)
            {
                await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
                Assert.Equal(1, await h.Exists("outbox_deliveries", delivery));
                Assert.Equal(0, await h.Count("select count(*) from ops.jobs where kind='retention'"));
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                while (await h.Exists("outbox_deliveries", delivery) != 0)
                {
                    await Task.Delay(20, timeout.Token);
                }
                Assert.Equal(1, await h.Count("select count(*) from ops.jobs where kind='retention'"));
            }
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    private sealed class Harness(NpgsqlDataSource source) : IAsyncDisposable
    {
        public required string ConnectionString { get; init; }
        private string MigratorConnectionString { get; init; } = "";
        public NpgsqlDataSource Source => source;
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public RetentionService Service => new(source, Time);
        public static async Task<Harness> CreateAsync(PostgresFixture fixture)
        {
            var connectionString = await fixture.CreateMigratedDatabaseAsync(Ct);
            var migrator = new NpgsqlConnectionStringBuilder(fixture.MigratorConnectionString)
            { Database = new NpgsqlConnectionStringBuilder(connectionString).Database };
            return new(NpgsqlDataSource.Create(connectionString))
            { ConnectionString = connectionString, MigratorConnectionString = migrator.ConnectionString };
        }
        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            // Each case owns a scratch database. Release its migration pool instead of retaining idle slots
            // until the global pool timer runs; never clear another case's/shared fixture pool.
            await using var migrator = new NpgsqlConnection(MigratorConnectionString);
            NpgsqlConnection.ClearPool(migrator);
        }
        public async Task<NpgsqlConnection> AdminConnection(PostgresFixture fixture)
        {
            var settings = new NpgsqlConnectionStringBuilder(fixture.MigratorConnectionString)
            { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database };
            var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(Ct);
            return connection;
        }
        public async Task Run()
        {
            var job = await Service.ClaimAsync(Ct);
            Assert.NotNull(job);
            Assert.True(await Service.ExecuteAsync(job, Ct));
        }
        public async Task Sql(string sql, params object?[] args)
        {
            await using var cmd = source.CreateCommand(sql);
            foreach (var arg in args)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
            }
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        public async Task<long> Count(string sql)
        {
            await using var cmd = source.CreateCommand(sql);
            return (long)(await cmd.ExecuteScalarAsync(Ct))!;
        }
        public async Task<long> Exists(string table, Guid id)
        {
            await using var cmd = source.CreateCommand($"select count(*) from ops.{table} where id=$1");
            cmd.Parameters.AddWithValue(id);
            return (long)(await cmd.ExecuteScalarAsync(Ct))!;
        }
        public async Task<Guid> Delivery(string status, DateTimeOffset? completed)
        {
            var id = Guid.NewGuid();
            var evt = Guid.NewGuid();
            var now = Time.GetUtcNow().AddDays(-40);
            await Sql("insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,payload,occurred_at) values($1,'test',1,'test',$1,'{}',$2)", evt, now);
            await Sql("""
                insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,attempts,available_at,deadline_at,fencing_token,created_at,completed_at,lease_expires_at)
                values($1,$2,'test','test',$3,0,$4,$4,0,$4,$5,case when $3='InFlight' then $4 else null end)
                """, id, evt, status, now, completed);
            return id;
        }
        public async Task<Guid> Receipt(string state, DateTimeOffset? processed)
        {
            var id = Guid.NewGuid();
            await Sql("""
                insert into ops.webhook_receipts(id,provider,provider_event_id,received_at,signature_verified_at,processed_at,payload_sha256,payload,state)
                values($1,'test',$2,$3,$3,$4,decode(repeat('00',32),'hex'),'{}',$5)
                """, id, id.ToString(), Time.GetUtcNow().AddDays(-40), processed, state);
            return id;
        }
    }
}
