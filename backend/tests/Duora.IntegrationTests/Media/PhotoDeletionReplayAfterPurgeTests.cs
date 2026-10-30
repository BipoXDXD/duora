using System.Net;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// API-M03 com a retenção de 1 ano dos recibos (ADR-0035, P3; docs/api/media-review.md): depois que o expurgo apaga o
/// recibo, repetir o DELETE com a mesma <c>Idempotency-Key</c> não é mais replay. Banco próprio, porque o expurgo varre
/// todas as contas.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class PhotoDeletionReplayAfterPurgeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ScratchDatabase? _database;
    private DuoraWebFactory? _factory;
    private MediaScenario? _media;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ScratchDatabase Database => _database ?? throw new InvalidOperationException("Teste não inicializado.");

    private MediaScenario Media => _media ?? throw new InvalidOperationException("Teste não inicializado.");

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        _factory = new DuoraWebFactory(_database.AppConnectionString);
        _media = new MediaScenario(_database.AppConnectionString, _factory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatingTheSameKeyAfterItsReceiptWasPurgedIs412WithoutEffect()
    {
        var account = await Media.Identity.CreateConfirmedAccountAsync();
        using var browser = await Media.Identity.SignedInBrowserAsync(account);
        var photo = await UploadAsync(browser);
        var tag = await ETagAsync(browser, photo);
        var key = MediaScenario.NewKey();
        using (var deleted = await PhotoDeletionTests.DeleteAsync(browser, photo, tag, key))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var version = await Media.CountAsync("select version from media.uploads where id = $1", photo);
        await AgeReceiptsPastOneYearAsync(photo);
        Assert.Equal(1, await PurgeReceiptsAsync());

        using var repeated = await PhotoDeletionTests.DeleteAsync(browser, photo, tag, key);

        await ProblemAssert.IsProblemAsync(repeated, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal(1, await Media.CountAsync(
            "select count(*) from media.uploads where id = $1 and state = 'Deleted' and version = $2", photo, version));
        Assert.Equal(0, await Media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", photo));
        Assert.Equal(1, await Media.CountAsync(
            "select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_id = $1", photo));
    }

    private static async Task<Guid> UploadAsync(Browser browser)
    {
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return Guid.Parse((string)(await MediaScenario.ReadObjectAsync(response))["photoId"]!);
    }

    private static async Task<string> ETagAsync(Browser browser, Guid photoId)
    {
        using var status = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        return status.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Status sem ETag.");
    }

    /// <summary>
    /// O recibo nasce no relógio real do banco, e o expurgo nunca passa dele; só a dona das tabelas
    /// (<c>duora_migrator</c>) consegue envelhecer o registro, porque a aplicação não tem <c>UPDATE</c> nele.
    /// </summary>
    private async Task AgeReceiptsPastOneYearAsync(Guid photo)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "update media.photo_deletion_commands set created_at = created_at - interval '2 years' where photo_id = $1",
            connection);
        command.Parameters.AddWithValue(photo);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    private async Task<long> PurgeReceiptsAsync() => await Media.CountAsync(
        "select receipts from media.purge_photo_deletion_records(statement_timestamp(), 1000)");
}
