using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Media;

[Trait("Acceptance", "T-16")]
public sealed class UploadValidationFormatTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DuoraWebFactory _factory = null!;
    private MediaScenario _media = null!;
    private string _connectionString = "";

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        _factory = new DuoraWebFactory(_connectionString)
        {
            TestServices = services => services.AddSingleton<IStartupFilter, ExistingVaryFilter>(),
        };
        _media = new MediaScenario(_connectionString, _factory);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task OnlyTheExactSelectorChangesTheTwoFieldValuesInEitherLanguage()
    {
        using var browser = await _media.Identity.SignedInBrowserAsync(await _media.Identity.CreateConfirmedAccountAsync());
        string?[] selectors = [null, "", "other", "CODES-V1", "codes-v1,codes-v1", "codes-v1, other", "codes-v1"];
        string?[] keys = [null, "", new string('a', 65), "bad key", "../x", "é"];
        foreach (var locale in new[] { "pt-BR", "en-US" })
        {
            foreach (var selector in selectors)
            {
                using var empty = await SendAsync(browser, [], null, selector, locale);
                await AssertValidationAsync(empty, "body", selector == "codes-v1" ? "PHOTO_BODY_EMPTY" : "A foto está vazia.", selector == "codes-v1");
                foreach (var key in keys)
                {
                    using var invalid = await SendAsync(browser, MediaScenario.JpegLike(), key, selector, locale);
                    await AssertValidationAsync(invalid, "Idempotency-Key", selector == "codes-v1" ? "IDEMPOTENCY_KEY_INVALID" : "Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.", selector == "codes-v1");
                }
            }
        }
        await AssertNoEffectsAsync();
    }

    [Fact]
    public async Task AlternatingAndConcurrentRequestsKeepTheirOwnFormat()
    {
        using var browser = await _media.Identity.SignedInBrowserAsync(await _media.Identity.CreateConfirmedAccountAsync());
        foreach (var concurrent in new[] { false, true })
        {
            async Task CheckAsync(int index)
            {
                var codes = index % 2 == 0;
                using var response = await SendAsync(browser, [], null, codes ? "codes-v1" : null, "en-US");
                await AssertValidationAsync(response, "body", codes ? "PHOTO_BODY_EMPTY" : "A foto está vazia.", codes);
            }
            if (concurrent)
            {
                await Task.WhenAll(Enumerable.Range(0, 12).Select(CheckAsync));
            }
            else
            {
                foreach (var index in Enumerable.Range(0, 12))
                {
                    await CheckAsync(index);
                }
            }
        }
        await AssertNoEffectsAsync();
    }

    [Fact]
    public async Task ExplicitFormatDoesNotBypassAuthentication()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        using var response = await SendAsync(browser, [], null, "codes-v1", "en-US");
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        await AssertNoEffectsAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codes-v1")]
    public async Task NegotiationPreservesPrecedenceAndDoesNotSignalOtherRefusals(string? selector)
    {
        using var browser = await _media.Identity.SignedInBrowserAsync(await _media.Identity.CreateConfirmedAccountAsync());
        using var unsupported = await SendAsync(browser, [], null, selector, "en-US", "text/html");
        await ProblemAssert.IsProblemAsync(unsupported, HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE");
        Assert.False(unsupported.Headers.Contains("Duora-Validation-Format"));
        Assert.DoesNotContain("Duora-Validation-Format", unsupported.Headers.Vary);
        using var missingCsrf = await SendAsync(browser, [], null, selector, "en-US", sendCsrf: false);
        await ProblemAssert.IsProblemAsync(missingCsrf, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.False(missingCsrf.Headers.Contains("Duora-Validation-Format"));
        await AssertNoEffectsAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codes-v1")]
    public async Task ValidKeyBoundariesAndIdempotencyRetainTheirMeaning(string? selector)
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        foreach (var key in new[] { "_", new string('a', 62) + "-_" })
        {
            using var accepted = await SendAsync(browser, MediaScenario.JpegLike(), key, selector, "en-US");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.False(accepted.Headers.Contains("Duora-Validation-Format"));
            using var repeated = await SendAsync(browser, MediaScenario.JpegLike(), key, selector, "en-US");
            Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
            Assert.Equal(await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await repeated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var reused = await SendAsync(browser, MediaScenario.JpegLike(fill: 5), key, selector, "en-US");
            await ProblemAssert.IsProblemAsync(reused, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
            Assert.False(reused.Headers.Contains("Duora-Validation-Format"));
        }
        var uploads = await _media.UploadsOfAsync(account.UserId);
        Assert.Equal(2, uploads.Count);
        Assert.Equal(2, _media.StoredObjectCount());
        foreach (var upload in uploads)
        {
            Assert.Single(await _media.ProcessingEventsAsync(upload.Id));
        }
    }

    [Theory]
    [InlineData("Accept-Encoding")]
    [InlineData("*")]
    [InlineData("duora-validation-format")]
    [InlineData("Accept-Encoding, DUORA-VALIDATION-FORMAT")]
    public async Task BothFormatsPreserveExistingVaryWithoutDuplicates(string existing)
    {
        using var browser = await _media.Identity.SignedInBrowserAsync(await _media.Identity.CreateConfirmedAccountAsync());
        foreach (var selector in new string?[] { null, "codes-v1" })
        {
            using var response = await SendAsync(browser, [], null, selector, "en-US", existingVary: existing);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            var values = response.Headers.Vary.ToArray();
            var expected = existing == "*" ? new[] { "*" } : existing.Split(',').Select(value => value.Trim()).ToArray();
            if (!expected.Contains("*") && !expected.Contains("Duora-Validation-Format", StringComparer.OrdinalIgnoreCase))
            {
                expected = [.. expected, "Duora-Validation-Format"];
            }
            Assert.Equal(expected, values);
            Assert.Equal(selector is not null, response.Headers.Contains("Duora-Validation-Format"));
        }
        await AssertNoEffectsAsync();
    }

    private static Task<HttpResponseMessage> SendAsync(Browser browser, byte[] bytes, string? key, string? selector, string locale, string contentType = "image/jpeg", bool sendCsrf = true, string? existingVary = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var extra = new Dictionary<string, string> { ["Accept-Language"] = locale };
        if (existingVary is not null)
        {
            extra["X-Synthetic-Vary"] = existingVary;
        }
        if (key is not null)
        {
            extra["Idempotency-Key"] = key;
        }
        if (selector is not null)
        {
            extra["Duora-Validation-Format"] = selector;
        }
        return browser.PostContentAsync(MediaScenario.PhotosPath, content, new MutationHeaders(SendCsrfToken: sendCsrf, Extra: extra));
    }

    internal static async Task<JsonObject> AssertValidationAsync(HttpResponseMessage response, string field, string value, bool codes)
    {
        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("The request contains invalid fields.", (string?)body["title"]);
        Assert.Equal("urn:duora:problem:validation-failed", (string?)body["type"]);
        Assert.False(body.ContainsKey("detail"));
        var errors = body["errors"]!.AsObject();
        Assert.Equal([field], errors.Select(pair => pair.Key));
        Assert.Equal(value, Assert.Single(errors[field]!.AsArray())!.GetValue<string>());
        Assert.Equal(codes, response.Headers.Contains("Duora-Validation-Format"));
        if (codes)
        {
            Assert.Equal("codes-v1", Assert.Single(response.Headers.GetValues("Duora-Validation-Format")));
        }
        Assert.Contains("Duora-Validation-Format", response.Headers.Vary);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).Name);
        Assert.False(response.Headers.Contains("Retry-After"));
        return body;
    }

    private sealed class ExistingVaryFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.TryGetValue("X-Synthetic-Vary", out var vary))
                {
                    context.Response.Headers.Vary = vary;
                }
                await following(context);
            });
            next(app);
        };
    }

    private async Task AssertNoEffectsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            select (select count(*) from media.uploads)
                 + (select count(*) from media.upload_guards)
                 + (select count(*) from ops.outbox_events where type = 'media.upload-quarantined')
                 + (select count(*) from ops.outbox_deliveries where consumer = 'media-processing')
            """, connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, _media.StoredObjectCount());
    }
}
