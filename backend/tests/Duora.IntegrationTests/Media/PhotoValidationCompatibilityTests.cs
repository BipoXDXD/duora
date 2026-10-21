using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>ADR-0034: negociação por pedido, legado exato, precedência e ausência de efeitos das duas recusas.</summary>
public sealed class PhotoValidationCompatibilityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = "";

    public async ValueTask InitializeAsync() =>
        _connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(true, false, "pt-BR")]
    [InlineData(true, false, "en-US")]
    [InlineData(false, false, "pt-BR")]
    [InlineData(false, false, "en-US")]
    [InlineData(true, true, "pt-BR")]
    [InlineData(true, true, "en-US")]
    [InlineData(false, true, "pt-BR")]
    [InlineData(false, true, "en-US")]
    public async Task BothFormatsPreserveTheValidationContractAndLanguageIndependence(bool empty, bool codes, string locale)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);

        using var response = await SendAsync(browser, empty, codes ? "codes-v1" : null, locale: locale);

        await PhotoValidationAssertions.RefusalAsync(response, empty, codes);
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, media, account.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("CODES-V1")]
    [InlineData("codes-v1, codes-v1")]
    [InlineData("codes-v1, unknown")]
    [InlineData("codes-v1\tother")]
    [InlineData("\u00a0codes-v1")]
    public async Task UnrecognizedSelectorsKeepTheExactLegacyForBothFields(string selector)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        foreach (var empty in new[] { true, false })
        {
            using var response = await SendAsync(browser, empty, selector);
            await PhotoValidationAssertions.RefusalAsync(response, empty, codes: false);
        }
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, media, account.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryInvalidKeyKeepsItsMeaningAndCreatesNothing(bool codes)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        foreach (var key in new[] { null, "", new string('a', 65), "a b", "a/b", "chave-é" })
        {
            using var response = await SendAsync(browser, empty: false, codes ? "codes-v1" : null, key: key);
            await PhotoValidationAssertions.RefusalAsync(response, empty: false, codes);
        }
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, media, account.UserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyBodyWinsOverAnInvalidKeyAndOnlyTheValueAndProtocolHeadersChange(bool empty)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        using var legacy = await SendAsync(browser, empty, selector: null);
        using var coded = await SendAsync(browser, empty, "codes-v1");
        var before = await PhotoValidationAssertions.RefusalAsync(legacy, empty, codes: false);
        var after = await PhotoValidationAssertions.RefusalAsync(coded, empty, codes: true);
        before.Remove("traceId");
        after.Remove("traceId");
        after["errors"] = before["errors"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Equal(legacy.Content.Headers.ContentType?.ToString(), coded.Content.Headers.ContentType?.ToString());
        Assert.Equal(legacy.Headers.CacheControl?.ToString(), coded.Headers.CacheControl?.ToString());
        Assert.Equal(legacy.Headers.Pragma.ToString(), coded.Headers.Pragma.ToString());
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, media, account.UserId);
    }

    [Theory]
    [InlineData("Accept-Encoding")]
    [InlineData("Accept-Encoding, Origin")]
    [InlineData("duora-validation-format")]
    [InlineData("Accept-Encoding, DUORA-VALIDATION-FORMAT")]
    [InlineData("*")]
    [InlineData("*, Accept-Encoding")]
    public async Task VaryPreservesExistingTokensWithoutDuplicatingTheSelectorOrWildcard(string initial)
    {
        await using var factory = new DuoraWebFactory(_connectionString)
        {
            TestServices = services => services.AddSingleton<IStartupFilter>(new SeedVary(initial)),
        };
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        foreach (var codes in new[] { false, true })
        {
            using var response = await SendAsync(browser, empty: true, codes ? "codes-v1" : null);
            await PhotoValidationAssertions.RefusalAsync(response, empty: true, codes);
            var expected = initial.Split(", ");
            if (!expected.Contains("*") && !expected.Contains(PhotoValidationAssertions.Header, StringComparer.OrdinalIgnoreCase))
            {
                expected = [.. expected, PhotoValidationAssertions.Header];
            }
            Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), response.Headers.Vary.Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task SelectionIsIndependentAcrossAlternatingAndConcurrentRequests()
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        for (var i = 0; i < 4; i++)
        {
            var codes = i % 2 == 1;
            using var response = await SendAsync(browser, empty: true, codes ? "codes-v1" : null);
            await PhotoValidationAssertions.RefusalAsync(response, empty: true, codes);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var codes = i % 2 == 1;
            var empty = i % 3 == 0;
            using var response = await SendAsync(browser, empty, codes ? "codes-v1" : null);
            await PhotoValidationAssertions.RefusalAsync(response, empty, codes);
        }));
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, media, account.UserId);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public async Task ExistingAsciiKeyBoundariesAreAcceptedWithoutFormatConfirmation(int length, bool codes)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        var key = length == 1 ? "a" : new string('a', 60) + "A0_-";
        using var response = await SendAsync(browser, empty: false, codes ? "codes-v1" : null, key: key);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        PhotoValidationAssertions.NoProtocolHeaders(response);
        var upload = Assert.Single(await media.UploadsOfAsync(account.UserId));
        Assert.Equal(key, upload.ClientKey);
        Assert.Single(await media.ProcessingEventsAsync(upload.Id));
    }

    [Fact]
    public async Task ReplayingAcrossFormatsHasOneEffectAndOtherBytesRemainAConflict()
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        var key = MediaScenario.NewKey();
        using var first = await SendAsync(browser, empty: false, selector: null, key: key);
        using var replay = await SendAsync(browser, empty: false, "codes-v1", key: key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var conflict = await SendContentAsync(browser, MediaScenario.JpegLike(fill: 0x77), key, "codes-v1");
        await ProblemAssert.IsProblemAsync(conflict, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        foreach (var response in new[] { first, replay, conflict })
        {
            PhotoValidationAssertions.NoProtocolHeaders(response);
        }
        var upload = Assert.Single(await media.UploadsOfAsync(account.UserId));
        Assert.Single(await media.ProcessingEventsAsync(upload.Id));
        Assert.Equal(1, media.StoredObjectCount());
    }

    [Theory]
    [InlineData("type", 415, "UNSUPPORTED_MEDIA_TYPE")]
    [InlineData("length", 411, "LENGTH_REQUIRED")]
    [InlineData("large", 413, "PAYLOAD_TOO_LARGE")]
    [InlineData("anonymous", 401, "UNAUTHENTICATED")]
    [InlineData("csrf", 403, "CSRF_VALIDATION_FAILED")]
    [InlineData("restricted", 403, "ACCOUNT_RESTRICTED")]
    [InlineData("disabled", 503, "MEDIA_UPLOADS_DISABLED")]
    public async Task OptInDoesNotChangeOtherRefusalsOrBypassExistingBarriers(string scenario, int status, string code)
    {
        await using var factory = new DuoraWebFactory(_connectionString)
        {
            Settings = scenario == "disabled"
                ? new Dictionary<string, string?> { ["Media:Review"] = null, ["Media:Storage:Provider"] = null }
                : new Dictionary<string, string?>(),
        };
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = scenario == "anonymous" ? new Browser(factory) : await media.Identity.SignedInBrowserAsync(account);
        if (scenario == "restricted")
        {
            await media.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, "Restricted");
        }
        HttpContent content = scenario == "length"
            ? new StreamContent(new ForwardOnlyStream(MediaScenario.JpegLike()))
            : new ByteArrayContent(scenario == "large" ? MediaScenario.JpegLike((5 * 1024 * 1024) + 1) : scenario == "type" ? [] : MediaScenario.JpegLike());
        content.Headers.ContentType = new MediaTypeHeaderValue(scenario == "type" ? "text/html" : "image/jpeg");
        using var response = await browser.PostContentAsync(MediaScenario.PhotosPath, content,
            new MutationHeaders(SendCsrfToken: scenario != "csrf", Extra: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = scenario is "type" or "length" or "large" ? "bad key" : MediaScenario.NewKey(),
                [PhotoValidationAssertions.Header] = "codes-v1",
            }));
        await ProblemAssert.IsProblemAsync(response, (HttpStatusCode)status, code);
        PhotoValidationAssertions.NoProtocolHeaders(response);
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, media, account.UserId);
    }

    [Fact]
    public async Task OptInNeitherChangesRateLimitsNorAppliesToOtherRoutes()
    {
        await using var factory = new DuoraWebFactory(_connectionString)
        {
            Settings = new Dictionary<string, string?>
            {
                ["RateLimiting:PhotoUploadPerAccount:PermitLimit"] = "1",
                ["RateLimiting:PhotoUploadPerAccount:Window"] = "01:00:00",
            },
        };
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        using var accepted = await SendAsync(browser, empty: false, "codes-v1", key: MediaScenario.NewKey());
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var limited = await SendAsync(browser, empty: false, "codes-v1", key: MediaScenario.NewKey());
        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        PhotoValidationAssertions.NoProtocolHeaders(limited);
        using var other = await browser.PostJsonAsync("/api/v1/auth/login", new { email = "", password = "" },
            new MutationHeaders(Extra: new Dictionary<string, string> { [PhotoValidationAssertions.Header] = "codes-v1" }));
        await ProblemAssert.IsProblemAsync(other, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        PhotoValidationAssertions.NoProtocolHeaders(other);
        Assert.Single(await media.UploadsOfAsync(account.UserId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInterruptedStreamKeepsItsOwnProblemAndCompensation(bool codes)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = MediaScenario.NewKey() };
        if (codes)
        {
            headers[PhotoValidationAssertions.Header] = "codes-v1";
        }
        using var response = await browser.PostContentAsync(MediaScenario.PhotosPath,
            new TruncatedPhotoContent(MediaScenario.JpegLike(1000), 4000), new MutationHeaders(Extra: headers));
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "UPLOAD_INCOMPLETE");
        PhotoValidationAssertions.NoProtocolHeaders(response);
        Assert.Empty(await media.UploadsOfAsync(account.UserId));
        Assert.Equal(0, media.StoredObjectCount());
    }

    private static Task<HttpResponseMessage> SendAsync(Browser browser, bool empty, string? selector, string? key = "bad key", string locale = "en-US") =>
        SendContentAsync(browser, empty ? [] : MediaScenario.JpegLike(), key, selector, locale);

    private static Task<HttpResponseMessage> SendContentAsync(Browser browser, byte[] bytes, string? key, string? selector, string locale = "en-US")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var headers = new Dictionary<string, string> { ["Accept-Language"] = locale };
        if (key is not null)
        {
            headers["Idempotency-Key"] = key;
        }
        if (selector is not null)
        {
            headers[PhotoValidationAssertions.Header] = selector;
        }
        return browser.PostContentAsync(MediaScenario.PhotosPath, content, new MutationHeaders(Extra: headers));
    }

    private sealed class SeedVary(string value) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path == MediaScenario.PhotosPath)
                {
                    context.Response.Headers.Vary = value;
                }
                await continuation(context);
            });
            next(app);
        };
    }

    private sealed class TruncatedPhotoContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly long _declaredLength;

        public TruncatedPhotoContent(byte[] bytes, long declaredLength)
        {
            _bytes = bytes;
            _declaredLength = declaredLength;
            Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, TestContext.Current.CancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
        }
    }
}

internal static class PhotoValidationAssertions
{
    public const string Header = "Duora-Validation-Format";

    public static async Task<JsonObject> RefusalAsync(HttpResponseMessage response, bool empty, bool codes)
    {
        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("urn:duora:problem:validation-failed", (string?)body["type"]);
        Assert.False(body.ContainsKey("detail"));
        var errors = body["errors"]!.AsObject();
        var field = empty ? "body" : "Idempotency-Key";
        Assert.Equal([field], errors.Select(p => p.Key));
        Assert.Single(errors[field]!.AsArray());
        var expected = codes
            ? empty ? "PHOTO_BODY_EMPTY" : "IDEMPOTENCY_KEY_INVALID"
            : empty ? "A foto está vazia." : "Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.";
        Assert.Equal(expected, (string?)errors[field]![0]);
        Assert.Equal(codes ? ["codes-v1"] : [], response.Headers.TryGetValues(Header, out var values) ? values : []);
        Assert.True(response.Headers.Vary.Contains("*") || response.Headers.Vary.Count(v => string.Equals(v, Header, StringComparison.OrdinalIgnoreCase)) == 1);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).Name);
        Assert.False(response.Headers.Contains("Retry-After"));
        return body;
    }

    public static void NoProtocolHeaders(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains(Header));
        Assert.DoesNotContain(response.Headers.Vary, v => string.Equals(v, Header, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task NoEffectsAsync(string connectionString, MediaScenario media, Guid ownerId)
    {
        Assert.Empty(await media.UploadsOfAsync(ownerId));
        Assert.Equal(0, media.StoredObjectCount());
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            select (select count(*) from media.uploads)
                 + (select count(*) from media.upload_guards)
                 + (select count(*) from ops.outbox_events where type = 'media.upload-quarantined')
                 + (select count(*) from ops.outbox_deliveries where consumer = 'media-processing')
            """, connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
