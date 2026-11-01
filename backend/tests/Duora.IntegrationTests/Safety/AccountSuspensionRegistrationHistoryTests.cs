using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Participation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Safety;

public sealed class AccountSuspensionRegistrationHistoryTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheApplicationCannotRewriteOrDeleteASuspensionWithdrawal(bool delete)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));

        var error = await Assert.ThrowsAsync<PostgresException>(() => s.ExecuteAsync(delete
            ? "delete from events.registrations where event_id=$1 and user_id=$2"
            : "update events.registrations set state='Confirmed' where event_id=$1 and user_id=$2",
            s.EventId, s.Seed.Alice));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
    }

    [Fact]
    public async Task AWaitlistWithdrawalCannotHaveAnUnknownHistoricalPosition()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        var suspension = Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));
        // A second legacy-style row for the same subject in another event, deliberately missing its queue history.
        var error = await Assert.ThrowsAsync<PostgresException>(() => s.ExecuteAsync("""
            insert into events.registrations(event_id,user_id,state,registered_at,withdrawn_at,
              withdrawn_by_suspension_id,withdrawn_from_state)
            values($1,$2,'Withdrawn',$3,$3,$4,'Waitlisted')
            """, s.Seed.Events[1], s.Seed.Alice, s.Clock.GetUtcNow(), suspension.View.Id));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task UpgradingPreservesExistingRegistrationsWithoutInventingWithdrawalHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261030120000_SelfieFailures", cancellationToken: ct);
        }
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(database.AppConnectionString, now, ct, Guid.NewGuid().ToString("N"));
        await using (var connection = new NpgsqlConnection(database.AppConnectionString))
        {
            await connection.OpenAsync(ct);
            await Execute(connection, """
                insert into events.registrations(event_id,user_id,state,registered_at,checked_in_at)
                  values($1,$2,'CheckedIn',$3,$3),($1,$4,'Withdrawn',$3,null)
                """, ct, seed.Events[0], seed.Alice, now, seed.Bob);
            await Execute(connection, "update events.events set confirmed_count=1 where id=$1", ct, seed.Events[0]);
        }
        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(ct);
            await after.Database.MigrateAsync(ct);
        }
        await using var verify = new NpgsqlConnection(database.AppConnectionString);
        await verify.OpenAsync(ct);
        await using var query = Command(verify, """
            select state,checked_in_at is not null,withdrawn_at is null,withdrawn_by_suspension_id is null,
              confirmed_at is null,version from events.registrations where event_id=$1 order by state
            """, seed.Events[0]);
        await using var reader = await query.ExecuteReaderAsync(ct);
        foreach (var state in new[] { "CheckedIn", "Withdrawn" })
        {
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(state, reader.GetString(0));
            Assert.Equal(state == "CheckedIn", reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
            Assert.Equal(1, reader.GetInt64(5));
        }
        Assert.False(await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task FailureAfterPromotionRollsBackTheWholeSuspension()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using (var migrator = new NpgsqlConnection(s.Database.MigratorConnectionString))
        {
            await migrator.OpenAsync(s.Token);
            await Execute(migrator, """
                create function ops.reject_synthetic_suspension() returns trigger language plpgsql as $$
                begin
                  if new.action='safety.account_suspended' then
                    raise exception 'Synthetic failure after registration changes' using errcode='23514';
                  end if;
                  return new;
                end $$;
                create trigger reject_synthetic_suspension before insert on ops.audit_events
                  for each row execute function ops.reject_synthetic_suspension();
                """, s.Token);
        }

        var failure = await Assert.ThrowsAsync<PostgresException>(() => s.SuspendAsync(s.Seed.Alice));

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(1);
        Assert.Equal("Active", await s.TextAsync("select status from identity.users where id=$1", s.Seed.Alice));
        Assert.Equal(0, await s.CountAsync("select count(*) from safety.moderation_actions"));
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.audit_events where action like 'events.%'"));
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
    }
}
