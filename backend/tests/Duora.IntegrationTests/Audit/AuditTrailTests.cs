using System.Diagnostics;
using Duora.Infrastructure.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Audit;

/// <summary>
/// Trilha de auditoria (TM-05, D3): o registro entra na transação do efeito e some com o rollback dela.
/// </summary>
public sealed class AuditTrailTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecordAppendedInCommittedTransactionIsPersistedWithItsFields()
    {
        var actorId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        using var activity = new Activity("audit-test").Start();
        await using var db = postgres.CreateDbContext();
        var trail = new AuditTrail(db, new FakeTimeProvider(Now));

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await trail.AppendAsync(
                new AuditRecord(AuditActorKind.User, actorId, AuditAction.SafetyBlockCreated, "user", targetId, 7),
                Ct);
            await transaction.CommitAsync(Ct);
        }

        var row = Assert.Single(await ReadByTargetAsync(targetId));
        Assert.Equal(Now, row.OccurredAt);
        Assert.Equal("User", row.ActorKind);
        Assert.Equal(actorId, row.ActorId);
        Assert.Equal("safety.block_created", row.Action);
        Assert.Equal("user", row.TargetType);
        Assert.Equal(7L, row.TargetVersion);
        Assert.Equal(activity.TraceId.ToHexString(), row.CorrelationId);
    }

    [Fact]
    public async Task RecordAppendedInRolledBackTransactionDisappearsWithTheEffect()
    {
        var targetId = Guid.CreateVersion7();
        await using var db = postgres.CreateDbContext();
        var trail = new AuditTrail(db, new FakeTimeProvider(Now));

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await trail.AppendAsync(SystemRecord(targetId), Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Empty(await ReadByTargetAsync(targetId));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ConditionalAppendWritesOnlyWhenTheEffectHappened(bool effectHappened, int expectedRows)
    {
        var targetId = Guid.CreateVersion7();
        await using var db = postgres.CreateDbContext();
        var trail = new AuditTrail(db, new FakeTimeProvider(Now));

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await trail.AppendIfAsync(SystemRecord(targetId), effectHappened, Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(expectedRows, (await ReadByTargetAsync(targetId)).Count);
    }

    [Fact]
    public async Task AppendOutsideATransactionIsRejected()
    {
        var targetId = Guid.CreateVersion7();
        await using var db = postgres.CreateDbContext();
        var trail = new AuditTrail(db, new FakeTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => trail.AppendAsync(SystemRecord(targetId), Ct));

        Assert.Empty(await ReadByTargetAsync(targetId));
    }

    [Fact]
    public async Task RecordWithoutActivityHasNoCorrelationId()
    {
        var targetId = Guid.CreateVersion7();
        Activity.Current = null;
        await using var db = postgres.CreateDbContext();
        var trail = new AuditTrail(db, new FakeTimeProvider(Now));

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await trail.AppendAsync(SystemRecord(targetId), Ct);
            await transaction.CommitAsync(Ct);
        }

        var row = Assert.Single(await ReadByTargetAsync(targetId));
        Assert.Null(row.ActorId);
        Assert.Equal("System", row.ActorKind);
        Assert.Null(row.CorrelationId);
        Assert.Null(row.TargetVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("User")]
    [InlineData("user profile")]
    [InlineData("user;drop")]
    [InlineData("a_target_type_longer_than_thirty_two")]
    public void TargetTypeOutsideTheCatalogFormatIsRejected(string targetType) =>
        Assert.Throws<ArgumentException>(() =>
            new AuditRecord(AuditActorKind.User, Guid.CreateVersion7(), AuditAction.SafetyBlockCreated, targetType, Guid.CreateVersion7()));

    [Fact]
    public void UserActorWithoutIdIsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            new AuditRecord(AuditActorKind.User, null, AuditAction.SafetyBlockCreated, "user", Guid.CreateVersion7()));

    [Fact]
    public async Task DatabaseRejectsUnknownActorKind()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            insert into ops.audit_events (occurred_at, actor_kind, action, target_type, target_id)
            values (now(), 'Root', 'safety.block_created', 'user', gen_random_uuid())
            """,
            connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public void TerminalOperatorOnlyActsThroughItsLabel() =>
        Assert.Throws<ArgumentException>(() =>
            new AuditRecord(AuditActorKind.TerminalOperator, null, AuditAction.SafetyBlockCreated, "user", Guid.CreateVersion7()));

    [Fact]
    public async Task TerminalOperatorRecordIsPersistedWithItsLabelAndNoActorId()
    {
        var targetId = Guid.CreateVersion7();
        Assert.True(OperatorName.TryParse("ana.ops", out var operatorName));
        await using var db = postgres.CreateDbContext();
        var trail = new AuditTrail(db, new FakeTimeProvider(Now));

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await trail.AppendAsync(
                AuditRecord.ByTerminalOperator(operatorName, AuditAction.AdminTemporaryPasswordReissued, "user", targetId),
                Ct);
            await transaction.CommitAsync(Ct);
        }

        var row = Assert.Single(await ReadByTargetAsync(targetId));
        Assert.Equal("TerminalOperator", row.ActorKind);
        Assert.Null(row.ActorId);
        Assert.Equal("ana.ops", row.ActorLabel);
        Assert.Equal("identity.admin_temporary_password_reissued", row.Action);
    }

    /// <summary>
    /// O banco é a última barreira do rótulo do terminal: só <c>TerminalOperator</c> o tem, sempre, sem id de ator e
    /// no formato de usuário de sistema. Os casos válidos mostram que a recusa vem da regra, não do comando.
    /// </summary>
    [Theory]
    [InlineData("TerminalOperator", false, "ana.ops", true)]
    [InlineData("TerminalOperator", false, null, false)]
    [InlineData("TerminalOperator", true, "ana.ops", false)]
    [InlineData("TerminalOperator", false, "Ana Silva", false)]
    [InlineData("TerminalOperator", false, "ana.ops\n", false)]
    [InlineData("TerminalOperator", false, "-ana", false)]
    [InlineData("TerminalOperator", false, ".ana", false)]
    [InlineData("User", true, "ana.ops", false)]
    [InlineData("System", false, "ana.ops", false)]
    [InlineData("Operator", true, "ana.ops", false)]
    [InlineData("Operator", true, null, true)]
    [InlineData("System", false, null, true)]
    public async Task DatabaseAcceptsAnActorLabelOnlyForTheTerminalOperator(
        string actorKind,
        bool withActorId,
        string? actorLabel,
        bool accepted)
    {
        var targetId = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            insert into ops.audit_events (occurred_at, actor_kind, actor_id, actor_label, action, target_type, target_id)
            values (now(), $1, $2, $3, 'identity.admin_granted.moderator', 'user', $4)
            """,
            connection);
        command.Parameters.AddWithValue(actorKind);
        command.Parameters.AddWithValue(withActorId ? Guid.CreateVersion7() : DBNull.Value);
        command.Parameters.AddWithValue((object?)actorLabel ?? DBNull.Value);
        command.Parameters.AddWithValue(targetId);

        if (accepted)
        {
            Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
            return;
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Empty(await ReadByTargetAsync(targetId));
    }

    private static AuditRecord SystemRecord(Guid targetId) =>
        new(AuditActorKind.System, null, AuditAction.SafetyBlockCreated, "user", targetId);

    private async Task<List<AuditRow>> ReadByTargetAsync(Guid targetId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Database
            .SqlQuery<AuditRow>(
                $"""
                select occurred_at as "OccurredAt", actor_kind as "ActorKind", actor_id as "ActorId",
                       actor_label as "ActorLabel", action as "Action", target_type as "TargetType",
                       target_version as "TargetVersion",
                       correlation_id as "CorrelationId"
                from ops.audit_events where target_id = {targetId}
                """)
            .ToListAsync(Ct);
    }

    private sealed record AuditRow(
        DateTimeOffset OccurredAt,
        string ActorKind,
        Guid? ActorId,
        string? ActorLabel,
        string Action,
        string TargetType,
        long? TargetVersion,
        string? CorrelationId);
}
