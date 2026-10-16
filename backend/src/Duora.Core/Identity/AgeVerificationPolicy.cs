namespace Duora.Core.Identity;

/// <summary>
/// Política etária versionada (spec 12.2). A versão vai para cada solicitação (TM-37). A validade é configurável
/// enquanto a leitura do ECA Digital, art. 9º § 1º ("a cada acesso"), está pendente (issue #9, item 5; ADR-0024).
/// <c>RequestLifetime</c> é quanto tempo uma solicitação pendente sem resultado continua aberta; depois dela a pessoa
/// pode pedir outra.
/// </summary>
public sealed record AgeVerificationPolicy
{
    public const int MaxVersionLength = 32;

    public AgeVerificationPolicy(string version, TimeSpan validity, TimeSpan requestLifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > MaxVersionLength || !version.All(IsVersionCharacter))
        {
            throw new ArgumentException("Versão de política fora do formato (letras, dígitos, '.', '-', '_').", nameof(version));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validity, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestLifetime, TimeSpan.Zero);

        Version = version;
        Validity = validity;
        RequestLifetime = requestLifetime;
    }

    public string Version { get; }

    /// <summary>Quanto tempo uma verificação adulta vale a partir da decisão.</summary>
    public TimeSpan Validity { get; }

    public TimeSpan RequestLifetime { get; }

    private static bool IsVersionCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';
}
