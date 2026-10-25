using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Profiles;
using Duora.Infrastructure.Profiles;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

/// <summary>Compatibilidade do binário, não uma migration nem autorização de contract em produção.</summary>
public sealed class ProfileStoreCompatibilityTests(PostgresFixture postgres)
{
    private const string ProfilePath = "/api/v1/me/profile";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothSchemasKeepTheLegacyHttpShapeAndCanonicalLocation(bool contracted)
    {
        var database = await CreateDatabaseAsync();
        if (contracted)
        {
            await SimulateContractAsync(database);
        }
        await using var factory = new DuoraWebFactory(database.AppConnectionString);
        var scenario = new IdentityScenario(database.AppConnectionString, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);

        var markerBefore = await MarkerAsync(database, account.UserId);
        using var empty = await browser.GetAsync(ProfilePath);
        await AssertProfileAsync(empty, null, null, null, 0);
        using var legacy = await browser.PatchRawJsonAsync(ProfilePath, """{"regionCode":"BR-SP"}""", "\"0\"");
        await AssertProfileAsync(legacy, "BR", "BR-SP", "BR-SP", 1);
        using var unitedStates = await browser.PatchRawJsonAsync(ProfilePath, """{"countryCode":"US","subdivisionCode":"US-MA"}""", "\"1\"");
        await AssertProfileAsync(unitedStates, "US", "US-MA", null, 2);
        using var countryOnly = await browser.PatchRawJsonAsync(ProfilePath, """{"countryCode":"BR"}""", "\"2\"");
        await AssertProfileAsync(countryOnly, "BR", null, null, 3);
        using var clear = await browser.PatchRawJsonAsync(ProfilePath, """{"regionCode":null}""", "\"3\"");
        await AssertProfileAsync(clear, null, null, null, 4);
        using var text = await browser.PatchRawJsonAsync(ProfilePath, """{"displayName":"Perfil de teste"}""", "\"4\"");
        await AssertProfileAsync(text, null, null, null, 5);

        using var mixed = await browser.PatchRawJsonAsync(ProfilePath, """{"countryCode":"BR","regionCode":"BR-SP"}""", "\"5\"");
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        using var invalidLegacy = await browser.PatchRawJsonAsync(ProfilePath, """{"regionCode":"US-MA"}""", "\"5\"");
        Assert.Equal(HttpStatusCode.BadRequest, invalidLegacy.StatusCode);
        using var stale = await browser.PatchRawJsonAsync(ProfilePath, """{"countryCode":"US"}""", "\"4\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var missingVersion = await browser.PatchRawJsonAsync(ProfilePath, """{"countryCode":"US"}""", null);
        Assert.Equal((HttpStatusCode)428, missingVersion.StatusCode);
        using var missingCsrf = await browser.PatchRawJsonAsync(ProfilePath, """{"countryCode":"US"}""", "\"5\"", new(SendCsrfToken: false));
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        using var unchanged = await browser.GetAsync(ProfilePath);
        await AssertProfileAsync(unchanged, null, null, null, 5);

        Assert.Equal(markerBefore + 4, await MarkerAsync(database, account.UserId));
    }

    [Fact]
    public async Task AnObsoleteLegacyValueCannotReplaceOrResurrectTheCanonicalLocation()
    {
        var database = await CreateDatabaseAsync();
        await using var factory = new DuoraWebFactory(database.AppConnectionString);
        var scenario = new IdentityScenario(database.AppConnectionString, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into profiles.profiles(user_id,region_code,country_code,subdivision_code,version,updated_at)
            values($1,'BR-SP','US','US-MA',1,'2026-01-01Z')
            """, connection);
        command.Parameters.AddWithValue(account.UserId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        using var unitedStates = await browser.GetAsync(ProfilePath);
        await AssertProfileAsync(unitedStates, "US", "US-MA", null, 1);
        // Simula a localização já apagada pelo escritor canônico, com a cópia antiga ainda obsoleta.
        command.CommandText = "update profiles.profiles set country_code=null,subdivision_code=null,version=2 where user_id=$1";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var cleared = await browser.GetAsync(ProfilePath);
        await AssertProfileAsync(cleared, null, null, null, 2);
        using var text = await browser.PatchRawJsonAsync(ProfilePath, """{"bio":"Texto sem localização"}""", "\"2\"");
        await AssertProfileAsync(text, null, null, null, 3);
        command.CommandText = "select region_code from profiles.profiles where user_id=$1";
        Assert.Equal("BR-SP", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSameStoreContinuesWritingAfterTheTestOnlyContract()
    {
        var database = await CreateDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var store = new ProfileStore(source, TimeProvider.System);
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var user = await RoleSeparationTests.InsertUserAsync(connection);
        var first = Assert.IsType<OwnProfile>(await store.UpdateOwnAsync(
            user, 0, new(null, null, null, new("BR", "BR-SP")), TestContext.Current.CancellationToken));
        Assert.Equal("BR-SP", first.RegionCode);
        await using (var legacy = new NpgsqlCommand("select region_code from profiles.profiles where user_id=$1", connection))
        {
            legacy.Parameters.AddWithValue(user);
            Assert.Equal("BR-SP", await legacy.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await SimulateContractAsync(database);
        var read = await store.GetOwnAsync(user, TestContext.Current.CancellationToken);
        Assert.Equal("BR-SP", read.RegionCode);
        var changed = Assert.IsType<OwnProfile>(await store.UpdateOwnAsync(
            user, 1, new(null, null, null, new("US", "US-DC")), TestContext.Current.CancellationToken));
        Assert.Equal("US", changed.CountryCode);
        Assert.Equal("US-DC", changed.SubdivisionCode);
        Assert.Null(changed.RegionCode);
        Assert.Equal(2, changed.Version);
    }

    private static async Task<long> MarkerAsync(ScratchDatabase database, Guid user)
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var marker = new NpgsqlCommand("select coalesce((select security_version from safety.user_guards where user_id=$1),0)", connection);
        marker.Parameters.AddWithValue(user);
        return Assert.IsType<long>(await marker.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private async Task<ScratchDatabase> CreateDatabaseAsync()
    {
        // A fixture cria e reconcilia um banco próprio no PostgreSQL descartável; nunca usa o banco compartilhado.
        var app = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        var name = new NpgsqlConnectionStringBuilder(app).Database;
        Assert.NotNull(name);
        var migrator = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString) { Database = name };
        return new ScratchDatabase(name, app, migrator.ConnectionString);
    }

    private async Task SimulateContractAsync(ScratchDatabase database)
    {
        // O único DDL deste arquivo é privado do teste e limitado ao banco empty_<GUID> criado acima.
        var target = new NpgsqlConnectionStringBuilder(database.MigratorConnectionString);
        var fixture = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString);
        Assert.Matches("^empty_[a-f0-9]{32}$", database.Name);
        Assert.Equal(database.Name, target.Database);
        Assert.NotEqual(PostgresFixture.DatabaseName, target.Database);
        Assert.Equal(fixture.Host, target.Host);
        Assert.Equal(fixture.Port, target.Port);
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            drop trigger sync_legacy_region on profiles.profiles;
            drop function profiles.sync_legacy_region();
            alter table profiles.profiles drop constraint ck_profiles_region_code;
            alter table profiles.profiles drop column region_code;
            """, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.CommandText = "select count(*) from information_schema.columns where table_schema='profiles' and table_name='profiles' and column_name='region_code'";
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        command.CommandText = "select region_code from profiles.profiles";
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, failure.SqlState);
    }

    private static async Task AssertProfileAsync(HttpResponseMessage response, string? country, string? subdivision, string? legacy, long version)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        // Campos presentes mesmo quando null: é a forma que o leitor Zod oficial exige.
        Assert.True(body.ContainsKey("displayName"));
        Assert.True(body.ContainsKey("bio"));
        Assert.True(body.ContainsKey("regionCode"));
        Assert.True(body.ContainsKey("countryCode"));
        Assert.True(body.ContainsKey("subdivisionCode"));
        Assert.Equal(country, (string?)body["countryCode"]);
        Assert.Equal(subdivision, (string?)body["subdivisionCode"]);
        Assert.Equal(legacy, (string?)body["regionCode"]);
        var expectedVersion = version.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(expectedVersion, (string?)body["version"]);
        Assert.Equal($"\"{expectedVersion}\"", response.Headers.ETag?.ToString());
    }
}
