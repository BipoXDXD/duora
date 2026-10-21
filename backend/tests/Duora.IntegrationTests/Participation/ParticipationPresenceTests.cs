using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class ParticipationPresenceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true, "Active", "Passed", true)]
    [InlineData(false, "Active", "Passed", false)]
    [InlineData(true, "Suspended", "Passed", false)]
    [InlineData(true, "Active", "Inconclusive", false)]
    public async Task MatchingUsesEmailAndPresenceWithoutAgeEvidence(bool confirmed, string state, string selfie, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, DateTimeOffset.UtcNow, ct, Guid.NewGuid().ToString("N"), true);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(ct);
        await Execute(connection, "update identity.users set status=$2,email_confirmed=$3 where id=$1", ct, seed.Alice, state, confirmed);
        await Execute(connection, "update identity.selfie_verifications set state=$2 where user_id=$1", ct, seed.Alice, selfie);
        Assert.Equal(expected, await Eligible(connection, seed.Alice, ct));
        await using var historical = Command(connection, "select count(*) from identity.age_verifications where user_id=$1", seed.Alice);
        Assert.Equal(0L, await historical.ExecuteScalarAsync(ct));
    }
}
