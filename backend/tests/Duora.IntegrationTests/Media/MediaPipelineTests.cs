extern alias worker;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SixLabors.ImageSharp;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// T-16 de ponta a ponta: upload pela API, um passo do consumidor <c>media-processing</c> do Worker real, e o resultado
/// lido por fora (banco, storage) e pela API. Banco próprio do teste, porque o consumidor pega qualquer entrega da fila.
/// </summary>
[Trait("Acceptance", "T-16")]
public sealed class MediaPipelineTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = "";
    private DuoraWebFactory? _web;
    private IHost? _worker;
    private readonly List<IHost> _otherWorkers = [];
    private DirectoryInfo? _mail;

    private DuoraWebFactory Web => _web ?? throw new InvalidOperationException("Teste não inicializado.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync(Cancellation);
        _web = new DuoraWebFactory(_connectionString);
        _mail = Directory.CreateTempSubdirectory("duora-mail-");
        _worker = WorkerBuilder().Build();
    }

    public async ValueTask DisposeAsync()
    {
        _worker?.Dispose();
        _otherWorkers.ForEach(worker => worker.Dispose());
        if (_web is not null)
        {
            await _web.DisposeAsync();
        }

        _mail?.Delete(recursive: true);
    }

    [Fact]
    public async Task AJpegWithGpsIsApprovedAsCleanVariantsAndTheOriginalIsDeleted()
    {
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.JpegWithGpsAndRotation(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();

        Assert.Equal(1, await DispatchOnceAsync());

        Assert.Equal(("Approved", null), await StatusAsync(browser, photoId));
        Assert.Null(await media.ReadQuarantineAsync(upload));
        var asset = await ReadAssetAsync(photoId);
        Assert.Equal(("Approved", "image/jpeg", 32, 64), (asset.State, asset.DetectedType, asset.Width, asset.Height));
        Assert.Null(asset.OriginalKey);
        Assert.Equal(upload.ContentSha256, asset.Sha256);
        Assert.Equal(["card", "full", "thumb"], await VariantKindsAsync(asset.Id));
        foreach (var variant in PhotoVariants.All)
        {
            var bytes = await ReadDerivativeAsync(media, upload.QuarantineKey, variant);
            Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(bytes));
            Assert.Null(Image.Identify(bytes).Metadata.ExifProfile);
            Assert.DoesNotContain("CanaryCamera", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        }

        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
    }

    public static TheoryData<string, string, string> HostileFiles => new()
    {
        { "svg", "image/png", "UNSUPPORTED_FORMAT" },
        { "html", "image/jpeg", "UNSUPPORTED_FORMAT" },
        { "exe", "image/webp", "UNSUPPORTED_FORMAT" },
        { "png-as-jpeg", "image/jpeg", "TYPE_MISMATCH" },
        { "bomb", "image/png", "TOO_MANY_PIXELS" },
        { "apng", "image/png", "ANIMATED" },
        { "animated-webp", "image/webp", "ANIMATED" },
        { "truncated-png", "image/png", "CORRUPT_IMAGE" },
        { "truncated-jpeg", "image/jpeg", "CORRUPT_IMAGE" },
        { "png-wrong-crc", "image/png", "CORRUPT_IMAGE" },
        { "png-garbage-idat", "image/png", "CORRUPT_IMAGE" },
        { "jpeg-tampered-dht", "image/jpeg", "CORRUPT_IMAGE" },
    };

    [Theory]
    [MemberData(nameof(HostileFiles))]
    public async Task AHostileFileEndsRejectedWithoutDerivativesAndWithoutTheOriginal(string file, string contentType, string code)
    {
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, Hostile(file), contentType);
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();

        Assert.Equal(1, await DispatchOnceAsync());

        Assert.Equal(("Rejected", code), await StatusAsync(browser, photoId));
        Assert.Null(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.All(PhotoVariants.All, variant => Assert.False(File.Exists(DerivativePath(upload.QuarantineKey, variant))));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOriginalChangedInTheStorageIsNeverDecodedAndEndsRejected(bool sameSize)
    {
        // M4: o Worker recalcula o SHA-256 do original e compara com o content_sha256 gravado na recepção. Um objeto
        // trocado na quarentena (outro processo, corrupção) não é decodificado nem aprovado, e o upload não fica preso
        // em Processing ocupando vaga.
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var original = TestImages.Jpeg();
        var photoId = await UploadAsync(browser, original, "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        // Mesmo tamanho com um byte trocado (só o hash pega) ou outro arquivo inteiro (o tamanho já pega).
        var swapped = sameSize ? [.. original] : TestImages.Png();
        if (sameSize)
        {
            swapped[^10] ^= 0xFF;
        }

        await using (var content = new MemoryStream(swapped))
        {
            await media.Store.PutAsync(ObjectKey.Parse(upload.QuarantineKey), content, swapped.Length, "image/png", Cancellation);
        }

        Assert.Equal(1, await DispatchOnceAsync());

        await AssertAbandonedAsync(media, browser, upload, "OriginalMismatch");
    }

    [Fact]
    public async Task AnOriginalMissingFromTheQuarantineEndsRejected()
    {
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        await media.Store.DeleteAsync(ObjectKey.Parse(upload.QuarantineKey), Cancellation);

        Assert.Equal(1, await DispatchOnceAsync());

        await AssertAbandonedAsync(media, browser, upload, "OriginalMissing");
    }

    [Fact]
    public async Task ADecoderThatKeepsFailingEndsRejectedAfterTheLastAttempt()
    {
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        // O relógio do Worker parte de depois da entrega gravada pelo Web e só anda quando o teste manda.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var worker = StartWorker(
            services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IPhotoSanitizer>(new ThrowingSanitizer());
            },
            "--Media:Processing:MaxAttempts=2");

        Assert.Equal(1, await DispatchOnceAsync(worker));
        Assert.Equal(("Processing", null), await StatusAsync(browser, photoId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await DispatchOnceAsync(worker));

        await AssertAbandonedAsync(media, browser, upload, nameof(ThrowingSanitizer.DecoderBugException));
    }

    [Fact]
    public async Task TheDerivativeKeysCarryNoPhotoIdAndSurviveARetry()
    {
        // TM-41 e ADR-0025 item 5: chave aleatória gerada pelo servidor, nunca o photoId público. Item 6: a tentativa
        // que retoma grava nas mesmas chaves da que caiu, sem deixar órfãos.
        var refusing = StartWorker(
            services =>
            {
                var real = services.Single(descriptor => descriptor.ServiceType == typeof(IObjectStore));
                services.Remove(real);
                services.AddSingleton<IObjectStore>(provider =>
                    new DerivativeRefusingStore((IObjectStore)real.ImplementationFactory!(provider)));
            },
            "--Media:Processing:MaxAttempts=2");
        var (_, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        Assert.Equal(1, await DispatchOnceAsync(refusing));
        var written = refusing.Services.GetRequiredService<IObjectStore>() is DerivativeRefusingStore store
            ? store.Written.Single()
            : throw new InvalidOperationException("Store do teste não registrado.");
        await ExecuteAsync(
            """
            update ops.outbox_deliveries set available_at = now() - interval '1 minute'
            where outbox_event_id in (select id from ops.outbox_events where resource_id = $1)
            """,
            photoId);

        Assert.Equal(1, await DispatchOnceAsync());

        Assert.Equal(("Approved", null), await StatusAsync(browser, photoId));
        var keys = await VariantKeysAsync(photoId);
        Assert.Equal(PhotoVariants.All.Count, keys.Length);
        Assert.Contains(written, keys);
        Assert.All(keys, key =>
        {
            Assert.DoesNotContain(photoId.ToString("N"), key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(photoId.ToString("D"), key, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ADeliveryAlreadyPastTheAttemptCeilingIsAbandonedWithoutDecoding()
    {
        // Imagem que derruba o processo: nenhuma tentativa chega ao catch, o lease vence e a entrega volta com mais
        // uma tentativa. Acima do teto, o Worker encerra o upload sem decodificar de novo, em vez de cair em laço.
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        await ExecuteAsync(
            """
            update ops.outbox_deliveries set attempts = 2
            where outbox_event_id in (select id from ops.outbox_events where resource_id = $1)
            """,
            photoId);
        var sanitizer = new CountingSanitizer();
        var worker = StartWorker(
            services => services.AddSingleton<IPhotoSanitizer>(sanitizer),
            "--Media:Processing:MaxAttempts=2");

        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.Equal(0, sanitizer.Calls);
        await AssertAbandonedAsync(media, browser, upload, "AttemptsExhausted");
    }

    [Fact]
    public async Task ADeliveryPastItsDeadlineEndsTheUploadRejectedAndLogsIt()
    {
        // Issue #66: a entrega vencida (24 h) não pode deixar o upload em Quarantined ocupando vaga de pendente.
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow + Upload.AbandonAfterDelay + TimeSpan.FromMinutes(1));
        var logs = new CapturingLoggerProvider();
        var worker = StartWorker(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ILoggerProvider>(logs);
        });

        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.Equal(("Rejected", "PROCESSING_LIMIT"), await StatusAsync(browser, photoId));
        Assert.Null(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.Equal("Expired", await DeliveryStatusAsync(photoId));
        Assert.Contains(
            logs.Entries,
            entry => entry.Category == typeof(MediaProcessingDispatcher).FullName
                && entry.Level == LogLevel.Error
                && entry.Text.Contains("prazo", StringComparison.Ordinal)
                && entry.Text.Contains(photoId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStorageThatRefusesTheDerivativesEndsRejectedAfterTheLastAttempt()
    {
        var worker = StartWorker(
            services =>
            {
                var real = services.Single(descriptor => descriptor.ServiceType == typeof(IObjectStore));
                services.Remove(real);
                services.AddSingleton<IObjectStore>(provider =>
                    new DerivativeRefusingStore((IObjectStore)real.ImplementationFactory!(provider)));
            },
            "--Media:Processing:MaxAttempts=1");
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();

        Assert.Equal(1, await DispatchOnceAsync(worker));

        await AssertAbandonedAsync(media, browser, upload, nameof(IOException));
    }

    [Fact]
    public async Task AfterACorruptUploadTheWorkerProcessesTheNextOne()
    {
        var (_, browser) = await SignInAsync();
        using var __ = browser;
        var corrupt = await UploadAsync(browser, TestImages.Truncated(TestImages.Png(400, 300)), "image/png");
        var good = await UploadAsync(browser, TestImages.Png(400, 300), "image/png");

        Assert.Equal(1, await DispatchOnceAsync());
        Assert.Equal(1, await DispatchOnceAsync());
        Assert.Equal(0, await DispatchOnceAsync());

        Assert.Equal(("Rejected", "CORRUPT_IMAGE"), await StatusAsync(browser, corrupt));
        Assert.Equal(("Approved", null), await StatusAsync(browser, good));
    }

    [Fact]
    public async Task ARepeatedDeliveryOfADecidedUploadChangesNothing()
    {
        // At-least-once: a mesma entrega processada de novo (outro worker depois de um lease vencido) não cria outro
        // asset nem mexe no veredito.
        var (_, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.WebP(), "image/webp");
        Assert.Equal(1, await DispatchOnceAsync());
        await ExecuteAsync(
            """
            update ops.outbox_deliveries set status = 'Pending', completed_at = null, available_at = now() - interval '1 minute'
            where outbox_event_id in (select id from ops.outbox_events where resource_id = $1)
            """,
            photoId);
        await ExecuteAsync(
            "update ops.outbox_events set payload = jsonb_build_object('uploadId', $1::text) where resource_id = $1",
            photoId);

        Assert.Equal(1, await DispatchOnceAsync());

        Assert.Equal(("Approved", null), await StatusAsync(browser, photoId));
        Assert.Equal(1, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
    }

    [Fact]
    public async Task ApprovedPhotoResponsesStillCarryNoStorageKey()
    {
        var (_, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        await DispatchOnceAsync();

        using var response = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");
        var text = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.DoesNotContain("derivatives", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantine/", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(photoId.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbandoningOneUploadKeepsTheDerivativesOfAnother()
    {
        // A limpeza do abandono apaga só as chaves do upload abandonado, nunca as de um aprovado.
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var approved = await UploadAsync(browser, TestImages.WebP(), "image/webp");
        var approvedKey = (await media.UploadsOfAsync(await OwnerOfAsync(approved))).Single().QuarantineKey;
        Assert.Equal(1, await DispatchOnceAsync());
        var before = new Dictionary<PhotoVariant, byte[]>();
        foreach (var variant in PhotoVariants.All)
        {
            before[variant] = await ReadDerivativeAsync(media, approvedKey, variant);
        }

        var worker = StartWorker(
            services =>
            {
                var real = services.Single(descriptor => descriptor.ServiceType == typeof(IObjectStore));
                services.Remove(real);
                services.AddSingleton<IObjectStore>(provider =>
                    new DerivativeRefusingStore((IObjectStore)real.ImplementationFactory!(provider)));
            },
            "--Media:Processing:MaxAttempts=1");
        var abandoned = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.Equal(("Rejected", "PROCESSING_LIMIT"), await StatusAsync(browser, abandoned));
        Assert.Equal(("Approved", null), await StatusAsync(browser, approved));
        foreach (var variant in PhotoVariants.All)
        {
            Assert.Equal(before[variant], await ReadDerivativeAsync(media, approvedKey, variant));
        }
    }

    /// <summary>
    /// Falha terminal: upload em <c>Rejected PROCESSING_LIMIT</c> (libera a vaga de pendentes), original fora da
    /// quarentena, nenhum asset nem derivado, e a entrega em <c>Failed</c> com a causa para a operação.
    /// </summary>
    private async Task AssertAbandonedAsync(MediaScenario media, Browser browser, StoredUpload upload, string cause)
    {
        Assert.Equal(("Rejected", "PROCESSING_LIMIT"), await StatusAsync(browser, upload.Id));
        Assert.Null(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", upload.Id));
        Assert.All(PhotoVariants.All, variant => Assert.False(File.Exists(DerivativePath(upload.QuarantineKey, variant))));
        Assert.Equal("Failed", await DeliveryStatusAsync(upload.Id));
        Assert.Equal(cause, await LastErrorAsync(upload.Id));
    }

    private HostApplicationBuilder WorkerBuilder(params string[] extra) => WorkerHost.CreateBuilder(
    [
        "--environment", "Testing",
        $"--ConnectionStrings:Duora={_connectionString}",
        $"--Duora:PublicOrigin={DuoraWebFactory.PublicOrigin}",
        $"--Email:PickupDirectory={_mail!.FullName}",
        "--Media:Review=TechnicalOnly",
        "--Media:Storage:Provider=FileSystem",
        $"--Media:Storage:FileSystemRoot={Web.MediaRoot}",
        .. extra,
    ]);

    private IHost StartWorker(Action<IServiceCollection> replace, params string[] extra)
    {
        var builder = WorkerBuilder(extra);
        replace(builder.Services);
        var worker = builder.Build();
        _otherWorkers.Add(worker);
        return worker;
    }

    private static byte[] Hostile(string file) => file switch
    {
        "svg" => TestImages.Text("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
        "html" => TestImages.Text("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>"),
        "exe" => TestImages.Text("MZ\u0090\u0000\u0003\u0000\u0000\u0000\u0004\u0000PE\u0000\u0000"),
        "png-as-jpeg" => TestImages.Png(),
        "bomb" => TestImages.DecompressionBombPng(),
        "apng" => TestImages.AnimatedPng(),
        "animated-webp" => TestImages.AnimatedWebP(),
        "truncated-png" => TestImages.Truncated(TestImages.Png(400, 300)),
        "truncated-jpeg" => TestImages.Truncated(TestImages.Jpeg(400, 300)),
        "png-wrong-crc" or "png-garbage-idat" or "jpeg-tampered-dht" => PhotoSanitizerTests.CorruptFixture(file),
        _ => throw new ArgumentOutOfRangeException(nameof(file), file, "Arquivo hostil sem gerador."),
    };

    private async Task<(MediaScenario Media, Browser Browser)> SignInAsync()
    {
        var media = new MediaScenario(_connectionString, Web);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        return (media, await media.Identity.SignedInBrowserAsync(account));
    }

    private static async Task<Guid> UploadAsync(Browser browser, byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await MediaScenario.UploadContentAsync(browser, content, MediaScenario.NewKey());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return Guid.Parse((string)(await MediaScenario.ReadObjectAsync(response))["photoId"]!);
    }

    private static async Task<(string Status, string? RejectionCode)> StatusAsync(Browser browser, Guid photoId)
    {
        using var response = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await MediaScenario.ReadObjectAsync(response);
        return ((string)body["status"]!, (string?)body["rejectionCode"]);
    }

    private Task<int> DispatchOnceAsync() => DispatchOnceAsync(_worker ?? throw new InvalidOperationException("Sem worker."));

    private static async Task<int> DispatchOnceAsync(IHost worker)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediaProcessingDispatcher>().DispatchOnceAsync(Cancellation);
    }

    private string DerivativePath(string quarantineKey, PhotoVariant variant) =>
        Path.Combine(Web.MediaRoot, ObjectKey.ForDerivative(ObjectKey.Parse(quarantineKey), variant).Value);

    private static async Task<byte[]> ReadDerivativeAsync(MediaScenario media, string quarantineKey, PhotoVariant variant)
    {
        var key = ObjectKey.ForDerivative(ObjectKey.Parse(quarantineKey), variant);
        await using var stream = await media.Store.OpenReadAsync(key, Cancellation)
            ?? throw new InvalidOperationException($"Derivado {variant} ausente.");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Cancellation);
        return copy.ToArray();
    }

    private async Task<Guid> OwnerOfAsync(Guid uploadId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select owner_id from media.uploads where id = $1", connection);
        command.Parameters.AddWithValue(uploadId);
        return (Guid)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private async Task<(Guid Id, string State, string DetectedType, int Width, int Height, string? OriginalKey, byte[] Sha256)>
        ReadAssetAsync(Guid uploadId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select id, state, detected_type, width, height, original_key, sha256 from media.media_assets where upload_id = $1",
            connection);
        command.Parameters.AddWithValue(uploadId);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        Assert.True(await reader.ReadAsync(Cancellation), "Asset não criado.");
        return (
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            (byte[])reader[6]);
    }

    private async Task<string[]> VariantKindsAsync(Guid assetId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select kind from media.media_variants where media_asset_id = $1 order by kind",
            connection);
        command.Parameters.AddWithValue(assetId);
        var kinds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        while (await reader.ReadAsync(Cancellation))
        {
            kinds.Add(reader.GetString(0));
        }

        return [.. kinds];
    }

    private async Task<string[]> VariantKeysAsync(Guid uploadId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select v.object_key from media.media_variants v join media.media_assets a on a.id = v.media_asset_id
            where a.upload_id = $1
            """,
            connection);
        command.Parameters.AddWithValue(uploadId);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        while (await reader.ReadAsync(Cancellation))
        {
            keys.Add(reader.GetString(0));
        }

        return [.. keys];
    }

    private async Task<string> DeliveryStatusAsync(Guid uploadId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select d.status from ops.outbox_deliveries d join ops.outbox_events e on e.id = d.outbox_event_id
            where e.resource_id = $1 and d.consumer = 'media-processing'
            """,
            connection);
        command.Parameters.AddWithValue(uploadId);
        return (string)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private async Task<string?> LastErrorAsync(Guid uploadId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select d.last_error from ops.outbox_deliveries d join ops.outbox_events e on e.id = d.outbox_event_id
            where e.resource_id = $1 and d.consumer = 'media-processing'
            """,
            connection);
        command.Parameters.AddWithValue(uploadId);
        return await command.ExecuteScalarAsync(Cancellation) as string;
    }

    private async Task<long> CountAsync(string sql, Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(id);
        return (long)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Cancellation);
        return connection;
    }

    /// <summary>Decoder com defeito: exceção que o sanitizador não mapeia para rejeição.</summary>
    private sealed class ThrowingSanitizer : IPhotoSanitizer
    {
        public Task<SanitizeOutcome> SanitizeAsync(PhotoFormat declared, ReadOnlyMemory<byte> original, CancellationToken cancellationToken) =>
            throw new DecoderBugException();

        public sealed class DecoderBugException() : Exception("Defeito sintético do decoder.");
    }

    /// <summary>Decoder que só conta as chamadas: o teste prova que ele nunca foi alcançado.</summary>
    private sealed class CountingSanitizer : IPhotoSanitizer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<SanitizeOutcome> SanitizeAsync(PhotoFormat declared, ReadOnlyMemory<byte> original, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<SanitizeOutcome>(new SanitizeOutcome.Rejected(PhotoRejection.CorruptImage));
        }
    }

    /// <summary>Storage que grava o primeiro derivado e recusa os seguintes, como uma queda no meio da publicação.</summary>
    private sealed class DerivativeRefusingStore(IObjectStore inner) : IObjectStore
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _written = new();
        private int _derivatives;

        /// <summary>Chaves dos derivados que chegaram ao storage de verdade.</summary>
        public IReadOnlyCollection<string> Written => _written;

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            if (!key.Value.StartsWith("derivatives/", StringComparison.Ordinal))
            {
                return inner.PutAsync(key, content, length, contentType, cancellationToken);
            }

            if (Interlocked.Increment(ref _derivatives) > 1)
            {
                throw new IOException("Storage sintético recusou a gravação.");
            }

            _written.Enqueue(key.Value);
            return inner.PutAsync(key, content, length, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }
}
