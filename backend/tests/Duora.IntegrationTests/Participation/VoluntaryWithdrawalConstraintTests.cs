using Duora.IntegrationTests.Safety;
using Npgsql;

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
