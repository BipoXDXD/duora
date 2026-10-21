using System.Text.Json;
using Duora.Core.Geolocation;
using Npgsql;

namespace Duora.Infrastructure.Geolocation;

public sealed record CountryDatabaseUpdateLease(Guid Id, long Token, int Attempt, DateTimeOffset DueAt);
public enum CountryDatabasePromotion { Promoted, Unchanged, LostLease, RequiresApproval, InvalidManifest }

/// <summary>Only this consumer claims its singleton kind. Downloads never run under a SQL transaction.</summary>
public sealed class CountryDatabaseUpdateStore(NpgsqlDataSource source, TimeProvider time, TimeSpan leaseDuration)
{
    public async Task<CountryDatabaseUpdateLease?> ClaimAsync(CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var now = time.GetUtcNow();
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Limits(c, ct);
        await Execute(c, """
            insert into ops.jobs(id,kind,aggregate_id,due_at,available_at)
            values($1,'country-database-update','00000000-0000-7000-8000-000000000108',$2,$2) on conflict do nothing
            """, ct, Guid.CreateVersion7(), CountryDatabaseSchedule.LatestDue(now));
        Guid id;
        int attempts;
        DateTimeOffset due;
        bool expired;
        await using (var q = Command(c, """
            select id,attempts,due_at,status='InFlight' from ops.jobs
            where kind='country-database-update' and
              ((status='Pending' and available_at<=$1) or (status='InFlight' and lease_expires_at<=$1))
            for update skip locked
            """, now))
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
            { return null; }
            id = r.GetGuid(0);
            attempts = r.GetInt32(1);
            due = r.GetFieldValue<DateTimeOffset>(2);
            expired = r.GetBoolean(3);
        }
        await Execute(c, "insert into ops.country_database_state(singleton) values(true) on conflict do nothing", ct);
        await using (var q = Command(c, "select blocked from ops.country_database_state where singleton for update"))
        {
            if ((bool)(await q.ExecuteScalarAsync(ct))!)
            { return null; }
        }
        var latest = CountryDatabaseSchedule.LatestDue(now);
        if (due < latest)
        {
            due = latest;
            attempts = 0;
            await Execute(c, "update ops.country_database_state set first_failure_at=null where singleton", ct);
        }
        if (attempts >= 4)
        {
            await EndCycle(c, id, now, "LeaseExpiredExhausted", false, ct);
            await tx.CommitAsync(ct);
            return null;
        }
        if (expired)
        {
            await Execute(c, "update ops.country_database_state set first_failure_at=coalesce(first_failure_at,$1) where singleton", ct, now);
        }
        await using var claim = Command(c, """
            update ops.jobs set fencing_token=fencing_token+1, attempts=$2, due_at=$3,
              status='InFlight',lease_expires_at=$4,completed_at=null
            where id=$1 returning fencing_token
            """, id, attempts + 1, due, now + leaseDuration);
        var token = (long)(await claim.ExecuteScalarAsync(ct))!;
        await Execute(c, "update ops.country_database_state set last_attempt_at=$1 where singleton", ct, now);
        await tx.CommitAsync(ct);
        return new(id, token, attempts + 1, due);
    }

    public async Task<bool> RenewAsync(CountryDatabaseUpdateLease lease, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var c = await source.OpenConnectionAsync(ct);
        return await Execute(c, """
            update ops.jobs set lease_expires_at=greatest(lease_expires_at,$4)
            where id=$1 and kind='country-database-update' and fencing_token=$2
              and status='InFlight' and lease_expires_at>$3
            """, ct, lease.Id, lease.Token, now, now + leaseDuration) == 1;
    }

    public async Task<bool> FailAsync(CountryDatabaseUpdateLease lease, string code, bool transient, DateTimeOffset? retryAfter, CancellationToken ct)
    {
        if (code is not ("Network" or "RateLimited" or "ServerError" or "Unauthorized" or "Schema" or "Integrity"
            or "Disk" or "Timeout" or "ApprovalRequired" or "Validation" or "Cancelled"))
        {
            throw new ArgumentException("Use a sanitized failure category.", nameof(code));
        }
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Limits(c, ct);
        if (!await Own(c, lease, ct))
        { return false; }
        var now = time.GetUtcNow();
        await Execute(c, "update ops.country_database_state set first_failure_at=coalesce(first_failure_at,$1) where singleton", ct, now);
        await using var q = Command(c, "select first_failure_at from ops.country_database_state where singleton");
        var first = new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        var retry = transient ? CountryDatabaseSchedule.RetryAt(lease.Attempt, first, now, retryAfter) : null;
        if (retry is { } at)
        {
            await Execute(c, "update ops.country_database_state set last_error_code=$1 where singleton", ct, code);
            if (await Execute(c, """
                update ops.jobs set status='Pending',available_at=$3,lease_expires_at=null,last_error_code=$4
                where id=$1 and fencing_token=$2 and status='InFlight' and lease_expires_at>$5
                """, ct, lease.Id, lease.Token, at, code, time.GetUtcNow()) != 1)
            { return false; }
        }
        else
        {
            if (!await StillOwned(c, lease, ct))
            { return false; }
            await EndCycle(c, lease.Id, now, code, !transient, ct, retryAfter);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<CountryDatabasePromotion> PromoteAsync(CountryDatabaseUpdateLease lease, CountryDatabaseManifest manifest, string? approvedInitialHash, CancellationToken ct)
    {
        if (!manifest.IsAdmissible(time.GetUtcNow()))
        { return CountryDatabasePromotion.InvalidManifest; }
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Limits(c, ct);
        if (!await Own(c, lease, ct))
        { return CountryDatabasePromotion.LostLease; }
        var previous = await ReadActive(c, ct);
        if (previous is null ? approvedInitialHash != manifest.Sha256
            : previous.DatabaseType != manifest.DatabaseType || previous.Schema != manifest.Schema
              || previous.Product != manifest.Product || previous.License != manifest.License || manifest.BuiltAt < previous.BuiltAt)
        {
            return CountryDatabasePromotion.RequiresApproval;
        }
        var canonical = await RegisterVersion(c, manifest, ct);
        if (canonical is null)
        { return CountryDatabasePromotion.InvalidManifest; }
        if (!await StillOwned(c, lease, ct))
        { return CountryDatabasePromotion.LostLease; }
        var changed = await Execute(c, """
            update ops.country_database_state set active_sha256=$1,active_fencing_token=$2,
              first_failure_at=null,last_error_code=null
            where singleton and not blocked and active_fencing_token<$2
            """, ct, canonical.Sha256, lease.Token);
        if (changed != 1)
        { return CountryDatabasePromotion.LostLease; }
        await EndCycle(c, lease.Id, time.GetUtcNow(), null, false, ct);
        await tx.CommitAsync(ct);
        return previous?.Sha256 == manifest.Sha256 ? CountryDatabasePromotion.Unchanged : CountryDatabasePromotion.Promoted;
    }

    /// <summary>Records first-download metadata before file publication; this never changes the active pointer.</summary>
    public async Task<CountryDatabaseManifest?> RecordCandidateAsync(CountryDatabaseUpdateLease lease, CountryDatabaseManifest manifest, CancellationToken ct)
    {
        if (!manifest.IsAdmissible(time.GetUtcNow()))
        { return null; }
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Limits(c, ct);
        if (!await Own(c, lease, ct))
        { return null; }
        var canonical = await RegisterVersion(c, manifest, ct);
        if (canonical is null || !await StillOwned(c, lease, ct))
        { return null; }
        await tx.CommitAsync(ct);
        return canonical;
    }

    private async Task<CountryDatabaseManifest?> RegisterVersion(NpgsqlConnection c, CountryDatabaseManifest manifest, CancellationToken ct)
    {
        await Execute(c, "insert into ops.country_database_versions(sha256,manifest) values($1,$2::jsonb) on conflict do nothing", ct,
            manifest.Sha256, JsonSerializer.Serialize(manifest));
        await using var version = Command(c, "select manifest::text from ops.country_database_versions where sha256=$1", manifest.Sha256);
        var canonical = JsonSerializer.Deserialize<CountryDatabaseManifest>((string)(await version.ExecuteScalarAsync(ct))!)!;
        return canonical with { FirstDownloadedAt = manifest.FirstDownloadedAt, ValidatedAt = manifest.ValidatedAt } == manifest
            && canonical.IsAdmissible(time.GetUtcNow()) ? canonical : null;
    }

    public async Task<CountryDatabaseManifest?> ReadActiveAsync(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await ReadActive(c, ct);
    }

    public async Task<CountryDatabaseSnapshot?> ReadActiveSnapshotAsync(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Command(c, """
            select s.active_fencing_token,v.manifest::text from ops.country_database_state s
            join ops.country_database_versions v on v.sha256=s.active_sha256 where s.singleton
            """);
        await using var reader = await q.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetInt64(0), JsonSerializer.Deserialize<CountryDatabaseManifest>(reader.GetString(1))!) : null;
    }

    private static async Task<CountryDatabaseManifest?> ReadActive(NpgsqlConnection c, CancellationToken ct)
    {
        await using var q = Command(c, """
            select v.manifest::text from ops.country_database_state s
            join ops.country_database_versions v on v.sha256=s.active_sha256 where s.singleton
            """);
        return await q.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<CountryDatabaseManifest>(json) : null;
    }

    private async Task<bool> Own(NpgsqlConnection c, CountryDatabaseUpdateLease lease, CancellationToken ct)
    {
        await using var q = Command(c, """
            select 1 from ops.jobs where id=$1 and kind='country-database-update' and fencing_token=$2
              and status='InFlight' and lease_expires_at>$3 for update
            """, lease.Id, lease.Token, time.GetUtcNow());
        return await q.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<bool> StillOwned(NpgsqlConnection c, CountryDatabaseUpdateLease lease, CancellationToken ct)
    {
        await using var q = Command(c, "select 1 from ops.jobs where id=$1 and fencing_token=$2 and status='InFlight' and lease_expires_at>$3",
            lease.Id, lease.Token, time.GetUtcNow());
        return await q.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task EndCycle(NpgsqlConnection c, Guid id, DateTimeOffset now, string? code, bool blocked, CancellationToken ct,
        DateTimeOffset? retryAfter = null)
    {
        var due = CountryDatabaseSchedule.NextDue(now);
        var available = retryAfter is { } notBefore && notBefore > due ? notBefore : due;
        await Execute(c, """
            update ops.jobs set status='Pending',attempts=0,due_at=$2,available_at=$4,lease_expires_at=null,
              completed_at=null,last_error_code=$3 where id=$1
            """, ct, id, due, (object?)code ?? DBNull.Value, available);
        await Execute(c, "update ops.country_database_state set first_failure_at=null,last_error_code=$1,blocked=$2 where singleton",
            ct, (object?)code ?? DBNull.Value, blocked);
    }

    private static Task<int> Limits(NpgsqlConnection c, CancellationToken ct) =>
        Execute(c, "set local lock_timeout='500ms'; set local statement_timeout='2s'", ct);

    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params object[] values)
    {
        var q = c.CreateCommand();
        q.CommandText = sql;
        q.CommandTimeout = 3;
        foreach (var value in values)
        { q.Parameters.Add(new NpgsqlParameter { Value = value }); }
        return q;
    }

    private static async Task<int> Execute(NpgsqlConnection c, string sql, CancellationToken ct, params object[] values)
    {
        await using var q = Command(c, sql, values);
        return await q.ExecuteNonQueryAsync(ct);
    }
}
