namespace Duora.Core.Identity;

/// <summary>
/// Janela de autenticação recente (step-up, auth.md): operações sensíveis exigem que a sessão tenha provado a
/// credencial, ou o segundo fator, há no máximo <see cref="Window"/>. A decisão é sempre do servidor.
/// </summary>
public static class RecentAuthentication
{
    /// <summary>
    /// 1 hora, decisão de segurança do dono em 28/10/2026 (antes 5 minutos). Vale para senha recente e TOTP recente.
    /// A janela passa da inatividade das sessões (15 min administrativa, 30 min normal): a prova só dura a hora
    /// inteira enquanto a sessão estiver viva; sessão expirada encerra a prova junto.
    /// </summary>
    public static TimeSpan Window { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Intervalo fechado [prova, prova + janela] (decisão do dono em 28/10/2026): a prova feita há exatamente
    /// <see cref="Window"/> ainda vale; um tick depois, não. Prova no futuro não vale.
    /// </summary>
    public static bool IsRecent(DateTimeOffset? provedAt, DateTimeOffset now) =>
        provedAt is { } at && at <= now && now <= at + Window;
}
