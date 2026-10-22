using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed class MediaReviewMigrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExpansionPreservesEveryLegacyStateAndCreatesNoCasesOrAssets()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261021210000_CountryDatabaseOperations", cancellationToken: Ct);
        }
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        var owner = await RoleSeparationTests.InsertUserAsync(connection);
        foreach (var state in new[] { "Pending", "Quarantined", "Processing", "Approved", "Rejected", "Deleted" })
        {
            await using var insert = new NpgsqlCommand("""
                insert into media.uploads (id, owner_id, state, client_key, quarantine_key, declared_content_type,
                    size_bytes, content_sha256, created_at, abandon_after, rejection_code, version)
                values ($1, $2, $3, $3, 'quarantine/' || replace($1::text, '-', ''), 'image/jpeg', 2048,
                    case when $3 = 'Pending' then null else decode(repeat('ab', 32), 'hex') end,
                    '2026-10-01Z', '2026-10-02Z', case when $3 = 'Rejected' then 'ANIMATED' else null end, 7)
                """, connection);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(owner);
            insert.Parameters.AddWithValue(state);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var original = await RowsAsync(connection);
        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(Ct);
            Assert.False(after.Database.HasPendingModelChanges());
        }
        Assert.Equal(original, await RowsAsync(connection));
        await using var check = new NpgsqlCommand("""
            select (select count(*) from safety.photo_review_cases),
                   (select count(*) from media.media_assets),
                   (select count(*) from media.uploads where state = 'AwaitingReview'),
                   (select count(*) from identity.consent_records)
            """, connection);
        await using var reader = await check.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0L, reader.GetInt64(i));
        }
    }

    [Fact]
    public async Task ExpandedPartialIndexCoversReviewWaitsAndStillRequiresAnOriginalFingerprint()
    {
        var database = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var factory = new DuoraWebFactory(database);
        var media = new MediaScenario(database, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        using var accepted = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        var photo = Assert.Single(await media.UploadsOfAsync(account.UserId));
        await media.ExecuteAsync("update media.uploads set state = 'AwaitingReview' where id = $1", photo.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => media.ExecuteAsync(
            "update media.uploads set content_sha256 = null where id = $1", photo.Id));
        Assert.Equal("ck_uploads_content_sha256", error.ConstraintName);
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(Ct);
        await using var explain = new NpgsqlCommand("""
            set enable_seqscan = off;
            explain select owner_id from media.uploads
            where state in ('Pending', 'Quarantined', 'Processing', 'AwaitingReview')
            """, connection);
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }
        Assert.Contains("ix_uploads_pending_owner", string.Join('\n', lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseIdentityIsUniqueAndDeadlineAndPolicyCannotBeInvalid()
    {
        var database = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var factory = new DuoraWebFactory(database);
        var media = new MediaScenario(database, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        using var accepted = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        var photo = Assert.Single(await media.UploadsOfAsync(account.UserId));
        await media.ExecuteAsync("update media.uploads set state = 'AwaitingReview' where id = $1", photo.Id);
        const string Insert = """
            insert into safety.photo_review_cases
                (id, upload_id, candidate_generation, policy_version, awaiting_review_at, review_deadline, version)
            values ($1, $2, $3, $4, '2026-10-22Z', $5, $6)
            """;
        var generation = Guid.NewGuid();
        var deadline = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        foreach (var (policy, until, version) in new[]
        {
            ("", deadline, 1L),
            ("human-review-v1", deadline.AddDays(-3), 1L),
            ("human-review-v1", deadline.AddDays(1), 1L),
            ("human-review-v1", deadline, 0L),
        })
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => media.ExecuteAsync(
                Insert, Guid.NewGuid(), photo.Id, generation, policy, until, version));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        }
        await media.ExecuteAsync(Insert, Guid.NewGuid(), photo.Id, generation, "human-review-v1", deadline, 1L);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => media.ExecuteAsync(
            Insert, Guid.NewGuid(), photo.Id, generation, "human-review-v1", deadline, 1L));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => media.ExecuteAsync(
            Insert, Guid.NewGuid(), Guid.NewGuid(), generation, "human-review-v1", deadline, 1L));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
    }

    private static async Task<List<string>> RowsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("select row_to_json(u)::text from media.uploads u order by id", connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }
}
