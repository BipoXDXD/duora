using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>Linha de <c>media.uploads</c> como o teste a lê, por fora da aplicação.</summary>
internal sealed record StoredUpload(
    Guid Id,
    Guid OwnerId,
    string State,
    string ClientKey,
    string QuarantineKey,
    string DeclaredContentType,
    long SizeBytes,
    byte[]? ContentSha256,
    string? RejectionCode);

/// <summary>Passos de upload pela API e leitura do estado por fora, com contas sintéticas únicas por teste.</summary>
internal sealed class MediaScenario(string connectionString, DuoraWebFactory factory)
{
    public MediaScenario(PostgresFixture postgres, DuoraWebFactory factory)
        : this(postgres.ConnectionString, factory)
    {
    }

    public const string PhotosPath = "/api/v1/me/photos";

    /// <summary>Bytes com assinatura de JPEG: a API não decodifica, então não precisa ser imagem válida.</summary>
    public static byte[] JpegLike(int size = 2048, byte fill = 0x42)
    {
        var bytes = Enumerable.Repeat(fill, size).ToArray();
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    public static string NewKey() => Guid.NewGuid().ToString("D");

    public IdentityScenario Identity { get; } = new(connectionString, factory);

    public FileSystemObjectStore Store { get; } = new(factory.MediaRoot);

    public static async Task<HttpResponseMessage> UploadAsync(
        Browser browser,
        byte[] bytes,
        string contentType = "image/jpeg",
        string? idempotencyKey = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await UploadContentAsync(browser, content, idempotencyKey ?? NewKey());
    }

    public static Task<HttpResponseMessage> UploadContentAsync(Browser browser, HttpContent content, string? idempotencyKey)
    {
        var extra = idempotencyKey is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey };
        return browser.PostContentAsync(PhotosPath, content, new MutationHeaders(Extra: extra));
    }

    public static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Corpo vazio.");
    }

    public async Task<IReadOnlyList<StoredUpload>> UploadsOfAsync(Guid ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, owner_id, state, client_key, quarantine_key, declared_content_type, size_bytes, content_sha256,
                   rejection_code
            from media.uploads where owner_id = $1 order by created_at, id
            """,
            connection);
        command.Parameters.AddWithValue(ownerId);
        var uploads = new List<StoredUpload>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            uploads.Add(new StoredUpload(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : (byte[])reader[7],
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return uploads;
    }

    /// <summary>Eventos de processamento de mídia de um upload: (payload, consumidor, status da entrega).</summary>
    public async Task<IReadOnlyList<(string Payload, string Consumer, string Status)>> ProcessingEventsAsync(Guid uploadId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select e.payload::text, d.consumer, d.status
            from ops.outbox_events e join ops.outbox_deliveries d on d.outbox_event_id = e.id
            where e.type = 'media.upload-quarantined' and e.resource_id = $1
            """,
            connection);
        command.Parameters.AddWithValue(uploadId);
        var events = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            events.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return events;
    }

    public async Task<byte[]?> ReadQuarantineAsync(StoredUpload upload)
    {
        await using var stream = await Store.OpenReadAsync(ObjectKey.Parse(upload.QuarantineKey), TestContext.Current.CancellationToken);
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    /// <summary>Todos os arquivos guardados pelo adaptador de teste, para provar que nada ficou para trás.</summary>
    public int StoredObjectCount() =>
        Directory.Exists(factory.MediaRoot)
            ? Directory.EnumerateFiles(factory.MediaRoot, "*", SearchOption.AllDirectories).Count()
            : 0;

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
