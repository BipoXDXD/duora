using System.Net;
using System.Net.Http.Headers;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Compensação da recepção (ADR-0025, spec 16.2): corpo truncado, storage falhando e reserva perdida. Nos três casos a
/// reserva <c>Pending</c> some e nenhum objeto sobra no storage.
/// </summary>
[Trait("Acceptance", "T-16")]
public sealed class PhotoUploadCompensationTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly DirectoryInfo _storeRoot = Directory.CreateTempSubdirectory("duora-media-faulty-");
    private readonly FaultyObjectStore _store;
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public PhotoUploadCompensationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _store = new FaultyObjectStore(new FileSystemObjectStore(_storeRoot.FullName), postgres.ConnectionString);
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.AddSingleton<IObjectStore>(_store),
        };
        _media = new MediaScenario(postgres, _factory);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _storeRoot.Delete(recursive: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codes-v1")]
    public async Task ATruncatedBodyIs400AndLeavesNoReservationNorObject(string? selector)
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        var content = new TruncatedContent(MediaScenario.JpegLike(1000), declaredLength: 4000);

        using var response = await MediaScenario.UploadContentAsync(browser, content, MediaScenario.NewKey(), selector);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "UPLOAD_INCOMPLETE");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        await AssertNothingLeftAsync(account.UserId);
    }

    [Fact]
    public async Task AFailingStorageIs503WithRetryAfterAndLeavesNoReservationNorObject()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        _store.Fault = Fault.FailAfterWriting;

        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE");
        ProblemAssert.RetryAfterSeconds(response);
        await AssertNothingLeftAsync(account.UserId);
    }

    [Fact]
    public async Task ALostReservationIs503WithRetryAfterAndLeavesNoObject()
    {
        // Reenvio depois do prazo reaproveitou a reserva enquanto este envio ainda gravava: a promoção afeta 0 linhas.
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        _store.Fault = Fault.DeleteReservationWhileWriting;

        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE");
        ProblemAssert.RetryAfterSeconds(response);
        await AssertNothingLeftAsync(account.UserId);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExistsIsAMissingOwner()
    {
        var result = await ReceiveDirectlyAsync(Guid.NewGuid());

        Assert.IsType<PhotoUploadResult.OwnerMissing>(result);
        Assert.Equal(0, StoredObjects());
    }

    [Fact]
    public async Task ARestrictionAppliedAfterTheEdgeCheckIsSeenUnderTheGuard()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        await _media.ExecuteAsync("update identity.users set status = 'Restricted' where id = $1", account.UserId);

        var result = await ReceiveDirectlyAsync(account.UserId);

        Assert.IsType<PhotoUploadResult.AccountRestricted>(result);
        await AssertNothingLeftAsync(account.UserId);
    }

    private async Task<PhotoUploadResult> ReceiveDirectlyAsync(Guid ownerId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<PhotoUploads>();
        Assert.True(IdempotencyKey.TryParse(MediaScenario.NewKey(), out var key));
        var bytes = MediaScenario.JpegLike();
        await using var body = new MemoryStream(bytes);
        return await uploads.ReceiveAsync(
            new PhotoUploadCommand(ownerId, key, PhotoFormat.Jpeg, bytes.Length),
            body,
            TestContext.Current.CancellationToken);
    }

    private async Task AssertNothingLeftAsync(Guid ownerId)
    {
        Assert.Empty(await _media.UploadsOfAsync(ownerId));
        Assert.Equal(0, StoredObjects());
    }

    private int StoredObjects() => _storeRoot.EnumerateFiles("*", SearchOption.AllDirectories).Count();

    private enum Fault
    {
        None,
        FailAfterWriting,
        DeleteReservationWhileWriting,
    }

    /// <summary>Storage de verdade (filesystem) com falhas injetadas depois de gravar, para a compensação ter o que apagar.</summary>
    private sealed class FaultyObjectStore(IObjectStore inner, string connectionString) : IObjectStore
    {
        public Fault Fault { get; set; }

        public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            await inner.PutAsync(key, content, length, contentType, cancellationToken);
            switch (Fault)
            {
                case Fault.FailAfterWriting:
                    throw new HttpRequestException("Storage indisponível (falha injetada).");
                case Fault.DeleteReservationWhileWriting:
                    await using (var connection = new NpgsqlConnection(connectionString))
                    {
                        await connection.OpenAsync(cancellationToken);
                        await using var command = new NpgsqlCommand(
                            "delete from media.uploads where quarantine_key = $1 and state = 'Pending'",
                            connection);
                        command.Parameters.AddWithValue(key.Value);
                        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
                    }

                    break;
                case Fault.None:
                    break;
            }
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }

    /// <summary>Declara mais bytes do que manda: o cliente que cai no meio do upload.</summary>
    private sealed class TruncatedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly long _declaredLength;

        public TruncatedContent(byte[] bytes, long declaredLength)
        {
            _bytes = bytes;
            _declaredLength = declaredLength;
            Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            Headers.ContentLength = declaredLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
        }
    }
}
