namespace Duora.Core.Identity;

/// <summary>
/// Janela de autenticação recente (step-up, auth.md): operações sensíveis exigem que a sessão tenha provado a
/// credencial há menos de <see cref="Window"/>. A decisão é sempre do servidor.
/// </summary>
public static class RecentAuthentication
{
    /// <summary>5 minutos (hipótese, auth.md).</summary>
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Intervalo semiaberto [prova, prova + janela); prova no futuro não vale.</summary>
    public static bool IsRecent(DateTimeOffset? provedAt, DateTimeOffset now) =>
        provedAt is { } at && at <= now && now < at + Window;
}
