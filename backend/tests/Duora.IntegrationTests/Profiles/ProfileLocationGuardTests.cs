using System.Net;
using Duora.Core.Profiles;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Profiles;
using Duora.IntegrationTests.Identity;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Profiles;

public sealed class ProfileLocationGuardTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BothLocationContractsRecheckAccountAndVersionAfterTheGuard(bool legacy, bool restricted)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var holder = await source.OpenConnectionAsync(ct);
        var user = await RoleSeparationTests.InsertUserAsync(holder);
        await Execute(holder, "insert into safety.user_guards(user_id) values($1)", ct, user);
        await using var tx = await holder.BeginTransactionAsync(ct);
        await Execute(holder, "select 1 from safety.user_guards where user_id=$1 for update", ct, user);
        var store = new ProfileStore(source, TimeProvider.System);
        Assert.True(RegionCode.TryParse("BR-SP", out var region));
        var edit = legacy ? new ProfileEdit(null, null, new(region)) : new(null, null, null, new("BR", "BR-SP"));
        var update = store.UpdateOwnAsync(user, 0, edit, ct);
        try
        {
            await WaitForBlockedWriter(source, holder.ProcessID, update, ct);
            await Execute(holder, restricted
                ? "update identity.users set status='Restricted' where id=$1"
                : "insert into profiles.profiles(user_id,version,updated_at) values($1,1,now())", ct, user);
        }
        finally { await tx.CommitAsync(ct); }
        if (restricted)
        { await Assert.ThrowsAsync<LanguagePreferenceRestrictedException>(() => update); }
        else
        { Assert.Null(await update); }
        var profile = await store.GetOwnAsync(user, ct);
        Assert.Null(profile.CountryCode);
        Assert.Null(profile.SubdivisionCode);
        Assert.Null(profile.RegionCode);
        await using var marker = Command(holder, "select security_version from safety.user_guards where user_id=$1", user);
        Assert.Equal(0L, await marker.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task LocationWritesIncrementTheGuardMarkerButOmissionDoesNotChangeLocation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(ct);
        var user = await RoleSeparationTests.InsertUserAsync(connection);
        var store = new ProfileStore(source, TimeProvider.System);
        var first = Assert.IsType<OwnProfile>(await store.UpdateOwnAsync(user, 0, new(null, null, null, new("BR", "BR-SP")), ct));
        await using (var marker = Command(connection, "select security_version from safety.user_guards where user_id=$1", user))
        { Assert.Equal(1L, await marker.ExecuteScalarAsync(ct)); }
        Assert.True(ProfileBio.TryParse("Synthetic bio", out var bio, out _));
        var second = Assert.IsType<OwnProfile>(await store.UpdateOwnAsync(user, first.Version, new(null, new(bio), null), ct));
        Assert.Equal("BR", second.CountryCode);
        Assert.Equal("BR-SP", second.SubdivisionCode);
        Assert.Equal("BR-SP", second.RegionCode);
        var cleared = Assert.IsType<OwnProfile>(await store.UpdateOwnAsync(user, second.Version, new(null, null, new(null)), ct));
        Assert.Null(cleared.CountryCode);
        Assert.Null(cleared.SubdivisionCode);
        Assert.Null(cleared.RegionCode);
        await using var finalMarker = Command(connection, "select security_version from safety.user_guards where user_id=$1", user);
        Assert.Equal(2L, await finalMarker.ExecuteScalarAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpWriterReturnsRestrictionOrRetryableTimeoutWithoutPartialChanges(bool restrict)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var user = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(user);
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var holder = await source.OpenConnectionAsync(ct);
        await Execute(holder, "insert into safety.user_guards(user_id) values($1) on conflict do nothing", ct, user.UserId);
        await using var tx = await holder.BeginTransactionAsync(ct);
        await Execute(holder, "select 1 from safety.user_guards where user_id=$1 for update", ct, user.UserId);
        var pending = browser.PatchRawJsonAsync("/api/v1/me/profile", """{"countryCode":"BR"}""",
            "\"0\"");
        if (restrict)
        {
            try
            {
                await WaitForBlockedWriter(source, holder.ProcessID, pending, ct);
                await Execute(holder, "update identity.users set status='Restricted' where id=$1", ct, user.UserId);
            }
            finally { await tx.CommitAsync(ct); }
        }
        using var response = await pending;
        Assert.Equal(restrict ? HttpStatusCode.Forbidden : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        if (!restrict)
        { Assert.NotNull(response.Headers.RetryAfter); await tx.CommitAsync(ct); }
        var read = await new ProfileStore(source, TimeProvider.System).GetOwnAsync(user.UserId, ct);
        Assert.Equal(0, read.Version);
        Assert.Null(read.CountryCode);
    }

    private static async Task WaitForBlockedWriter(NpgsqlDataSource source, int blocker, Task writer, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using var observer = await source.OpenConnectionAsync(linked.Token);
        while (true)
        {
            await using var query = Command(observer, "select exists(select 1 from pg_stat_activity where $1=any(pg_blocking_pids(pid)))", blocker);
            if ((bool)(await query.ExecuteScalarAsync(linked.Token))!)
            { return; }
            Assert.False(writer.IsCompleted, "Location writer bypassed the matching guard.");
            await Task.Delay(10, linked.Token);
        }
    }
}
