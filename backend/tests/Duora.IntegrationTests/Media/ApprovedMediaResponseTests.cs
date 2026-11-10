using Duora.Infrastructure.Media;
using Duora.Web.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// A resposta de API-M04 sem servidor HTTP: o corpo vai em fatias de 64 KiB, para o Kestrel não segurar uma segunda
/// cópia inteira do derivado no pipe, e o prazo da reserva corta a cópia de um cliente que parou de ler.
/// </summary>
public sealed class ApprovedMediaResponseTests
{
    private const int Chunk = 64 * 1024;

    [Fact]
    public async Task ABufferedDerivativeIsWrittenInChunksOfAtMost64KiB()
    {
        var bytes = Enumerable.Range(0, 5 * 1024 * 1024).Select(i => (byte)i).ToArray();
        var body = new RecordingBody();
        var http = new DefaultHttpContext();
        http.Response.Body = body;

        await new MediaAssetEndpoints.ApprovedMediaResult(new AuthorizedMedia(new MemoryStream(bytes))).ExecuteAsync(http);

        Assert.Equal(bytes, body.ToArray());
        Assert.InRange(body.LargestWrite, 1, Chunk);
        Assert.True(body.Flushes >= bytes.Length / Chunk, $"flushes={body.Flushes}");
    }

    /// <summary>
    /// O cliente lê a primeira fatia e para. Vencidos os 60 s da reserva, a escrita parada recebe o cancelamento, a
    /// reserva já voltou ao orçamento e a conexão é abortada. O <c>TestServer</c> não reproduz a janela do Kestrel,
    /// então o cliente parado é este corpo de resposta.
    /// </summary>
    [Fact]
    public async Task TheReservationDeadlineCancelsAStalledCopyAndFreesTheBudget()
    {
        var time = new FakeTimeProvider();
        var bytes = new byte[2 * Chunk];
        var budget = new OwnerMediaReadBudget(bytes.Length);
        var reservation = budget.TryReserve(Guid.NewGuid(), bytes.Length);
        Assert.NotNull(reservation);
        var media = new AuthorizedMedia(new MemoryStream(bytes), reservation, TimeSpan.FromSeconds(60), time);
        var body = new StalledBody(time, () => budget.TryReserve(Guid.NewGuid(), bytes.Length));
        var lifetime = new RecordingLifetime();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(new StartedResponse(body));
        http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        http.Response.Body = body;

        await new MediaAssetEndpoints.ApprovedMediaResult(media).ExecuteAsync(http);

        Assert.True(body.CopyCancelled, "A escrita parada não recebeu o cancelamento do prazo.");
        Assert.True(body.BudgetFreedAtDeadline, "A reserva não voltou quando o prazo venceu.");
        Assert.True(lifetime.Aborted);
    }

    private sealed class RecordingBody : MemoryStream
    {
        internal int LargestWrite { get; private set; }

        internal int Flushes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            LargestWrite = Math.Max(LargestWrite, count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return base.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Aceita a primeira fatia e trava na segunda. O relógio anda 60 s ali; uma escrita cujo token não cancelou falha
    /// na hora em vez de travar o teste.
    /// </summary>
    private sealed class StalledBody(FakeTimeProvider clock, Func<IDisposable?> tryReserve) : MemoryStream
    {
        internal bool Started { get; private set; }

        internal bool CopyCancelled { get; private set; }

        internal bool BudgetFreedAtDeadline { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!Started)
            {
                Started = true;
                await base.WriteAsync(buffer, cancellationToken);
                return;
            }

            clock.Advance(TimeSpan.FromSeconds(60));
            using (var reservation = tryReserve())
            {
                BudgetFreedAtDeadline = reservation is not null;
            }

            CopyCancelled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("O cliente parou de ler.");
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StartedResponse(StalledBody body) : HttpResponseFeature
    {
        public override bool HasStarted => body.Started;
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = CancellationToken.None;

        internal bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }
}
