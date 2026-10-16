using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Duora.Core.Media;
using Microsoft.Extensions.Logging;

namespace Duora.Infrastructure.Media;

/// <summary>Conexão com o bucket de mídia (ADR-0025). Uma credencial por processo: Web e Worker têm políticas diferentes.</summary>
internal sealed record S3StorageOptions
{
    /// <summary>Endpoint do fornecedor compatível com S3; vazio usa o endpoint da AWS da região.</summary>
    public string? ServiceUrl { get; init; }

    public string Region { get; init; } = "";

    public string Bucket { get; init; } = "";

    public string AccessKeyId { get; init; } = "";

    public string SecretAccessKey { get; init; } = "";

    /// <summary>Teto de cada chamada ao storage (spec 16.4: toda chamada externa tem timeout).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Só o dublê de teste fala HTTP; fora dele, o endpoint tem de ser https.</summary>
    public bool AllowInsecureHttp { get; init; }

    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(Region)
        && !string.IsNullOrWhiteSpace(Bucket)
        && !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && RequestTimeout > TimeSpan.Zero
        && (string.IsNullOrWhiteSpace(ServiceUrl) || HasAllowedScheme(ServiceUrl));

    /// <summary>Para log e mensagem de assert: só onde fica o bucket, nunca a chave de acesso nem o segredo.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ServiceUrl = ").Append(ServiceUrl)
            .Append(", Region = ").Append(Region)
            .Append(", Bucket = ").Append(Bucket)
            .Append(", RequestTimeout = ").Append(RequestTimeout);
        return true;
    }

    private bool HasAllowedScheme(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp));
}

/// <summary>
/// Adaptador S3 sobre o SDK oficial, que assina as requisições (nada de SigV4 à mão). O bucket é privado: nenhuma
/// ACL pública, nenhuma URL assinada. Retry fica com o SDK, com teto baixo; um PUT de fluxo não pesquisável não é
/// repetido por ele, e quem decide repetir é a fila.
/// </summary>
internal sealed partial class S3ObjectStore(S3StorageOptions options, ILogger<S3ObjectStore> logger) : IObjectStore, IDisposable
{
    private const int MaxSdkRetries = 2;

    /// <summary>Erros que dizem "credencial ou política do bucket errada", não "objeto ausente".</summary>
    private static readonly string[] CredentialErrors = ["AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch"];

    private readonly IAmazonS3 _client = CreateClient(options);

    public static IAmazonS3 CreateClient(S3StorageOptions options)
    {
        var config = new AmazonS3Config
        {
            Timeout = options.RequestTimeout,
            MaxErrorRetry = MaxSdkRetries,
            // Checksum só quando a operação exige: fornecedores compatíveis nem sempre aceitam o trailer padrão do
            // SDK v4 em upload de fluxo não pesquisável.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
            config.ForcePathStyle = true;
        }

        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
    {
        await using var exact = new ExactLengthStream(content, length);
        var request = new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key.Value,
            InputStream = exact,
            ContentType = contentType,
            AutoCloseStream = false,
        };
        request.Headers.ContentLength = length;
        try
        {
            await _client.PutObjectAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsCredentialError(exception))
        {
            LogCredentialError(logger, "PutObject", key.Area, exception.ErrorCode);
            throw;
        }
    }

    public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(options.Bucket, key.Value, cancellationToken);
            return new ResponseOwningStream(response);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            return null;
        }
        catch (AmazonS3Exception exception) when (IsCredentialError(exception))
        {
            LogCredentialError(logger, "GetObject", key.Area, exception.ErrorCode);
            throw;
        }
    }

    public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(options.Bucket, key.Value, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            // Alguns fornecedores respondem 404 em vez de 204: o objeto já não existe, que é o estado pedido.
        }
        catch (AmazonS3Exception exception) when (IsCredentialError(exception))
        {
            LogCredentialError(logger, "DeleteObject", key.Area, exception.ErrorCode);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Só "objeto ausente"; bucket ausente é configuração errada e precisa subir como erro.</summary>
    private static bool IsMissingObject(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode == "NoSuchKey";

    private static bool IsCredentialError(AmazonS3Exception exception) => CredentialErrors.Contains(exception.ErrorCode);

    /// <summary>Sem a chave do objeto (TM-42): a área e o código bastam para achar a política errada.</summary>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Storage de mídia recusou {Operation} na área {Area} ({ErrorCode}): credencial ou política do bucket")]
    private static partial void LogCredentialError(ILogger logger, string operation, StorageArea area, string errorCode);

    /// <summary>Fecha a resposta HTTP junto com o fluxo do corpo.</summary>
    private sealed class ResponseOwningStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _body = response.ResponseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => response.ContentLength;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _body.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _body.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _body.ReadAsync(buffer, offset, count, cancellationToken);

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
                _body.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
