using System.Diagnostics.Metrics;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Pedidos de suspensão por selfie que esgotaram o teto de falhas e ficaram mortos (<c>Failed</c> na outbox, para
/// revisão). Sem rótulo de conta ou tentativa: o id vai só no log, e só o da entrega.
/// </summary>
internal sealed class SelfieSuspensionRequestMetrics
{
    public const string MeterName = "Duora.Identity";
    public const string DeadInstrument = "duora.identity.selfie_suspension_requests.dead";

    private readonly Counter<long> _dead;

    public SelfieSuspensionRequestMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _dead = meters.Create(MeterName).CreateCounter<long>(DeadInstrument,
            description: "Pedidos de suspensão por selfie encerrados sem suspender depois do teto de falhas.");
    }

    public void RecordDead() => _dead.Add(1);
}
