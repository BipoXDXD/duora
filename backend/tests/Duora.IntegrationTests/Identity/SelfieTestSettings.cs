namespace Duora.IntegrationTests.Identity;

/// <summary>Configuração sintética da selfie ligada: privacidade aprovada, papel e chaves fictícias.</summary>
internal static class SelfieTestSettings
{
    public static Dictionary<string, string?> Enabled(string policyVersion = "test") => new()
    {
        ["SelfieVerification:Enabled"] = "true",
        ["SelfieVerification:PrivacyApproved"] = "true",
        ["SelfieVerification:PolicyVersion"] = policyVersion,
        ["SelfieVerification:CaptureRoleArn"] = "arn:aws:iam::000000000000:role/synthetic",
        ["SelfieVerification:AwsAccessKeyId"] = "SYNTHETICACCESSKEYID",
        ["SelfieVerification:AwsSecretAccessKey"] = "synthetic-secret-value",
    };
}
