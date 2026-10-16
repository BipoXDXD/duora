using System.Diagnostics.Metrics;

namespace Duora.Infrastructure.Operations;

/// <summary>Métricas dos webhooks de fornecedor, pelo <see cref="IMeterFactory"/> do host (sem estado estático).</summary>
internal sealed class WebhookMetrics
{
    public const string MeterName = "Duora.Webhooks";
    public const string EventConflict = "webhook_event_conflict";

    private readonly Counter<long> _eventConflicts;

    public WebhookMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _eventConflicts = meter.CreateCounter<long>(EventConflict, description: "Evento repetido pelo fornecedor com outro corpo.");
    }

    public void RecordEventConflict(string provider) =>
        _eventConflicts.Add(1, new KeyValuePair<string, object?>("provider", provider));
}
