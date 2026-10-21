using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Duora.Contracts.Participation;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class RoutesCatalogPublicationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task PublicationValidatesBilingualStructurePublicAudioAndRecentMfa()
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
        await using var connection = await source.OpenConnectionAsync(ct);
        await Execute(connection, "update identity.users set status='Active' where id=$1", ct, admin.UserId);
        var valid = Request();
        var cue = valid.Audio[0];
        foreach (var request in new[]
        {
            valid with { EditorialApproved = false },
            valid with { Audio = [] },
            valid with { Audio = [cue with { PtBR = null! }] },
            valid with { Audio = [cue with { PtBR = cue.PtBR with { Bytes = [0] } }] },
            valid with { Audio = [cue with { PtBR = cue.PtBR with { AssetId = cue.EnUS.AssetId } }] },
            valid with { Audio = [cue with { CueId = "private-clue" }] },
            valid with { Audio = [cue with { EnUS = cue.EnUS with { Transcript = "Private clue" } }] },
            valid with { Audio = [cue with { EnUS = cue.EnUS with { AccessibleDescription = "Private clue" } }] },
            valid with { PtBR = valid.PtBR with { Stops = [valid.PtBR.Stops[0] with { StepId = "wrong" }] } },
            valid with { PtBR = valid.PtBR with { ClueSets = [[new("Uma pista"), new("Outra pista")], [new("Segredo")]] } },
            valid with { PtBR = valid.PtBR with { ClueSets = [null!, [new("Segredo")]] } },
        })
        {
            using var rejected = await administrator.PostJsonAsync("/api/v1/admin/route-experience-versions", request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        var unknown = JsonSerializer.SerializeToNode(valid, JsonSerializerOptions.Web)!.AsObject();
        unknown["enUS"]!["stops"]![0]!["script"] = "unknown";
        using var unknownResponse = await administrator.PostJsonAsync("/api/v1/admin/route-experience-versions", unknown);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
        await using (var query = Command(connection, "select count(*) from experiences.experience_versions where approved_by=$1", admin.UserId))
        {
            Assert.Equal(0L, await query.ExecuteScalarAsync(ct));
        }
        using var published = await administrator.PostJsonAsync("/api/v1/admin/route-experience-versions", valid);
        Assert.True(published.StatusCode == HttpStatusCode.Created, await published.Content.ReadAsStringAsync(ct));
        var id = Guid.Parse((string)(await MfaScenario.ReadObjectAsync(published))["experienceVersionId"]!);
        await using (var query = Command(connection, "select experience_kind from experiences.experience_versions where id=$1", id))
        {
            Assert.Equal("unlikely-routes", await query.ExecuteScalarAsync(ct));
        }
        foreach (var sql in new[]
        {
            "update experiences.experience_versions set experience_kind='constellation' where id=$1",
            "update experiences.experience_versions set content_en='{}'::jsonb where id=$1",
            "update experiences.experience_versions set audio_manifest='[]'::jsonb where id=$1",
            "delete from experiences.experience_versions where id=$1",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, sql, ct, id));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, clock.GetUtcNow(), ct);
        await using (var legacy = Command(connection, "select experience_version_id from events.events where id=$1", seed.Events[0]))
        {
            var legacyId = (Guid)(await legacy.ExecuteScalarAsync(ct))!;
            await using var kind = Command(connection, "select experience_kind from experiences.experience_versions where id=$1", legacyId);
            Assert.Equal("constellation", await kind.ExecuteScalarAsync(ct));
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
                "update experiences.experience_versions set experience_kind='unlikely-routes' where id=$1", ct, legacyId));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        clock.Advance(TimeSpan.FromMinutes(6));
        using var staleMfa = await administrator.PostJsonAsync("/api/v1/admin/route-experience-versions", valid);
        Assert.Equal(HttpStatusCode.Unauthorized, staleMfa.StatusCode);
    }

    internal static PublishRoutesCatalogRequest Request()
    {
        var bytes = Wave();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(1,
            new("Imaginary route", "Choose a route together", "Propose and confirm", [new("stop", "Where next?", [new("park", "Park"), new("lake", "Lake")])],
                [[new("The park has flowers")], [new("The lake has boats")]]),
            new("Rota imaginária", "Escolham uma rota juntos", "Proponham e confirmem", [new("stop", "Para onde?", [new("park", "Parque"), new("lake", "Lago")])],
                [[new("O parque tem flores")], [new("O lago tem barcos")]]),
            [new("intro", new("route-intro-en", "Choose a route together", "Imaginary route", "Synthetic offline fixture", hash, bytes),
                new("route-intro-pt", "Escolham uma rota juntos", "Rota imaginária", "Amostra sintética offline", hash, bytes))], true);
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
