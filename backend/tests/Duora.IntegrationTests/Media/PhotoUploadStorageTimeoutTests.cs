using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Duora.Web.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Atribuição do estouro de <c>Media:UploadTimeout</c> na recepção (issue 505): com o corpo inteiro entregue quem
/// estava lento era o storage (503 + Retry-After); com o corpo incompleto ou com o próprio cliente abortando, o
/// envio é que falhou (400 <c>UPLOAD_INCOMPLETE</c>). O prazo corre no relógio falso: nada espera tempo real.
/// </summary>
[Trait("Acceptance", "T-16")]
public sealed class PhotoUploadStorageTimeoutTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = TestClock.StartingNow();
    private readonly StallingObjectStore _store = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public PhotoUploadStorageTimeoutTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs)
        {
            Time = _time,
            TestServices = services => services.AddSingleton<IObjectStore>(_store),
        };
        _media = new MediaScenario(postgres, _factory);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AStorageThatStallsAfterTheWholeBodyArrivedIs503WithRetryAfterAndLeavesNoReservation()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        _store.Mode = StallMode.AfterWholeBody;

        var response = MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        await ElapseUploadTimeoutAsync();
        using var result = await response;

        await ProblemAssert.IsProblemAsync(result, HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE");
        Assert.Equal(PhotoEndpoints.StorageRetryAfter.TotalSeconds, ProblemAssert.RetryAfterSeconds(result));
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Single(_store.Deleted);
        Assert.False(StreamingFailureClientFailed());
    }

    [Fact]
    public async Task AClientThatStallsBeforeTheWholeBodyArrivedIs400UploadIncomplete()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        _store.Mode = StallMode.ReadingTheBody;
        using var content = new StallingContent(MediaScenario.JpegLike(1000), declaredLength: 4000);

        var response = MediaScenario.UploadContentAsync(browser, content, MediaScenario.NewKey());
        await ElapseUploadTimeoutAsync();
        using var result = await response;

        await ProblemAssert.IsProblemAsync(result, HttpStatusCode.BadRequest, "UPLOAD_INCOMPLETE");
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Single(_store.Deleted);
        Assert.True(StreamingFailureClientFailed());
    }

    [Fact]
    public async Task AStorageThatStopsReadingBeforeTheWholeBodyAndTimesOutIsTheSendersFailure()
    {
        // O corpo não passou inteiro e o prazo acabou: o storage parou de puxar, mas o envio não completou.
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        _store.Mode = StallMode.AfterHalfTheBody;

        var result = await ReceiveDirectlyAsync(account.UserId, abortWhenStalled: null);

        Assert.IsType<PhotoUploadResult.BodyIncomplete>(result);
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Single(_store.Deleted);
        Assert.True(StreamingFailureClientFailed());
    }

    [Fact]
    public async Task ATimeoutOnAnIncompleteBodyIsTheSendersFailureEvenWhenTheStorageHidesTheCancellation()
    {
        // O SDK pode devolver o estouro como erro opaco, sem OperationCanceledException na cadeia.
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        _store.Mode = StallMode.AfterHalfTheBodyThenOpaqueError;

        var result = await ReceiveDirectlyAsync(account.UserId, abortWhenStalled: null);

        Assert.IsType<PhotoUploadResult.BodyIncomplete>(result);
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Single(_store.Deleted);
        Assert.True(StreamingFailureClientFailed());
    }

    [Fact]
    public async Task AClientThatAbortsAfterTheWholeBodyArrivedIsStillTheSendersFailure()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        _store.Mode = StallMode.AfterWholeBody;
        using var aborted = new CancellationTokenSource();

        var result = await ReceiveDirectlyAsync(account.UserId, abortWhenStalled: aborted);

        Assert.IsType<PhotoUploadResult.BodyIncomplete>(result);
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Single(_store.Deleted);
        Assert.True(StreamingFailureClientFailed());
    }

    /// <summary>O <c>ClientFailed</c> do único log de recepção desfeita: a métrica que a issue 505 escondia.</summary>
    private bool StreamingFailureClientFailed()
    {
        var entry = Assert.Single(
            _logs.Entries,
            log => log.Level == LogLevel.Warning && log.Fields.ContainsKey("ClientFailed"));
        return Assert.IsType<bool>(entry.Fields["ClientFailed"]);
    }

    private async Task ElapseUploadTimeoutAsync()
    {
        await _store.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _time.Advance(UploadTimeout() + TimeSpan.FromSeconds(1));
    }

    private TimeSpan UploadTimeout() =>
        _factory.Services.GetRequiredService<IOptions<MediaOptions>>().Value.UploadTimeout;

    /// <summary>
    /// Com <paramref name="abortWhenStalled"/> o cliente cancela a requisição quando o storage trava; sem ele, o prazo
    /// estoura no relógio falso.
    /// </summary>
    private async Task<PhotoUploadResult> ReceiveDirectlyAsync(Guid ownerId, CancellationTokenSource? abortWhenStalled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<PhotoUploads>();
        Assert.True(IdempotencyKey.TryParse(MediaScenario.NewKey(), out var key));
        var bytes = MediaScenario.JpegLike(1000);
        await using var body = new MemoryStream(bytes);
        var receiving = uploads.ReceiveAsync(
            new PhotoUploadCommand(ownerId, key, PhotoFormat.Jpeg, bytes.Length),
            body,
            abortWhenStalled?.Token ?? CancellationToken.None);
        await _store.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (abortWhenStalled is null)
        {
            _time.Advance(UploadTimeout() + TimeSpan.FromSeconds(1));
        }
        else
        {
            await abortWhenStalled.CancelAsync();
        }

        return await receiving;
    }

    private enum StallMode
    {
        AfterWholeBody,
        AfterHalfTheBody,
        AfterHalfTheBodyThenOpaqueError,
        ReadingTheBody,
    }

    /// <summary>Storage que lê o corpo até o ponto combinado e espera o cancelamento, como um storage lento.</summary>
    private sealed class StallingObjectStore : IObjectStore
    {
        private readonly ConcurrentQueue<ObjectKey> _deleted = new();

        public StallMode Mode { get; set; }

        /// <summary>Objetos que a compensação mandou apagar.</summary>
        public IReadOnlyCollection<ObjectKey> Deleted => _deleted;

        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PutAsync(
            ObjectKey key,
            Stream content,
            long length,
            string contentType,
            CancellationToken cancellationToken)
        {
            switch (Mode)
            {
                case StallMode.AfterWholeBody:
                    await content.CopyToAsync(Stream.Null, cancellationToken);
                    break;
                case StallMode.AfterHalfTheBody:
                case StallMode.AfterHalfTheBodyThenOpaqueError:
                    await content.ReadExactlyAsync(new byte[length / 2], cancellationToken);
                    break;
                case StallMode.ReadingTheBody:
                    Stalled.TrySetResult();
                    await content.CopyToAsync(Stream.Null, cancellationToken);
                    break;
            }

            Stalled.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (Mode == StallMode.AfterHalfTheBodyThenOpaqueError)
            {
                throw new HttpRequestException("Falha opaca do storage (cancelamento escondido).");
            }
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            _deleted.Enqueue(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Cliente lento: manda parte do corpo e fica parado, sem fechar nem terminar o envio.</summary>
    private sealed class StallingContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly long _declaredLength;

        public StallingContent(byte[] bytes, long declaredLength)
        {
            _bytes = bytes;
            _declaredLength = declaredLength;
            Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            Headers.ContentLength = declaredLength;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_bytes);
            await stream.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
        }
    }
}
