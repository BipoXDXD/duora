using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

/// <summary>Sem <c>Media:Review</c>, o upload fica desligado e falha fechado (ADR-0025), como na produção atual.</summary>
public sealed class PhotoUploadsDisabledTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString)
    {
        Settings = new Dictionary<string, string?> { ["Media:Review"] = null, ["Media:Storage:Provider"] = null },
    };

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("codes-v1")]
    public async Task UploadsAnswer503AndStoreNothing(string? selector)
    {
        var media = new MediaScenario(postgres, _factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var browser = await media.Identity.SignedInBrowserAsync(account);

        using var response = await MediaScenario.UploadAsync(browser, [], validationFormat: selector);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "MEDIA_UPLOADS_DISABLED");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        Assert.Empty(await media.UploadsOfAsync(account.UserId));
        Assert.Equal(0, media.StoredObjectCount());
    }
}
