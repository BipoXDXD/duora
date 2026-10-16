namespace Duora.Core.Identity;

/// <summary>
/// Política etária versionada (spec 12.2). A versão vai para cada solicitação (TM-37). A verificação adulta não vence:
/// acontece uma vez, no cadastro (decisão do dono, 2026-10-16; ADR-0024). <c>RequestLifetime</c> é quanto tempo uma
/// solicitação pendente sem resultado continua aberta; depois dela a pessoa pode pedir outra.
/// </summary>
public sealed record AgeVerificationPolicy
{
    public const int MaxVersionLength = 32;

    public AgeVerificationPolicy(string version, TimeSpan requestLifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > MaxVersionLength || !version.All(IsVersionCharacter))
        {
            throw new ArgumentException("Versão de política fora do formato (letras, dígitos, '.', '-', '_').", nameof(version));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestLifetime, TimeSpan.Zero);

        Version = version;
        RequestLifetime = requestLifetime;
    }

    public string Version { get; }

    public TimeSpan RequestLifetime { get; }

    private static bool IsVersionCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';
}
