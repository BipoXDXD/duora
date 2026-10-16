using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// Migration de dados AgeVerificationMinorSuspicionBackfill (ADR-0024): os <c>NotAdult</c> recebidos antes da coluna
/// <c>minor_suspected_at</c> viram suspeita gravada. Falha fechada: toda <c>Rejected</c> (só <c>NotAdult</c> produz
/// esse estado) e toda solicitação citada na auditoria de <c>NotAdult</c> tardio.
/// </summary>
public sealed class MinorSuspicionBackfillTests(PostgresFixture postgres)
{
    private const string MigrationBeforeBackfill = "20261016085440_AgeVerificationMinorSuspicion";

    private static readonly DateTimeOffset RequestedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DecidedAt = RequestedAt.AddMinutes(5);
    private static readonly DateTimeOffset FirstLateNotAdult = RequestedAt.AddDays(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotAdultReceivedBeforeTheColumnBecomesARecordedSuspicion()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync(MigrationBeforeBackfill, cancellationToken: Ct);
        }

        Guid rejected, rejectedUndated, lateOnAdult, lateOnExpired, adult, review;
        await using (var connection = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            var userId = await RoleSeparationTests.InsertUserAsync(connection);
            var otherUserId = await RoleSeparationTests.InsertUserAsync(connection);
            rejected = await InsertAsync(connection, userId, "Rejected", DecidedAt);
            rejectedUndated = await InsertAsync(connection, otherUserId, "Rejected", decidedAt: null);
            lateOnAdult = await InsertAsync(connection, userId, "VerifiedAdult", DecidedAt);
            lateOnExpired = await InsertAsync(connection, userId, "Expired", decidedAt: null);
            adult = await InsertAsync(connection, otherUserId, "VerifiedAdult", DecidedAt);
            review = await InsertAsync(connection, otherUserId, "ManualReview", DecidedAt);
            await AuditLateNotAdultAsync(connection, lateOnAdult, FirstLateNotAdult.AddHours(1));
            await AuditLateNotAdultAsync(connection, lateOnAdult, FirstLateNotAdult);
            await AuditLateNotAdultAsync(connection, lateOnExpired, FirstLateNotAdult);
        }

        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(Ct);
        }

        await using var check = new NpgsqlConnection(database.MigratorConnectionString);
        await check.OpenAsync(Ct);
        Assert.Equal(DecidedAt, await SuspectedAtAsync(check, rejected));
        Assert.Equal(RequestedAt, await SuspectedAtAsync(check, rejectedUndated));
        Assert.Equal(FirstLateNotAdult, await SuspectedAtAsync(check, lateOnAdult));
        Assert.Equal(FirstLateNotAdult, await SuspectedAtAsync(check, lateOnExpired));
        Assert.Null(await SuspectedAtAsync(check, adult));
        Assert.Null(await SuspectedAtAsync(check, review));
    }

    private static async Task<Guid> InsertAsync(NpgsqlConnection connection, Guid userId, string state, DateTimeOffset? decidedAt)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            insert into identity.age_verifications
                (id, user_id, state, policy_version, provider, provider_reference, request_nonce, requested_at,
                 decided_at, decision_source, version)
            values ($1, $2, $3, 'test-2026-09', 'fake', 'ref-' || $1::text, 'nonce-' || $1::text, $4, $5,
                    case when $5::timestamptz is null then null else 'Callback' end, 1)
            """,
            connection);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(state);
        command.Parameters.AddWithValue(RequestedAt);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)decidedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        return id;
    }

    private static async Task AuditLateNotAdultAsync(NpgsqlConnection connection, Guid verificationId, DateTimeOffset at)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into ops.audit_events (occurred_at, actor_kind, action, target_type, target_id, target_version)
            values ($1, 'System', 'identity.age_verification_late_not_adult', 'age_verification', $2, 1)
            """,
            connection);
        command.Parameters.AddWithValue(at);
        command.Parameters.AddWithValue(verificationId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    private static async Task<DateTimeOffset?> SuspectedAtAsync(NpgsqlConnection connection, Guid verificationId)
    {
        await using var command = new NpgsqlCommand(
            "select minor_suspected_at from identity.age_verifications where id = $1",
            connection);
        command.Parameters.AddWithValue(verificationId);
        return await command.ExecuteScalarAsync(Ct) is DateTime instant ? new DateTimeOffset(instant, TimeSpan.Zero) : null;
    }
}
