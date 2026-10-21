using System.Net;
using System.Net.Http.Headers;
using Duora.IntegrationTests.Http;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

public sealed partial class KestrelPhotoUploadTests
{
    public static TheoryData<string[], bool> ValidationSelectors => new()
    {
        { [], false },
        { ["codes-v1"], true },
        { [" codes-v1 "], true },
        { ["\tcodes-v1\t"], true },
        { ["CODES-V1"], false },
        { [""], false },
        { ["unknown"], false },
        { ["codes-v1", "codes-v1"], false },
        { ["codes-v1", "unknown"], false },
        { ["codes-v1, codes-v1"], false },
        { ["codes-v1,unknown"], false },
    };

    [Theory]
    [MemberData(nameof(ValidationSelectors))]
    public async Task FormatSelectionAndConfirmationSurviveRealHttp(string[] selectors, bool codes)
    {
        var session = await SignInAsync();
        foreach (var empty in new[] { true, false })
        {
            foreach (var locale in new[] { "pt-BR", "en-US" })
            {
                using var request = Upload(empty ? [] : MediaScenario.JpegLike());
                if (!empty)
                {
                    request.Headers.Remove("Idempotency-Key");
                    request.Headers.Add("Idempotency-Key", "bad key");
                }
                request.Headers.Add("Accept-Language", locale);
                if (selectors.Length != 0)
                {
                    request.Headers.TryAddWithoutValidation("duora-validation-format", selectors);
                }
                using var response = await session.SendAsync(request);
                var body = await PhotoValidationAssertions.RefusalAsync(response, empty, codes);
                await ProblemSchemaAssertions.MatchesUploadRefusalAsync(_kestrel, response, body);
            }
        }
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, new MediaScenario(_connectionString, _kestrel), session.UserId);
    }

    [Theory]
    [InlineData("type", 415, "UNSUPPORTED_MEDIA_TYPE")]
    [InlineData("missing", 411, "LENGTH_REQUIRED")]
    [InlineData("large", 413, "PAYLOAD_TOO_LARGE")]
    public async Task OptInKeepsHeaderPrecedenceAndLengthsOnRealHttp(string scenario, int status, string code)
    {
        var session = await SignInAsync();
        foreach (var codes in new[] { false, true })
        {
            HttpContent content = scenario == "missing"
                ? new StreamContent(new ForwardOnlyStream(MediaScenario.JpegLike()))
                : new ByteArrayContent(scenario == "large" ? MediaScenario.JpegLike((5 * 1024 * 1024) + 1) : []);
            content.Headers.ContentType = new MediaTypeHeaderValue(scenario == "type" ? "text/html" : "image/jpeg");
            using var request = Upload(content);
            request.Headers.ExpectContinue = true;
            request.Headers.Remove("Idempotency-Key");
            request.Headers.Add("Idempotency-Key", "bad key");
            if (codes)
            {
                request.Headers.Add(PhotoValidationAssertions.Header, "codes-v1");
            }
            using var response = await session.SendAsync(request);
            await ProblemAssert.IsProblemAsync(response, (HttpStatusCode)status, code);
            PhotoValidationAssertions.NoProtocolHeaders(response);
        }
        await PhotoValidationAssertions.NoEffectsAsync(_connectionString, new MediaScenario(_connectionString, _kestrel), session.UserId);
    }

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(5 * 1024 * 1024)]
    public async Task OptInKeepsTheAcceptedBodySizesWithoutClaimingAValidationFormat(int size)
    {
        var session = await SignInAsync();
        using var request = Upload(MediaScenario.JpegLike(size));
        request.Headers.Add(PhotoValidationAssertions.Header, "codes-v1");
        using var response = await session.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        PhotoValidationAssertions.NoProtocolHeaders(response);
    }
}
