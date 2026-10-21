using System.Net;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class RegionalPreferenceTimeoutTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GuardTimeoutReturnsRetryableUnavailabilityWithoutPartialConsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = new IdentityScenario(postgres, _factory);
        var user = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(user);
        await using var holder = new NpgsqlConnection(postgres.ConnectionString);
        await holder.OpenAsync(ct);
        await using (var create = new NpgsqlCommand("insert into safety.user_guards(user_id) values(@user) on conflict do nothing", holder))
        {
            create.Parameters.AddWithValue("user", user.UserId);
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var tx = await holder.BeginTransactionAsync(ct);
        await using (var guard = new NpgsqlCommand("select 1 from safety.user_guards where user_id=@user for update", holder))
        {
            guard.Parameters.AddWithValue("user", user.UserId);
            await guard.ExecuteNonQueryAsync(ct);
        }
        using var response = await browser.PutAsync("/api/v1/me/regional-preferences",
            """{"mode":"countries","countryCodes":["BR"]}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        await tx.CommitAsync(ct);
        using var read = await browser.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal("\"0\"", read.Headers.ETag!.ToString());
        await using var count = new NpgsqlCommand("select count(*) from profiles.regional_preference_countries where user_id=@user", holder);
        count.Parameters.AddWithValue("user", user.UserId);
        Assert.Equal(0L, await count.ExecuteScalarAsync(ct));
    }
}
