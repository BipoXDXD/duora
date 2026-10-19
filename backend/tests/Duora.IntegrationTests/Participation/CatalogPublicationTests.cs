using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Duora.Contracts.Participation;
using Duora.Core.Identity;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class CatalogPublicationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ApprovedCatalogDeliversOnlyPinnedLocaleAudioAndBlockRevokesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var identity = new IdentityScenario(connectionString, factory);
        var admin = await identity.CreatePrivilegedAccountAsync(AccountRoles.ProductAdmin);
        using var administrator = await identity.SignedInBrowserAsync(admin);
        await MfaScenario.EnableAsync(administrator, clock);
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var connection = await source.OpenConnectionAsync(ct))
        {
            await Execute(connection, "update identity.users set status='Active' where id=$1", ct, admin.UserId);
        }
        using var published = await administrator.PostJsonAsync("/api/v1/admin/experience-versions", Request());
        Assert.True(published.StatusCode == HttpStatusCode.Created, await published.Content.ReadAsStringAsync(ct));
        var contentId = Guid.Parse((string)(await MfaScenario.ReadObjectAsync(published))["experienceVersionId"]!);
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, clock.GetUtcNow(), ct, declarePreferences: true);
        await using (var connection = await source.OpenConnectionAsync(ct))
        {
            await Execute(connection, "update events.events set experience_version_id=$2 where id=$1", ct, seed.Events[0], contentId);
            await Execute(connection, "update identity.users set preferred_locale='pt-BR' where id=$1", ct, seed.Bob);
        }
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await service.RegisterAsync(seed.Events[0], user, false, ct);
            await service.RegisterAsync(seed.Events[0], user, true, ct);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value;
        await service.RespondAsync(room, seed.Alice, true, ct);
        await service.RespondAsync(room, seed.Bob, true, ct);
        using var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        using var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));
        using var third = await identity.SignedInBrowserAsync(new(seed.Third, "third@example.com", IdentityScenario.ValidPassword));
        using var bobRoom = await bob.GetAsync($"/api/v1/rooms/{room}");
        var roomJson = await bobRoom.Content.ReadAsStringAsync(ct);
        Assert.Contains("intro-pt", roomJson);
        Assert.DoesNotContain("intro-en", roomJson);
        using var bytes = await bob.GetAsync($"/api/v1/rooms/{room}/audio/intro-pt");
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("audio/wav", bytes.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Wave(), await bytes.Content.ReadAsByteArrayAsync(ct));
        Assert.True(bytes.Headers.CacheControl!.NoStore);
        using var wrongLocale = await alice.GetAsync($"/api/v1/rooms/{room}/audio/intro-pt");
        using var outsider = await third.GetAsync($"/api/v1/rooms/{room}/audio/intro-pt");
        Assert.Equal(HttpStatusCode.NotFound, wrongLocale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        using var blocked = await alice.PutAsync($"/api/v1/me/blocks/{seed.Bob}");
        Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
        using var afterBlock = await bob.GetAsync($"/api/v1/rooms/{room}/audio/intro-pt");
        Assert.Equal(HttpStatusCode.NotFound, afterBlock.StatusCode);
        await AssertImmutable(source, contentId, ct);
    }

    [Fact]
    public async Task CatalogRejectsMissingVariantInvalidBytesAndUnapprovedPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Time = clock };
        var identity = new IdentityScenario(postgres, factory);
        var admin = await identity.CreatePrivilegedAccountAsync(AccountRoles.ProductAdmin);
        using var administrator = await identity.SignedInBrowserAsync(admin);
        await MfaScenario.EnableAsync(administrator, clock);
        await using (var active = new NpgsqlConnection(postgres.ConnectionString))
        {
            await active.OpenAsync(ct);
            await Execute(active, "update identity.users set status='Active' where id=$1", ct, admin.UserId);
        }
        var valid = Request();
        var cue = valid.Audio[0];
        foreach (var request in new[]
        {
            valid with { EditorialApproved = false },
            valid with { Audio = [cue with { PtBR = null! }] },
            valid with { Audio = [cue with { PtBR = cue.PtBR with { Bytes = [0] } }] },
            valid with { PtBR = valid.PtBR with { Cards = [new("wrong-id", "Outra?", valid.PtBR.Cards[0].Options)] } },
        })
        {
            using var response = await administrator.PostJsonAsync("/api/v1/admin/experience-versions", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(ct);
        await using var query = Command(connection, "select count(*) from experiences.experience_versions where approved_by=$1", admin.UserId);
        Assert.Equal(0L, await query.ExecuteScalarAsync(ct));
    }

    private static async Task AssertImmutable(NpgsqlDataSource source, Guid content, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        foreach (var sql in new[]
        {
            "update experiences.experience_versions set editorial_state='Draft' where id=$1",
            "update experiences.experience_versions set audio_manifest='[]'::jsonb where id=$1",
            "update experiences.audio_assets set bytes=decode('00','hex') where experience_version_id=$1",
            "delete from experiences.audio_assets where experience_version_id=$1",
            "delete from experiences.experience_versions where id=$1",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, sql, ct, content));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await Execute(connection, "update experiences.experience_versions set editorial_state='Retired' where id=$1", ct, content);
        var restore = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "update experiences.experience_versions set editorial_state='Published' where id=$1", ct, content));
        Assert.Equal(PostgresErrorCodes.CheckViolation, restore.SqlState);
    }

    private static PublishCatalogRequest Request()
    {
        var bytes = Wave();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(2,
            new("Shared choices", "Explore together", [new("morning", "What would you choose?", [new("tea", "Tea"), new("walk", "A walk")])]),
            new("Escolhas em comum", "Explorem juntos", [new("morning", "O que escolheria?", [new("tea", "Chá"), new("walk", "Uma caminhada")])]),
            [new("intro", new("intro-en", "Welcome", "A welcome voice", "Synthetic offline test fixture", hash, bytes),
                new("intro-pt", "Bem-vindos", "Uma voz de boas-vindas", "Amostra sintética offline de teste", hash, bytes))], true);
    }

    private static byte[] Wave()
    {
        // A valid, silent PCM fixture. Never shipped as production media.
        var bytes = new byte[46];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 38);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 2);
        return bytes;
    }
}
