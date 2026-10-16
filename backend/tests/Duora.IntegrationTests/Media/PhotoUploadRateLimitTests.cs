using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

/// <summary>Rate limit do envio de fotos por conta e por IP (spec 11.5, TM-43), com limites baixos só para o teste.</summary>
[Trait("Acceptance", "T-16")]
public sealed class PhotoUploadRateLimitTests(PostgresFixture postgres)
{
    [Fact]
    public async Task TheAccountLimitAnswers429WithRetryAfterBeforeStoringAnything()
    {
        await using var factory = Factory(perAccount: 2, perIp: 100);
        var media = new MediaScenario(postgres, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);
        for (var i = 0; i < 2; i++)
        {
            using var accepted = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: (byte)i));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var limited = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: 9));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        Assert.Equal(2, (await media.UploadsOfAsync(account.UserId)).Count);
    }

    [Fact]
    public async Task TheIpLimitHoldsAcrossAccounts()
    {
        await using var factory = Factory(perAccount: 100, perIp: 2);
        var media = new MediaScenario(postgres, factory);
        var first = await media.Identity.CreateConfirmedAccountAsync();
        var second = await media.Identity.CreateConfirmedAccountAsync();
        using var firstBrowser = await media.Identity.SignedInBrowserAsync(first);
        using var secondBrowser = await media.Identity.SignedInBrowserAsync(second);
        for (var i = 0; i < 2; i++)
        {
            using var accepted = await MediaScenario.UploadAsync(firstBrowser, MediaScenario.JpegLike(fill: (byte)i));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var limited = await MediaScenario.UploadAsync(secondBrowser, MediaScenario.JpegLike());

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        Assert.Empty(await media.UploadsOfAsync(second.UserId));
    }

    private DuoraWebFactory Factory(int perAccount, int perIp) => new(postgres.ConnectionString)
    {
        Settings = new Dictionary<string, string?>
        {
            ["RateLimiting:PhotoUploadPerAccount:PermitLimit"] = perAccount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimiting:PhotoUploadPerAccount:Window"] = "01:00:00",
            ["RateLimiting:PhotoUploadPerIp:PermitLimit"] = perIp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimiting:PhotoUploadPerIp:Window"] = "01:00:00",
        },
    };
}
