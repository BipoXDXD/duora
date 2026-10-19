using Duora.Core.Jobs;

namespace Duora.Infrastructure.Email;

/// <summary>
/// Parâmetros do consumidor de e-mail (seção <c>Outbox</c>). Os padrões seguem a política inicial da spec 10.6:
/// até 8 tentativas, backoff exponencial com jitter e teto de 15 minutos.
/// </summary>
internal sealed class OutboxDispatchOptions
{
    public const string Section = "Outbox";

    public int BatchSize { get; set; } = 10;

    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// Lease do consumidor de e-mail. O lease é explícito por consumidor: a verificação etária usa o seu, em
    /// <c>AgeVerification:Dispatch:LeaseDuration</c>. Dois minutos mantêm curta a recuperação de link de
    /// confirmação e de reset quando um worker cai; envios longos não pedem lease maior porque o dispatcher o
    /// renova (heartbeat) antes de cada envio.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    public ExponentialBackoff Backoff => new(InitialRetryDelay, MaxRetryDelay);

    /// <summary>
    /// Entre dois heartbeats cabe um envio (até <see cref="SendTimeout"/>), a conclusão e o heartbeat seguinte;
    /// a outra metade do lease é a margem para conclusão, pool e banco. O tamanho do lote não entra: o heartbeat
    /// renova também as entregas do lote que ainda esperam. A divisão evita overflow com durações extremas.
    /// </summary>
    public bool HasSendLeaseBudget() =>
        SendTimeout > TimeSpan.Zero
        && SendTimeout.Ticks <= LeaseDuration.Ticks / 2;

    public bool IsConsistent() =>
        BatchSize is >= 1 and <= 100
        && MaxAttempts >= 1
        && SendTimeout > TimeSpan.Zero
        && LeaseDuration > SendTimeout
        && PollInterval > TimeSpan.Zero
        && InitialRetryDelay > TimeSpan.Zero
        && MaxRetryDelay >= InitialRetryDelay;
}
