using System.Security.Cryptography;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Corpo do upload a caminho do storage: confere o tamanho declarado, calcula o SHA-256 no caminho (sem guardar o
/// arquivo em memória) e registra se a falha veio do cliente (corpo curto, longo ou conexão caída) e não do storage.
/// </summary>
internal sealed class UploadBodyStream : Stream
{
    private readonly ExactLengthStream _body;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _sha256;

    public UploadBodyStream(Stream source, long length)
    {
        _body = new ExactLengthStream(source, length);
    }

    /// <summary>Verdadeiro quando a leitura do corpo falhou; distingue erro do cliente de erro do storage.</summary>
    public bool ClientFailed { get; private set; }

    /// <summary>Verdadeiro quando o tamanho declarado inteiro já passou.</summary>
    public bool IsComplete => _body.Position == _body.Length;

    /// <summary>Hash dos bytes lidos; só existe depois que o tamanho declarado inteiro passou.</summary>
    public byte[] Sha256 => _sha256 ?? throw new InvalidOperationException("O corpo ainda não foi lido até o fim.");

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _body.Length;

    public override long Position
    {
        get => _body.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return Hash(buffer, _body.Read(buffer));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            ClientFailed = true;
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await _body.ReadAsync(buffer, cancellationToken);
            return Hash(buffer.Span, read);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            // O IOException cobre o corpo truncado (EndOfStreamException) e a conexão caída ou o corpo acima do teto
            // do Kestrel (BadHttpRequestException herda dele); quem decide a resposta é o chamador.
            ClientFailed = true;
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            _body.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Fecha o hash ao chegar no tamanho declarado, sem esperar a leitura final de 0 bytes: há cliente de storage que
    /// para de ler quando completa o <c>Content-Length</c>.
    /// </summary>
    private int Hash(ReadOnlySpan<byte> buffer, int read)
    {
        if (read > 0)
        {
            _hash.AppendData(buffer[..read]);
        }

        if (_sha256 is null && _body.Position == _body.Length)
        {
            _sha256 = _hash.GetHashAndReset();
        }

        return read;
    }
}
