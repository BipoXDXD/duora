namespace Duora.Infrastructure.Email;

/// <summary>
/// Consumidor dos fatos de promoção da lista de espera (seção <c>Notifications:WaitlistPromotion</c>, ADR-0056).
/// Desligado por padrão em todos os ambientes: ligar é decisão do dono, depois de aprovar canal e textos.
/// </summary>
public sealed class WaitlistPromotionNoticeOptions
{
    public const string Section = "Notifications:WaitlistPromotion";

    public bool Enabled { get; set; }

    /// <summary>
    /// Marca d'água: fatos gravados antes deste instante nunca são lidos. Obrigatória para ligar, e deve ser o
    /// instante da implantação, para que ligar o consumidor não dispare avisos de promoções antigas.
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }

    public int BatchSize { get; set; } = 20;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid() =>
        BatchSize is >= 1 and <= 100
        && PollInterval > TimeSpan.Zero
        && (!Enabled || NotBefore is not null);
}
