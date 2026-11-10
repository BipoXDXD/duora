using Duora.IntegrationTests.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-E04 (ADR-0053): <c>ck_registrations_withdrawal</c> aceita a retirada voluntária com o mesmo histórico exigido da
/// suspensão, sem suspensão vinculada, e continua aceitando a linha legada sem histórico.
/// </summary>
public sealed class VoluntaryWithdrawalConstraintTests(PostgresFixture postgres)
{
    private const string Insert = """
        insert into events.registrations(event_id,user_id,state,waitlist_position,registered_at,
          withdrawn_at,withdrawn_from_state,withdrawn_waitlist_position)
        values($1,$2,$3,$4,$5,$6,$7,$8)
        """;

    [Theory]
    [InlineData("Confirmed", null)]
    [InlineData("CheckedIn", null)]
    [InlineData("Waitlisted", 3L)]
    public async Task AVoluntaryWithdrawalWithFullHistoryIsAccepted(string from, long? position)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);

        await InsertAsync(s, "Withdrawn", s.Clock.GetUtcNow(), from, position);

        Assert.Equal($"Withdrawn|{from}|{(position is null ? "-" : "3")}|true", await s.TextAsync("""
            select state||'|'||withdrawn_from_state||'|'||coalesce(withdrawn_waitlist_position::text,'-')||'|'
              ||(withdrawn_by_suspension_id is null)
            from events.registrations where event_id=$1 and user_id=$2
            """, s.EventId, s.Seed.Alice));
    }

    [Fact]
    public async Task ALegacyWithdrawalWithoutHistoryIsStillAccepted()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);

        await InsertAsync(s, "Withdrawn", null, null, null);

        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
    }

    [Fact]
    public async Task AVoluntaryWaitlistWithdrawalNeedsItsPosition() =>
        await AssertRejectedAsync("Withdrawn", "Waitlisted", null);

    [Fact]
    public async Task AVoluntaryWithdrawalFromASeatCannotKeepAQueuePosition() =>
        await AssertRejectedAsync("Withdrawn", "Confirmed", 2L);

    [Fact]
    public async Task AVoluntaryWithdrawalNeedsItsOriginState() =>
        await AssertRejectedAsync("Withdrawn", null, null);

    [Fact]
    public async Task AWithdrawalTimestampOnAnActiveRegistrationIsRejected() =>
        await AssertRejectedAsync("Confirmed", "Confirmed", null);

    [Fact]
    public async Task AnUnknownOriginStateIsRejected() =>
        await AssertRejectedAsync("Withdrawn", "Completed", null);

    [Fact]
    public async Task AnInfiniteWithdrawalTimestampIsRejected()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);

        var error = await Assert.ThrowsAsync<PostgresException>(() => s.ExecuteAsync("""
            insert into events.registrations(event_id,user_id,state,registered_at,withdrawn_at,withdrawn_from_state)
            values($1,$2,'Withdrawn',$3,'infinity','Confirmed')
            """, s.EventId, s.Seed.Alice, s.Clock.GetUtcNow()));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_registrations_withdrawal", error.ConstraintName);
    }

    /// <summary>
    /// Uma transação do web anterior lendo a tabela segura um lock que conflita com o <c>ACCESS EXCLUSIVE</c> da troca.
    /// A migração desiste em 5 s (<c>lock_timeout</c>) sem trocar nada, em vez de prender o tráfego atrás dela, e
    /// aplica depois que a transação termina.
    /// </summary>
    [Fact]
    public async Task TheMigrationGivesUpQuicklyBehindAnOpenTransactionAndAppliesLater()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261105120000_RegistrationReconciliationManifest", cancellationToken: ct);
        }
        await using (var reader = new NpgsqlConnection(database.AppConnectionString))
        {
            await reader.OpenAsync(ct);
            await using var open = await reader.BeginTransactionAsync(ct);
            await Execute(reader, "select count(*) from events.registrations", ct);

            var migrating = Task.Run(async () =>
            {
                await using var during = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
                await during.Database.MigrateAsync(ct);
            }, ct);

            // Sem o lock_timeout a migração ficaria presa atrás da leitura; o teto faz o teste falhar em vez de travar.
            // A estratégia de execução do EF trata 55P03 como transitório, tenta de novo e embrulha a última falha.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => migrating.WaitAsync(TimeSpan.FromSeconds(60), ct));

            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
        await using var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
        Assert.Contains("20261109120000_VoluntaryRegistrationWithdrawal", await after.Database.GetPendingMigrationsAsync(ct));

        await after.Database.MigrateAsync(ct);

        Assert.Empty(await after.Database.GetPendingMigrationsAsync(ct));
    }

    private async Task AssertRejectedAsync(string state, string? from, long? position)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(s, state, s.Clock.GetUtcNow(), from, position));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_registrations_withdrawal", error.ConstraintName);
        Assert.Equal(0, await s.CountAsync("select count(*) from events.registrations where event_id=$1", s.EventId));
    }

    private static Task InsertAsync(SuspendedRegistrationScenario s, string state, DateTimeOffset? withdrawnAt,
        string? from, long? position) =>
        s.ExecuteAsync(Insert, s.EventId, s.Seed.Alice, state,
            state == "Waitlisted" ? 1L : DBNull.Value, s.Clock.GetUtcNow(),
            withdrawnAt is { } at ? at : DBNull.Value,
            (object?)from ?? DBNull.Value, position is { } p ? p : DBNull.Value);
}
