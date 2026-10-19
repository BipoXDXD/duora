using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Profiles;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class LanguagePreferenceGuardTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionIsRecheckedAfterWaitingForUserGuard(bool locale)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = new IdentityScenario(postgres, _factory);
        var user = await scenario.CreateConfirmedAccountAsync();
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var holder = await source.OpenConnectionAsync(ct);
        await using (var create = new NpgsqlCommand("insert into safety.user_guards (user_id) values (@value) on conflict do nothing", holder))
        {
            create.Parameters.AddWithValue("value", user.UserId);
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var transaction = await holder.BeginTransactionAsync(ct);
        await using (var guard = new NpgsqlCommand("select 1 from safety.user_guards where user_id = @value for update", holder))
        {
            guard.Parameters.AddWithValue("value", user.UserId);
            await guard.ExecuteNonQueryAsync(ct);
        }
        var store = new PreferenceStore(source, TimeProvider.System);
        var localeStore = new LocaleStore(source);
        Task pending = locale
            ? localeStore.UpdateOwnAsync(user.UserId, 0, "pt-BR", ct)
            : store.UpdateOwnAsync(user.UserId, 0, ["pt"], false, ct);
        await using var observer = await source.OpenConnectionAsync(ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1.5);
        while (true)
        {
            await using var wait = new NpgsqlCommand("select count(*) from pg_stat_activity where @value = any(pg_blocking_pids(pid))", observer);
            wait.Parameters.AddWithValue("value", holder.ProcessID);
            if ((long)(await wait.ExecuteScalarAsync(ct))! > 0)
            {
                break;
            }
            Assert.True(DateTimeOffset.UtcNow < deadline, "Writer did not wait for shared guard.");
            await Task.Yield();
        }
        await using (var change = new NpgsqlCommand(locale
            ? "update identity.users set preferred_locale = 'en-US', locale_version = 1 where id = @value"
            : "insert into profiles.preferences (user_id,allow_different_display_locales,version,updated_at) values (@value,false,1,now()); insert into profiles.communication_languages values (@value,'en')", holder))
        {
            change.Parameters.AddWithValue("value", user.UserId);
            await change.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        await pending;
        if (locale)
        {
            Assert.Null(await (Task<OwnLocale?>)pending);
            Assert.Equal(new OwnLocale("en-US", 1), await localeStore.GetOwnAsync(user.UserId, ct));
        }
        else
        {
            Assert.Null(await (Task<OwnPreferences?>)pending);
            Assert.Equal(["en"], (await store.GetOwnAsync(user.UserId, ct)).CommunicationLanguages);
        }
    }
}
