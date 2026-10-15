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

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    public ExponentialBackoff Backoff => new(InitialRetryDelay, MaxRetryDelay);

    /// <summary>
    /// O lease precisa durar mais que o envio: senão outro worker retoma a entrega enquanto o primeiro ainda
    /// envia, e todo envio lento vira duplicado.
    /// </summary>
    public bool IsConsistent() =>
        BatchSize is >= 1 and <= 100
        && MaxAttempts >= 1
        && SendTimeout > TimeSpan.Zero
        && LeaseDuration > SendTimeout
        && PollInterval > TimeSpan.Zero
        && InitialRetryDelay > TimeSpan.Zero
        && MaxRetryDelay >= InitialRetryDelay;
}
