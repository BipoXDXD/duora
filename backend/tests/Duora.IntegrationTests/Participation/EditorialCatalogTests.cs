using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class EditorialCatalogTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("constellation", "/api/v1/admin/experience-versions")]
    [InlineData("unlikely-routes", "/api/v1/admin/route-experience-versions")]
    public async Task RealEditorialArtifactsPublishOnlyAfterApprovalAndRemainLocalizedAndImmutable(string kind, string endpoint)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateMigratedDatabaseAsync(ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(database) { Time = clock };
        var identity = new IdentityScenario(database, factory);
        var admin = await identity.CreatePrivilegedAccountAsync(AccountRoles.ProductAdmin);
        using var administrator = await identity.SignedInBrowserAsync(admin);
        await MfaScenario.EnableAsync(administrator, clock);
        await using var source = NpgsqlDataSource.Create(database);
        await using var connection = await source.OpenConnectionAsync(ct);
        await Execute(connection, "update identity.users set status='Active' where id=$1", ct, admin.UserId);
        var pending = Read(kind);
        Assert.False((bool)pending["editorialApproved"]!);
        using (var unapproved = await administrator.PostJsonAsync(endpoint, pending))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unapproved.StatusCode);
        }
        // A test-only administrator approval exercises admission in a disposable database.
        // The checked-in artifact and review payload remain unapproved; this is not editorial sign-off.
        var approved = pending.DeepClone().AsObject();
        approved["editorialApproved"] = true;
        var missing = approved.DeepClone().AsObject();
        missing["audio"]![0]!["ptBR"] = null;
        var corrupt = approved.DeepClone().AsObject();
        corrupt["audio"]![0]!["enUS"]!["sha256"] = new string('0', 64);
        var mismatched = approved.DeepClone().AsObject();
        if (kind == "constellation")
        {
            mismatched["ptBR"]!["cards"]![0]!["cardId"] = "changed-after-translation";
        }
        else
        {
            mismatched["ptBR"]!["stops"]![0]!["stepId"] = "changed-after-translation";
        }
        foreach (var invalid in new[] { missing, corrupt, mismatched })
        {
            using var rejected = await administrator.PostJsonAsync(endpoint, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        await using (var query = Command(connection, "select count(*) from experiences.experience_versions where approved_by=$1", admin.UserId))
        {
            Assert.Equal(0L, await query.ExecuteScalarAsync(ct));
        }
        using var published = await administrator.PostJsonAsync(endpoint, approved);
        Assert.True(published.StatusCode == HttpStatusCode.Created, await published.Content.ReadAsStringAsync(ct));
        var id = Guid.Parse((string)(await MfaScenario.ReadObjectAsync(published))["experienceVersionId"]!);
        using (var nextPublication = await administrator.PostJsonAsync(endpoint, approved))
        {
            Assert.Equal(HttpStatusCode.Created, nextPublication.StatusCode);
        }
        var seed = await BrowserScenarioSeed.InitializeAsync(database, clock.GetUtcNow(), ct, declarePreferences: true);
        await Execute(connection, "update events.events set experience_version_id=$2 where id=$1", ct, seed.Events[0], id);
        await Execute(connection, "update identity.users set preferred_locale='pt-BR' where id=$1", ct, seed.Bob);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, ct));
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value;
        await service.RespondAsync(room, seed.Alice, true, ct);
        await service.RespondAsync(room, seed.Bob, true, ct);
        var left = (await service.RoomAsync(room, seed.Alice, ct))!;
        var right = (await service.RoomAsync(room, seed.Bob, ct))!;
        Assert.Equal("en-US", left.Locale);
        Assert.Equal("pt-BR", right.Locale);
        Assert.Equal(left.Game?.ContentVersion ?? left.RoutesGame!.ContentVersion, right.Game?.ContentVersion ?? right.RoutesGame!.ContentVersion);
        Assert.Equal(left.Game?.RulesVersion ?? left.RoutesGame!.RulesVersion, right.Game?.RulesVersion ?? right.RoutesGame!.RulesVersion);
        if (kind == "constellation")
        {
            Assert.Equal(left.Game!.CurrentCard!.CardId, right.Game!.CurrentCard!.CardId);
            Assert.Equal(left.Game.CurrentCard.Options.Select(option => option.OptionId), right.Game.CurrentCard.Options.Select(option => option.OptionId));
        }
        else
        {
            Assert.Empty(left.RoutesGame!.MyClues);
            Assert.Empty(right.RoutesGame!.MyClues);
        }
        foreach (var (user, own, other, locale) in new[] { (seed.Alice, left, right, "enUS"), (seed.Bob, right, left, "ptBR") })
        {
            var cue = Assert.Single(own.Audio);
            Assert.Equal((string)approved[locale]!["objective"]!, cue.Transcript);
            var audio = await service.AudioAsync(room, user, cue.AssetId, ct);
            Assert.NotNull(audio);
            Assert.Equal((string)approved["audio"]![0]![locale]!["bytes"]!, Convert.ToBase64String(audio.Bytes));
            Assert.Equal(cue.Sha256, Convert.ToHexStringLower(SHA256.HashData(audio.Bytes)));
            Assert.Null(await service.AudioAsync(room, user, other.Audio[0].AssetId, ct));
        }
        Assert.Null(await service.RoomAsync(room, seed.Third, ct));
        Assert.Null(await service.AudioAsync(room, seed.Third, left.Audio[0].AssetId, ct));
        // Publishing another version cannot repoint an existing session or replace its media.
        await using (var pinned = Command(connection, "select experience_version_id from experiences.game_sessions where id=$1", room))
        {
            Assert.Equal(id, await pinned.ExecuteScalarAsync(ct));
        }
        var mutation = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "update experiences.experience_versions set content_en='{}'::jsonb where id=$1", ct, id));
        Assert.Equal(PostgresErrorCodes.CheckViolation, mutation.SqlState);
        await using (var query = Command(connection, "select bytes from experiences.audio_assets where experience_version_id=$1 and asset_id=$2", id, left.Audio[0].AssetId))
        {
            Assert.Equal((string)approved["audio"]![0]!["enUS"]!["bytes"]!, Convert.ToBase64String((byte[])(await query.ExecuteScalarAsync(ct))!));
        }
    }

    private static JsonObject Read(string kind)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "content", "experiences", "2026-10-review")))
        {
            directory = directory.Parent;
        }
        var root = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Editorial catalog root missing."), "content", "experiences", "2026-10-review");
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(root, kind + ".json")))!.AsObject();
        Assert.Equal("pending_human_review", (string)node["review"]!["status"]!);
        Assert.Null(node["review"]!["approvedBy"]);
        node.Remove("kind");
        node.Remove("review");
        node["editorialApproved"] = false;
        foreach (var cue in node["audio"]!.AsArray())
        {
            foreach (var locale in new[] { "enUS", "ptBR" })
            {
                var variant = cue![locale]!.AsObject();
                var bytes = File.ReadAllBytes(Path.Combine(root, (string)variant["path"]!));
                Assert.Equal((string)variant["sha256"]!, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Assert.InRange(((string)variant["transcript"]!).Length, 1, 120);
                Assert.Contains(bytes.Skip(44), value => value != 0);
                variant.Remove("path");
                variant["bytes"] = Convert.ToBase64String(bytes);
            }
        }
        return node;
    }
}
