namespace Duora.Contracts.Identity;

public sealed record GoogleAuthenticationRequest(string? Purpose = null);
public sealed record GoogleAuthenticationOptions(bool Enabled, bool RegistrationEnabled, bool LinkingEnabled);
public sealed record GoogleAuthorizationResponse(string AuthorizationUrl);
public sealed record GoogleFlowResponse(string NextStep, DateTime ExpiresAt, string? Email = null);
public sealed record GoogleCompletionRequest(string? TwoFactorCode = null, string? RecoveryCode = null);
public sealed record GoogleRegistrationConfirmation(string? Code, string? Password);
public sealed record ExternalLoginResponse(string Provider, DateTime LinkedAt);
public sealed record ExternalLoginsResponse(IReadOnlyList<ExternalLoginResponse> Items);

public sealed record ConfirmExternalLoginRequest();
