using System.Net;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Falhas do streaming do upload (ADR-0025, spec 16.2) com o relógio injetado, sem espera real: o prazo do envio
/// (<c>Media:UploadTimeout</c>) corre no <see cref="FakeTimeProvider"/>. Quem foi lento decide a resposta: cliente
/// (corpo curto, longo, caído ou fora do prazo) dá 400 <c>UPLOAD_INCOMPLETE</c>; storage dá 503. Nos dois casos
/// a reserva e o objeto parcial somem. O reenvio da mesma chave de um upload que já passou do streaming só relê o
/// corpo para o hash e nunca mexe no primeiro envio.
/// </summary>
[Trait("Acceptance", "T-16")]
public sealed class PhotoUploadStreamingFailureTests : IAsyncDisposable
{
    private const int PartialBytes = 100;

    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _time =
        new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;
    private readonly ScriptedStore _store;

    public PhotoUploadStreamingFailureTests(PostgresFixture postgres)
    {
        _store = new ScriptedStore(() => MediaStore);
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs)
        {
            Time = _time,
            Settings = new Dictionary<string, string?> { ["Media:UploadTimeout"] = "00:00:30" },
            TestServices = services => services.AddSingleton<IObjectStore>(_store),
        };
        _media = new MediaScenario(postgres, _factory);
    }

    private IObjectStore MediaStore => _media.Store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan Guard => TimeSpan.FromSeconds(30);

    private TimeSpan UploadTimeout =>
        _factory.Services.GetRequiredService<IOptions<MediaOptions>>().Value.UploadTimeout;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AnUploadWithoutFaultsGoesThroughTheSameStoreAndIsAccepted()
    {
        var owner = await _media.Identity.CreateConfirmedAccountAsync();
        var bytes = MediaScenario.JpegLike();
        _store.Script = Script.None;

        var result = await ReceiveAsync(
            owner.UserId, MediaScenario.NewKey(), new MemoryStream(bytes), bytes.Length, Ct);

        Assert.Equal(UploadState.Quarantined, Assert.IsType<PhotoUploadResult.Accepted>(result).State);
        Assert.Equal(1, _media.StoredObjectCount());
    }

    [Fact]
    public async Task AnUploadThatOutlastsTheTimeoutIs400UploadIncompleteAndLeavesNothing()
    {
        var owner = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(owner);
        _store.Script = new Script(WholeBody: false, Wait.Timeout, Failure: null);

        var sending = MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        await _store.Waiting.Task.WaitAsync(Guard, Ct);
        _time.Advance(UploadTimeout);
        using var response = await sending.WaitAsync(Guard, Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "UPLOAD_INCOMPLETE");
        Assert.Empty(await _media.UploadsOfAsync(owner.UserId));
        Assert.Equal(0, _media.StoredObjectCount());
    }

    [Fact]
    public async Task TheUploadDeadlineIsExactlyTheConfiguredTimeoutOnTheInjectedClock()
    {
        var owner = await _media.Identity.CreateConfirmedAccountAsync();
        var bytes = MediaScenario.JpegLike();
        _store.Script = new Script(WholeBody: false, Wait.Timeout, Failure: null);

        var receiving = ReceiveAsync(owner.UserId, MediaScenario.NewKey(), new MemoryStream(bytes), bytes.Length, Ct);
        await _store.Waiting.Task.WaitAsync(Guard, Ct);
        Assert.False(_store.Token.IsCancellationRequested, "O prazo não pode ter vencido antes de o relógio andar.");
        _time.Advance(UploadTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(_store.Token.IsCancellationRequested, "Um milissegundo antes do prazo o envio ainda vale.");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(_store.Token.IsCancellationRequested, "No prazo exato o envio é cortado.");
        Assert.IsType<PhotoUploadResult.BodyIncomplete>(await receiving.WaitAsync(Guard, Ct));
    }

    [Theory]
    [InlineData("timeout-fires", true, "TaskCanceledException")]
    [InlineData("timeout-fires-and-the-store-reports-a-generic-error", true, "InvalidOperationException")]
    // Caracteriza o comportamento atual: o prazo estourado depois de o corpo inteiro ser lido ainda é atribuído ao
    // cliente (400 UPLOAD_INCOMPLETE), porque o cancelamento do prazo vale como falha do cliente. Issue #505.
    [InlineData("timeout-fires-after-the-whole-body", true, "InvalidOperationException")]
    [InlineData("request-aborts", true, "TaskCanceledException")]
    [InlineData("sdk-timeout-while-the-body-is-incomplete", true, "TimeoutException")]
    [InlineData("sdk-timeout-wrapped-while-the-body-is-incomplete", true, "HttpRequestException")]
    [InlineData("sdk-cancellation-while-the-body-is-incomplete", true, "OperationCanceledException")]
    [InlineData("sdk-timeout-after-the-whole-body", false, "TimeoutException")]
    [InlineData("sdk-timeout-wrapped-after-the-whole-body", false, "HttpRequestException")]
    [InlineData("generic-failure-while-the-body-is-incomplete", false, "InvalidOperationException")]
    [InlineData("generic-failure-after-the-whole-body", false, "HttpRequestException")]
    public async Task AFailureWhileStoringBlamesTheClientOnlyWhenTheClientWasTheSlowOne(
        string scenario, bool clientFailed, string cause)
    {
        var owner = await _media.Identity.CreateConfirmedAccountAsync();
        var bytes = MediaScenario.JpegLike();
        _store.Script = ScriptOf(scenario);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var receiving = ReceiveAsync(
            owner.UserId, MediaScenario.NewKey(), new MemoryStream(bytes), bytes.Length, abort.Token);
        if (_store.Script.Wait != Wait.None)
        {
            await _store.Waiting.Task.WaitAsync(Guard, Ct);
            if (_store.Script.Wait == Wait.Timeout)
            {
                _time.Advance(UploadTimeout);
            }
            else
            {
                await abort.CancelAsync();
            }
        }

        var result = await receiving.WaitAsync(Guard, Ct);

        if (clientFailed)
        {
            Assert.IsType<PhotoUploadResult.BodyIncomplete>(result);
        }
        else
        {
            Assert.IsType<PhotoUploadResult.StorageFailed>(result);
        }

        Assert.NotNull(_store.Thrown);
        var warning = Assert.Single(
            _logs.Entries,
            entry => entry.Category == typeof(PhotoUploads).FullName && entry.Level == LogLevel.Warning);
        Assert.Equal(clientFailed, warning.Fields["ClientFailed"]);
        Assert.Equal(cause, warning.Fields["Cause"]);
        var key = Assert.IsType<ObjectKey>(_store.Key).Value;
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(key, StringComparison.Ordinal));
        Assert.Empty(await _media.UploadsOfAsync(owner.UserId));
        Assert.Equal(0, _media.StoredObjectCount());
    }

    [Fact]
    public async Task AReplayWithATruncatedBodyIs400AndTheFirstUploadIsUntouched()
    {
        var owner = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(owner);
        var bytes = MediaScenario.JpegLike();
        var key = MediaScenario.NewKey();
        _store.Script = Script.None;
        using var first = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var photoId = Guid.Parse((string)(await MediaScenario.ReadObjectAsync(first))["photoId"]!);
        var before = await SnapshotAsync(owner.UserId, photoId, bytes);

        using var truncated = await MediaScenario.UploadContentAsync(
            browser, new TruncatedContent(bytes[..500], declaredLength: bytes.Length), key);

        await ProblemAssert.IsProblemAsync(truncated, HttpStatusCode.BadRequest, "UPLOAD_INCOMPLETE");
        Assert.Equal(before, await SnapshotAsync(owner.UserId, photoId, bytes));
        using var again = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(photoId, Guid.Parse((string)(await MediaScenario.ReadObjectAsync(again))["photoId"]!));
        Assert.Equal(before, await SnapshotAsync(owner.UserId, photoId, bytes));
    }

    [Theory]
    [InlineData("connection-drops")]
    [InlineData("shorter-than-declared")]
    [InlineData("longer-than-declared")]
    [InlineData("request-aborts")]
    [InlineData("timeout-fires")]
    public async Task AReplayThatCannotReadItsBodyIsBodyIncompleteAndTheFirstUploadIsUntouched(string failure)
    {
        var owner = await _media.Identity.CreateConfirmedAccountAsync();
        var bytes = MediaScenario.JpegLike();
        var key = MediaScenario.NewKey();
        _store.Script = Script.None;
        var accepted = Assert.IsType<PhotoUploadResult.Accepted>(
            await ReceiveAsync(owner.UserId, key, new MemoryStream(bytes), bytes.Length, Ct));
        var before = await SnapshotAsync(owner.UserId, accepted.PhotoId, bytes);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var body = new ReplayBody(failure, bytes, abort);

        var replaying = ReceiveAsync(owner.UserId, key, body, bytes.Length, abort.Token);
        if (failure == "timeout-fires")
        {
            await body.Reading.Task.WaitAsync(Guard, Ct);
            Assert.False(
                body.Token.IsCancellationRequested, "O prazo não pode ter vencido antes de o relógio andar.");
            _time.Advance(UploadTimeout - TimeSpan.FromMilliseconds(1));
            Assert.False(body.Token.IsCancellationRequested, "Um milissegundo antes do prazo a releitura ainda vale.");
            _time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.True(body.Token.IsCancellationRequested, "No prazo exato a releitura é cortada.");
        }

        Assert.IsType<PhotoUploadResult.BodyIncomplete>(await replaying.WaitAsync(Guard, Ct));
        Assert.Equal(before, await SnapshotAsync(owner.UserId, accepted.PhotoId, bytes));
        var replay = Assert.IsType<PhotoUploadResult.Accepted>(
            await ReceiveAsync(owner.UserId, key, new MemoryStream(bytes), bytes.Length, Ct));
        Assert.Equal(accepted.PhotoId, replay.PhotoId);
        Assert.Equal(before, await SnapshotAsync(owner.UserId, accepted.PhotoId, bytes));
    }

    private static Script ScriptOf(string scenario) => scenario switch
    {
        "timeout-fires" => new Script(WholeBody: false, Wait.Timeout, Failure: null),
        "timeout-fires-and-the-store-reports-a-generic-error" =>
            new Script(WholeBody: false, Wait.Timeout, () => new InvalidOperationException("O storage desistiu.")),
        "timeout-fires-after-the-whole-body" =>
            new Script(WholeBody: true, Wait.Timeout, () => new InvalidOperationException("O storage desistiu.")),
        "request-aborts" => new Script(WholeBody: false, Wait.RequestAbort, Failure: null),
        "sdk-timeout-while-the-body-is-incomplete" =>
            new Script(WholeBody: false, Wait.None, () => new TimeoutException("SDK")),
        "sdk-timeout-wrapped-while-the-body-is-incomplete" =>
            new Script(WholeBody: false, Wait.None, () => new HttpRequestException("SDK", new TimeoutException())),
        "sdk-cancellation-while-the-body-is-incomplete" =>
            new Script(WholeBody: false, Wait.None, () => new OperationCanceledException("SDK")),
        "sdk-timeout-after-the-whole-body" => new Script(WholeBody: true, Wait.None, () => new TimeoutException("SDK")),
        "sdk-timeout-wrapped-after-the-whole-body" =>
            new Script(WholeBody: true, Wait.None, () => new HttpRequestException("SDK", new TimeoutException())),
        "generic-failure-while-the-body-is-incomplete" =>
            new Script(WholeBody: false, Wait.None, () => new InvalidOperationException("Storage indisponível.")),
        "generic-failure-after-the-whole-body" =>
            new Script(WholeBody: true, Wait.None, () => new HttpRequestException("Storage indisponível.")),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    private async Task<PhotoUploadResult> ReceiveAsync(
        Guid ownerId, string key, Stream body, long size, CancellationToken cancellationToken)
    {
        Assert.True(IdempotencyKey.TryParse(key, out var parsed));
        await using var scope = _factory.Services.CreateAsyncScope();
        await using var owned = body;
        return await scope.ServiceProvider.GetRequiredService<PhotoUploads>().ReceiveAsync(
            new PhotoUploadCommand(ownerId, parsed, PhotoFormat.Jpeg, size),
            owned,
            cancellationToken);
    }

    /// <summary>O primeiro envio como o banco e o storage o guardam: estado, hash, versão, eventos e bytes.</summary>
    private async Task<string> SnapshotAsync(Guid ownerId, Guid photoId, byte[] bytes)
    {
        var upload = Assert.Single(await _media.UploadsOfAsync(ownerId));
        Assert.Equal(photoId, upload.Id);
        var version = await _media.CountAsync("select version from media.uploads where id = $1", upload.Id);
        var events = (await _media.ProcessingEventsAsync(upload.Id)).Count;
        var stored = await _media.ReadQuarantineAsync(upload);
        Assert.Equal(bytes, stored);
        return string.Join(
            '|',
            upload.Id,
            upload.State,
            Convert.ToHexString(upload.ContentSha256 ?? []),
            version,
            events,
            _media.StoredObjectCount(),
            Convert.ToHexString(SHA256.HashData(stored!)));
    }

    private enum Wait
    {
        None,
        Timeout,
        RequestAbort,
    }

    /// <summary>
    /// O que o storage faz com o corpo: lê só o começo ou tudo, deixa o objeto parcial para a compensação apagar,
    /// espera o prazo ou o cancelamento da requisição e falha. Sem falha nem espera, grava de verdade.
    /// </summary>
    private sealed record Script(bool WholeBody, Wait Wait, Func<Exception>? Failure)
    {
        public static Script None { get; } = new(WholeBody: true, Wait.None, Failure: null);

        public bool IsFaultless => WholeBody && Wait == Wait.None && Failure is null;
    }

    private sealed class ScriptedStore(Func<IObjectStore> inner) : IObjectStore
    {
        public Script Script { get; set; } = Script.None;

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public Exception? Thrown { get; private set; }

        public ObjectKey? Key { get; private set; }

        public async Task PutAsync(
            ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Key = key;
            try
            {
                await RunAsync(key, content, length, contentType, cancellationToken);
            }
            catch (Exception exception)
            {
                Thrown = exception;
                throw;
            }
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner().OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner().DeleteAsync(key, cancellationToken);

        private async Task RunAsync(
            ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            if (Script.IsFaultless)
            {
                await inner().PutAsync(key, content, length, contentType, cancellationToken);
                return;
            }

            var head = new byte[PartialBytes];
            var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            await inner().PutAsync(key, new MemoryStream(head, 0, read), read, contentType, CancellationToken.None);
            if (Script.WholeBody)
            {
                await content.CopyToAsync(Stream.Null, cancellationToken);
            }

            if (Script.Wait != Wait.None)
            {
                Waiting.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (Script.Failure is not null)
                {
                    throw Script.Failure();
                }
            }

            if (Script.Failure is { } failure)
            {
                throw failure();
            }
        }
    }

    /// <summary>Corpo do reenvio que falha de um dos jeitos que o cliente ou a rede conseguem falhar.</summary>
    private sealed class ReplayBody : Stream
    {
        private const int Extra = 16;

        private readonly string _failure;
        private readonly CancellationTokenSource _abort;
        private readonly MemoryStream _bytes;

        public ReplayBody(string failure, byte[] bytes, CancellationTokenSource abort)
        {
            _failure = failure;
            _abort = abort;
            _bytes = new MemoryStream(failure switch
            {
                "shorter-than-declared" => bytes[..^Extra],
                "longer-than-declared" => [.. bytes, .. new byte[Extra]],
                _ => bytes,
            });
        }

        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_failure)
            {
                case "connection-drops":
                    throw new IOException("A conexão caiu.");
                case "request-aborts":
                    await _abort.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    return 0;
                case "timeout-fires":
                    Token = cancellationToken;
                    Reading.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                default:
                    return await _bytes.ReadAsync(buffer, cancellationToken);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bytes.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
