using System.Text.Json;
using Duora.Infrastructure.Audit;
using Npgsql;

namespace Duora.Infrastructure.Geolocation;

/// <summary>
/// Operations metadata only. Destructive retention needs a separately authorized maintenance data source;
/// duora_app retains SELECT/INSERT-only access to manifests, approvals and tombstones.
/// </summary>
public sealed class CountryDatabaseOperationsStore(NpgsqlDataSource source, TimeProvider time)
{
    internal DateTimeOffset UtcNow => time.GetUtcNow();
    internal Task<CountryDatabaseFileLock> LockFilesAsync(CancellationToken ct) => CountryDatabaseFileLock.AcquireAsync(source, ct);

    internal async Task<CountryDatabaseOperationsSnapshot> ReadAsync(CancellationToken ct)
    {
        var observedNow = UtcNow;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Lock(c, ct);
        var versions = new List<CountryDatabaseManifest>();
        var approved = new List<CountryDatabaseManifest>();
        var retired = new HashSet<string>(StringComparer.Ordinal);
        await using (var q = Command(c, """
            select v.manifest::text,a.sha256 is not null,r.sha256 is not null
            from ops.country_database_versions v
            left join ops.country_database_approvals a using(sha256)
            left join ops.country_database_retired r using(sha256)
            """))
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var m = JsonSerializer.Deserialize<CountryDatabaseManifest>(r.GetString(0))!;
                versions.Add(m);
                if (r.GetBoolean(1))
                { approved.Add(m); }
                if (r.GetBoolean(2))
                { retired.Add(m.Sha256); }
            }
        }
        CountryDatabaseOperationsSnapshot snapshot;
        await using (var q = Command(c, """
            select s.active_fencing_token,s.active_sha256,s.last_attempt_at,s.last_error_code,s.blocked,
              s.review_required,s.repair_authorized,s.last_observed_at,
              exists(select 1 from ops.jobs where kind='country-database-update' and status='InFlight' and lease_expires_at>$1),
              exists(select 1 from ops.jobs where kind='country-database-update' and
                ((status='InFlight' and attempts>=4 and lease_expires_at<=$1) or
                 (status='Pending' and attempts=0 and last_error_code is not null and not s.blocked)))
            from ops.country_database_state s where singleton
            """, observedNow))
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            var hash = r.IsDBNull(1) ? null : r.GetString(1);
            snapshot = new(r.GetInt64(0), versions.SingleOrDefault(v => v.Sha256 == hash),
                r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6),
                r.IsDBNull(7) ? observedNow : r.GetFieldValue<DateTimeOffset>(7), r.GetBoolean(8), r.GetBoolean(9),
                approved, versions, retired);
        }
        var highWater = new[] { snapshot.LastObserved, observedNow, snapshot.LastAttempt ?? observedNow }
            .Concat(versions.Select(m => m.ValidatedAt)).Max();
        snapshot = snapshot with { LastObserved = highWater };
        await Execute(c, "update ops.country_database_state set last_observed_at=greatest(last_observed_at,$1) where singleton", ct, highWater);
        await tx.CommitAsync(ct);
        return snapshot;
    }

    internal async Task RecordHealthAsync(CountryDatabaseOperationsSnapshot snapshot, IReadOnlyDictionary<string, int> conditions,
        bool integrityHold, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Lock(c, ct);
        if (!await Matches(c, snapshot.Generation, ct))
        { return; }
        var now = UtcNow < snapshot.LastObserved ? snapshot.LastObserved : UtcNow;
        if (integrityHold && !snapshot.ReviewRequired)
        {
            await Execute(c, "update ops.country_database_state set review_required=true,blocked=true,last_error_code='Integrity' where singleton", ct);
            await Fence(c, ct);
        }
        var existing = new Dictionary<string, (int Severity, DateTimeOffset Notified)>(StringComparer.Ordinal);
        await using (var q = Command(c, "select code,severity,last_notified_at from ops.country_database_incidents"))
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            { existing.Add(r.GetString(0), (r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2))); }
        }
        foreach (var (code, severity) in conditions)
        {
            if (!existing.TryGetValue(code, out var old) || old.Severity != severity || now - old.Notified >= TimeSpan.FromDays(1))
            {
                await Event(c, code, severity, false, now, true, null, ct);
                await Execute(c, """
                    insert into ops.country_database_incidents(code,severity,opened_at,last_notified_at) values($1,$2,$3,$3)
                    on conflict(code) do update set severity=excluded.severity,last_notified_at=excluded.last_notified_at
                    """, ct, code, severity, now);
            }
        }
        foreach (var (code, old) in existing)
        {
            if (UtcNow >= snapshot.LastObserved && !conditions.ContainsKey(code))
            {
                await Event(c, code, old.Severity, true, now, true, null, ct);
                await Execute(c, "delete from ops.country_database_incidents where code=$1", ct, code);
            }
        }
        await tx.CommitAsync(ct);
    }

    internal async Task DeliverAsync(ICountryDatabaseNotifier notifier, CancellationToken ct)
    {
        // A bounded, durable delivery reservation. Transport is outside SQL and must deduplicate event Id.
        for (var i = 0; i < 32; i++)
        {
            CountryDatabaseNotification notification;
            await using (var c = await source.OpenConnectionAsync(ct))
            await using (var q = Command(c, """
                update ops.country_database_notifications set available_at=$2
                where id=(select id from ops.country_database_notifications
                  where outbound and delivered_at is null and available_at<=$1
                  order by id for update skip locked limit 1)
                returning id,code,severity,recovered,occurred_at
                """, UtcNow, UtcNow.AddMinutes(15)))
            await using (var r = await q.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct))
                { return; }
                notification = new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetFieldValue<DateTimeOffset>(4));
            }
            try
            {
                await notifier.SendAsync(notification, ct);
                await using var c = await source.OpenConnectionAsync(ct);
                await Execute(c, "update ops.country_database_notifications set delivered_at=$2 where id=$1", ct, notification.Id, UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // No transport exceptions, payloads or addresses are persisted/logged. Retry no sooner than 15 min.
                return;
            }
        }
    }

    internal async Task<bool> HoldAsync(long generation, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Lock(c, ct);
        if (!await Matches(c, generation, ct))
        { return false; }
        await Execute(c, "update ops.country_database_state set review_required=true,blocked=true,repair_authorized=false,last_error_code='Integrity' where singleton", ct);
        await Fence(c, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    internal async Task<bool> ReleaseAsync(long generation, Guid reviewId, OperatorName actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (reviewId == Guid.Empty)
        { throw new ArgumentException("An opaque review reference is required.", nameof(reviewId)); }
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Lock(c, ct);
        if (!await Matches(c, generation, ct))
        { return false; }
        await using (var q = Command(c, "select last_observed_at from ops.country_database_state where singleton"))
        {
            if (await q.ExecuteScalarAsync(ct) is DateTime observed && UtcNow < new DateTimeOffset(observed))
            { return false; }
        }
        await Execute(c, """
            update ops.country_database_state set blocked=false,review_required=false,repair_authorized=true,
              active_sha256=null,last_error_code=null where singleton
            """, ct);
        await Fence(c, ct);
        await Event(c, "ReviewReleased", 0, false, UtcNow, false, null, ct);
        await Execute(c, """
            insert into ops.audit_events(occurred_at,actor_kind,actor_label,action,target_type,target_id,target_version)
            values($1,'TerminalOperator',$2,'ops.country_database_review_released','country_database',$3,$4)
            """, ct, UtcNow, actor.Value, reviewId, generation);
        await tx.CommitAsync(ct);
        return true;
    }

    internal async Task<bool> RecoverAsync(long generation, CountryDatabaseManifest manifest, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Lock(c, ct);
        if (!manifest.IsAdmissible(UtcNow) || !await Matches(c, generation, ct))
        { return false; }
        await using (var q = Command(c, """
            select 1 from ops.country_database_approvals a join ops.country_database_versions v using(sha256)
            where a.sha256=$1 and v.manifest=$2::jsonb
              and not exists(select 1 from ops.country_database_retired r where r.sha256=a.sha256)
              and exists(select 1 from ops.country_database_state where singleton and not blocked and not review_required
                and (last_observed_at is null or last_observed_at<=$3))
            """, manifest.Sha256, JsonSerializer.Serialize(manifest), UtcNow))
        {
            if (await q.ExecuteScalarAsync(ct) is null)
            { return false; }
        }
        await Fence(c, ct);
        await Execute(c, "update ops.country_database_state set active_sha256=$1,repair_authorized=false,last_observed_at=$2 where singleton", ct,
            manifest.Sha256, UtcNow);
        await Event(c, "Recovered", 0, false, UtcNow, false, manifest.Sha256, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    internal async Task<IReadOnlySet<string>> RetireAsync(CountryDatabaseOperationsSnapshot snapshot, CancellationToken ct)
    {
        var keep = snapshot.Approved.Where(m => !snapshot.Retired.Contains(m.Sha256)
                && m.FirstDownloadedAt <= UtcNow && UtcNow - m.FirstDownloadedAt < TimeSpan.FromDays(14))
            .OrderByDescending(m => m.FirstDownloadedAt).ThenBy(m => m.Sha256, StringComparer.Ordinal).Take(14)
            .Select(m => m.Sha256).ToHashSet(StringComparer.Ordinal);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Lock(c, ct);
        if (!await Matches(c, snapshot.Generation, ct) || UtcNow < snapshot.LastObserved)
        { throw new InvalidOperationException("Maintenance snapshot is no longer valid."); }
        var approvedHashes = snapshot.Approved.Select(m => m.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var m in snapshot.Versions.Where(m => !keep.Contains(m.Sha256)
            && (approvedHashes.Contains(m.Sha256) || UtcNow - m.FirstDownloadedAt >= TimeSpan.FromDays(14))))
        {
            await Execute(c, "insert into ops.country_database_retired(sha256,retired_at) values($1,$2) on conflict do nothing", ct, m.Sha256, UtcNow);
        }
        if (snapshot.Active is { } active && !keep.Contains(active.Sha256))
        {
            await Execute(c, "update ops.country_database_state set active_sha256=null where singleton", ct);
            await Fence(c, ct);
            await Event(c, "Retired", 0, false, UtcNow, false, active.Sha256, ct);
        }
        // Deliberately requires maintenance DELETE privilege; never grant it to duora_app.
        await Execute(c, """
            delete from ops.country_database_notifications where occurred_at<=$1;
            """, ct, UtcNow.AddDays(-90));
        await Execute(c, "delete from ops.country_database_incidents where last_notified_at<=$1", ct, UtcNow.AddDays(-90));
        await Execute(c, "update ops.country_database_incidents set opened_at=greatest(opened_at,$1)", ct, UtcNow.AddDays(-90));
        foreach (var m in snapshot.Versions.Where(m => m.FirstDownloadedAt <= UtcNow.AddDays(-90)))
        {
            await Execute(c, "delete from ops.country_database_approvals where sha256=$1", ct, m.Sha256);
            await Execute(c, "delete from ops.country_database_retired where sha256=$1", ct, m.Sha256);
            await Execute(c, "delete from ops.country_database_versions where sha256=$1", ct, m.Sha256);
        }
        await tx.CommitAsync(ct);
        return keep;
    }

    private static async Task Lock(NpgsqlConnection c, CancellationToken ct)
    {
        await Execute(c, "set local lock_timeout='500ms'; set local statement_timeout='2s'", ct);
        await Execute(c, "select id from ops.jobs where kind='country-database-update' for update", ct);
        await Execute(c, "insert into ops.country_database_state(singleton) values(true) on conflict do nothing", ct);
        await Execute(c, "select singleton from ops.country_database_state where singleton for update", ct);
    }

    private static async Task<bool> Matches(NpgsqlConnection c, long generation, CancellationToken ct)
    {
        await using var q = Command(c, "select 1 from ops.country_database_state where singleton and active_fencing_token=$1", generation);
        return await q.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task Fence(NpgsqlConnection c, CancellationToken ct)
    {
        await Execute(c, """
            update ops.jobs set fencing_token=greatest(fencing_token,
              (select active_fencing_token from ops.country_database_state where singleton))+1,
              status='Pending',attempts=0,lease_expires_at=null
            where kind='country-database-update'
            """, ct);
        await Execute(c, """
            update ops.country_database_state set active_fencing_token=greatest(active_fencing_token+1,
              coalesce((select fencing_token from ops.jobs where kind='country-database-update'),0)) where singleton
            """, ct);
    }

    private static Task<int> Event(NpgsqlConnection c, string code, int severity, bool recovered, DateTimeOffset now,
        bool outbound, string? hash, CancellationToken ct) => Execute(c, """
            insert into ops.country_database_notifications(code,severity,recovered,occurred_at,available_at,outbound,sha256)
            values($1,$2,$3,$4,$4,$5,$6)
            """, ct, code, severity, recovered, now, outbound, hash);

    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c) { CommandTimeout = 3 };
        foreach (var arg in args)
        { q.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value }); }
        return q;
    }

    private static async Task<int> Execute(NpgsqlConnection c, string sql, CancellationToken ct, params object?[] args)
    {
        await using var q = Command(c, sql, args);
        return await q.ExecuteNonQueryAsync(ct);
    }
}
