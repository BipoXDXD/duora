namespace Duora.Core.Identity;

/// <summary>
/// Política de uma sessão, decidida no login pelos papéis da conta (spec 4.3; auth.md): conta privilegiada abre
/// sessão administrativa, mais curta. Gravada pelo nome em <c>login_sessions.policy</c>.
/// </summary>
public enum SessionPolicy
{
    Normal,
    Administrative,
}

/// <summary>Instantes em que uma sessão deixa de valer por inatividade e por idade.</summary>
public sealed record SessionWindow(DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

/// <summary>
/// Limites de uma sessão de login (spec 4.3): a inatividade desliza a cada uso, mas nunca passa do limite absoluto
/// contado a partir do login. O deslize é feito no banco, no mesmo comando que valida a sessão
/// (<c>least(agora + Idle, absoluto)</c>), para não haver janela entre ler e renovar.
/// </summary>
public sealed record SessionLifetime
{
    public SessionLifetime(TimeSpan idle, TimeSpan absolute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idle, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(absolute, idle);

        Idle = idle;
        Absolute = absolute;
    }

    /// <summary>Sessão normal de participante: 30 minutos ociosa, 12 horas absoluta.</summary>
    public static SessionLifetime Participant { get; } = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12));

    /// <summary>Sessão administrativa (contas privilegiadas): 15 minutos ociosa, 8 horas absoluta.</summary>
    public static SessionLifetime Administrative { get; } = new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(8));

    public TimeSpan Idle { get; }

    public TimeSpan Absolute { get; }

    public static SessionLifetime For(SessionPolicy policy) => policy switch
    {
        SessionPolicy.Normal => Participant,
        SessionPolicy.Administrative => Administrative,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de sessão desconhecida."),
    };

    public SessionWindow Open(DateTimeOffset now) => new(now + Idle, now + Absolute);
}
