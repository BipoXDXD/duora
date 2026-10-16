using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Seção <c>Media</c> (ADR-0025). Sem <see cref="Review"/>, o upload fica desligado e falha fechado: nem o storage é
/// exigido. Com <c>TechnicalOnly</c>, o boot recusa <c>Production</c>.
/// </summary>
internal sealed class MediaOptions
{
    public const string Section = "Media";

    public MediaReviewPolicy? Review { get; set; }

    /// <summary>Teto de uploads sem veredito por conta (spec 11.5); hipótese inicial a medir.</summary>
    public int MaxPendingUploadsPerAccount { get; set; } = PendingUploadQuota.DefaultCap;

    /// <summary>Tempo máximo para receber o corpo de um upload (spec 16.1: limitar tamanho e tempo).</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Taxa mínima de recepção do corpo do upload, aplicada na rota (Kestrel corta quem fica abaixo depois do período
    /// de tolerância). Contra cliente lento segurando conexão e reserva (spec 16.1).
    /// </summary>
    public double MinUploadBytesPerSecond { get; set; } = 1024;

    public TimeSpan MinUploadRateGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    public bool UploadsEnabled => Review is not null;

    /// <summary>
    /// Reserva <c>Pending</c> mais velha que isso é de um envio que já estourou o tempo (ou de um Web que caiu): não
    /// ocupa vaga e a mesma chave pode ser reenviada.
    /// </summary>
    public TimeSpan AbandonedReservationAfter => UploadTimeout * 2;

    public PendingUploadQuota Quota => new(MaxPendingUploadsPerAccount);
}
