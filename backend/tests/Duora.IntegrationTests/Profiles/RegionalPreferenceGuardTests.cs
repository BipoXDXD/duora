using Duora.Core.Profiles;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Profiles;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class RegionalPreferenceGuardTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountAndVersionAreRecheckedAfterAcquiringTheSharedGuard(bool restrictAccount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var holder = await source.OpenConnectionAsync(ct);
        var user = await RoleSeparationTests.InsertUserAsync(holder);
        await using (var create = new NpgsqlCommand("insert into safety.user_guards(user_id) values(@user)", holder))
        {
            create.Parameters.AddWithValue("user", user);
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var tx = await holder.BeginTransactionAsync(ct);
        await using (var guard = new NpgsqlCommand("select 1 from safety.user_guards where user_id=@user for update", holder))
        {
            guard.Parameters.AddWithValue("user", user);
            await guard.ExecuteNonQueryAsync(ct);
        }
        Assert.True(RegionalPreference.TryCreate("countries", ["BR"], out var preference));
        var pending = new RegionalPreferenceStore(source, TimeProvider.System).UpdateOwnAsync(user, 0, preference!, ct);
        await using var observer = await source.OpenConnectionAsync(ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1.5);
        while (true)
        {
            await using var waiting = new NpgsqlCommand("select count(*) from pg_stat_activity where @pid=any(pg_blocking_pids(pid))", observer);
            waiting.Parameters.AddWithValue("pid", holder.ProcessID);
            if ((long)(await waiting.ExecuteScalarAsync(ct))! > 0)
            { break; }
            Assert.True(DateTimeOffset.UtcNow < deadline, "Regional writer did not wait for the shared guard.");
            await Task.Yield();
        }
        await using (var change = new NpgsqlCommand(restrictAccount
            ? "update identity.users set status='Restricted' where id=@user"
            : "insert into profiles.regional_preferences values(@user,'unrestricted',1,now())", holder))
        {
            change.Parameters.AddWithValue("user", user);
            await change.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (restrictAccount)
        {
            await Assert.ThrowsAsync<LanguagePreferenceRestrictedException>(() => pending);
        }
        else
        {
            Assert.Null(await pending);
        }
        await using var count = new NpgsqlCommand("select count(*) from profiles.regional_preference_countries where user_id=@user", observer);
        count.Parameters.AddWithValue("user", user);
        Assert.Equal(0L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task ReadersNeverMixCountriesWithAnotherModeOrVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(ct);
        var user = await RoleSeparationTests.InsertUserAsync(connection);
        var store = new RegionalPreferenceStore(source, TimeProvider.System);
        Assert.True(RegionalPreference.TryCreate("countries", ["BR"], out var countries));
        Assert.True(RegionalPreference.TryCreate("unrestricted", [], out var unrestricted));
        var writer = Task.Run(async () =>
        {
            for (var version = 0; version < 30; version++)
            {
                Assert.NotNull(await store.UpdateOwnAsync(user, version, version % 2 == 0 ? countries! : unrestricted!, ct));
            }
        }, ct);
        do
        {
            var read = await store.GetOwnAsync(user, ct);
            if (read.Version == 0)
            { Assert.Equal(RegionalPreferenceMode.Pending, read.Preference.Mode); }
            else if (read.Version % 2 == 1)
            {
                Assert.Equal(RegionalPreferenceMode.Countries, read.Preference.Mode);
                Assert.Equal(["BR"], read.Preference.CountryCodes);
            }
            else
            {
                Assert.Equal(RegionalPreferenceMode.Unrestricted, read.Preference.Mode);
                Assert.Empty(read.Preference.CountryCodes);
            }
        } while (!writer.IsCompleted);
        await writer;
    }
}
