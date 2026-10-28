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

/// <summary>Asset aprovado semeado por fora da API, com a chave e os bytes de cada derivado.</summary>
internal sealed record SeededAsset(Guid AssetId, IReadOnlyDictionary<PhotoVariant, (ObjectKey Key, byte[] Bytes)> Variants);

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
        string? idempotencyKey = null,
        string? validationFormat = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await UploadContentAsync(browser, content, idempotencyKey ?? NewKey(), validationFormat);
    }

    public static Task<HttpResponseMessage> UploadContentAsync(Browser browser, HttpContent content, string? idempotencyKey, string? validationFormat = null)
    {
        var extra = idempotencyKey is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey };
        if (validationFormat is not null)
        {
            extra["Duora-Validation-Format"] = validationFormat;
        }
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

    /// <summary>
    /// Leva um upload em quarentena a <c>Approved</c> com asset e três derivados no storage, por fora da API e sem
    /// decoder: o comportamento sob teste é o que vem depois da aprovação (API-M03/M04), não o pipeline.
    /// </summary>
    public async Task<SeededAsset> ApproveWithVariantsAsync(StoredUpload upload)
    {
        var assetId = Guid.CreateVersion7();
        var quarantine = ObjectKey.Parse(upload.QuarantineKey);
        await ExecuteAsync("update media.uploads set state = 'Approved', version = version + 1 where id = $1", upload.Id);
        await ExecuteAsync(
            """
            insert into media.media_assets (id, upload_id, owner_id, state, detected_type, width, height, sha256, approved_at, version)
            values ($1, $2, $3, 'Approved', 'image/jpeg', 1600, 1200, $4, now(), 1)
            """,
            assetId,
            upload.Id,
            upload.OwnerId,
            upload.ContentSha256 ?? new byte[32]);
        var variants = new Dictionary<PhotoVariant, (ObjectKey Key, byte[] Bytes)>();
        foreach (var variant in PhotoVariants.All)
        {
            var key = ObjectKey.ForDerivative(quarantine, variant);
            var bytes = Enumerable.Range(0, 512 + (int)variant).Select(i => (byte)(i + (int)variant * 31)).ToArray();
            await Store.PutAsync(key, new MemoryStream(bytes), bytes.Length, "image/webp", TestContext.Current.CancellationToken);
            await ExecuteAsync(
                """
                insert into media.media_variants (id, media_asset_id, kind, object_key, width, height, size_bytes)
                values ($1, $2, $3, $4, 10, 10, $5)
                """,
                Guid.CreateVersion7(),
                assetId,
                PhotoVariants.Name(variant),
                key.Value,
                (long)bytes.Length);
            variants[variant] = (key, bytes);
        }

        return new SeededAsset(assetId, variants);
    }

    public async Task<bool> ObjectExistsAsync(ObjectKey key)
    {
        await using var stream = await Store.OpenReadAsync(key, TestContext.Current.CancellationToken);
        return stream is not null;
    }

    public async Task<long> CountAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

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
