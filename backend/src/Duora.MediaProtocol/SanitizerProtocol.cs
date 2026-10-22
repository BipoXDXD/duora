using Duora.Core.Media;

namespace Duora.MediaProtocol;

public sealed record ProtocolBudget(int VariantBytes, int TotalBytes, int StderrBytes, int DeadlineMilliseconds)
{
    public static ProtocolBudget Candidate { get; } = new(16 * 1024 * 1024, 24 * 1024 * 1024, 64 * 1024, 50_000);
}

public sealed record SanitizerRequest(byte[] Nonce, PhotoFormat Format, byte[] Content, ProtocolBudget Budget);

/// <summary>Bounded one-request/one-response wire contract. No decoder, transport or activation.</summary>
public static class SanitizerProtocol
{
    public static Task<SanitizerRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public static Task WriteRequestAsync(Stream stream, SanitizerRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public static Task<SanitizeOutcome> ReadResponseAsync(Stream stream, SanitizerRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public static Task WriteResponseAsync(Stream stream, SanitizerRequest request, SanitizeOutcome outcome, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public static Task DrainStderrAsync(Stream stream, int limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
