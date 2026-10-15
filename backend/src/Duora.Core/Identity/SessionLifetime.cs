namespace Duora.Core.Identity;

/// <summary>Instantes em que uma sessão deixa de valer por inatividade e por idade.</summary>
public sealed record SessionWindow(DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

/// <summary>
/// Limites de uma sessão de login (spec 4.3): a inatividade desliza a cada uso, mas nunca passa do limite absoluto
/// contado a partir do login.
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

    public TimeSpan Idle { get; }

    public TimeSpan Absolute { get; }

    public SessionWindow Open(DateTimeOffset now) => new(now + Idle, now + Absolute);

    public SessionWindow Touch(SessionWindow window, DateTimeOffset now) =>
        window with { IdleExpiresAt = Min(now + Idle, window.AbsoluteExpiresAt) };

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
