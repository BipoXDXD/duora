using System.Text.Json;
using Amazon.SecurityToken.Model;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

public sealed record SelfieCaptureCredentials(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTime Expiration);
public sealed record SelfieCaptureSession(string SessionId, string Region, SelfieCaptureCredentials Credentials);

/// <summary>Papel que o web assume para a credencial temporária do navegador (só StartFaceLivenessSession).</summary>
public sealed class SelfieCaptureOptions
{
    public string CaptureRoleArn { get; set; } = "";

    public bool IsValid() => CaptureRoleArn.StartsWith("arn:aws:iam::", StringComparison.Ordinal);
}

public sealed class SelfieCaptureCredentialService
{
    private readonly SelfieVerificationService _verifications;
    private readonly IOptions<SelfieProviderOptions> _options;
    private readonly IOptions<SelfieCaptureOptions> _capture;
    private readonly SelfieAwsClients _clients;

    internal SelfieCaptureCredentialService(SelfieVerificationService verifications, IOptions<SelfieProviderOptions> options,
        IOptions<SelfieCaptureOptions> capture, SelfieAwsClients clients)
    {
        _verifications = verifications;
        _options = options;
        _capture = capture;
        _clients = clients;
    }

    public async Task<SelfieCaptureSession?> CreateAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled || await _verifications.CaptureReferenceAsync(userId, id, cancellationToken) is not { } reference)
        {
            return null;
        }

        using var client = _clients.CreateSecurityTokenService();
        var policy = JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[] { new { Effect = "Allow", Action = "rekognition:StartFaceLivenessSession", Resource = "*" } },
        });
        var response = await client.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = _capture.Value.CaptureRoleArn,
            RoleSessionName = "selfie-" + id.ToString("N"),
            DurationSeconds = 900,
            Policy = policy,
        }, cancellationToken);
        var credentials = response.Credentials;
        return new(reference, _options.Value.Region,
            new(credentials.AccessKeyId, credentials.SecretAccessKey, credentials.SessionToken, credentials.Expiration!.Value));
    }
}
