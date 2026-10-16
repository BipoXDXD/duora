using System.Diagnostics.Metrics;
using Duora.Infrastructure.Identity;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>Conta as medições de <c>webhook_event_conflict</c> enquanto vive (escuta o meter real da aplicação).</summary>
internal sealed class WebhookConflictCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _count;

    public WebhookConflictCounter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgeVerificationWebhook.MeterName && instrument.Name == AgeVerificationWebhook.EventConflictMetric)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
        _listener.Start();
    }

    public long Count => Interlocked.Read(ref _count);

    public void Dispose() => _listener.Dispose();
}
