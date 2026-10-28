using System.Text.Json;
using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

public sealed record SelfieCaptureCredentials(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTime Expiration);
public sealed record SelfieCaptureSession(string SessionId, string Region, SelfieCaptureCredentials Credentials);

public sealed class SelfieCaptureCredentialService(SelfieVerificationService verifications, IOptions<SelfieProviderOptions> options,
    IOptions<SelfieAwsCredentialOptions> backendCredentials)
{
    public async Task<SelfieCaptureSession?> CreateAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || await verifications.CaptureReferenceAsync(userId, id, cancellationToken) is not { } reference)
        {
            return null;
        }

        using var client = new AmazonSecurityTokenServiceClient(backendCredentials.Value.ToAwsCredentials(),
            RegionEndpoint.GetBySystemName(options.Value.Region));
        var policy = JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[] { new { Effect = "Allow", Action = "rekognition:StartFaceLivenessSession", Resource = "*" } },
        });
        var response = await client.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = options.Value.CaptureRoleArn,
            RoleSessionName = "selfie-" + id.ToString("N"),
            DurationSeconds = 900,
            Policy = policy,
        }, cancellationToken);
        var credentials = response.Credentials;
        return new(reference, options.Value.Region,
            new(credentials.AccessKeyId, credentials.SecretAccessKey, credentials.SessionToken, credentials.Expiration!.Value));
    }
}
