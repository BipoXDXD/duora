using System.Diagnostics.Metrics;

namespace Duora.Infrastructure.Identity;

/// <summary>Closed operation/outcome labels only; never user, subject, email, URL or token values.</summary>
public sealed class ExternalAuthenticationMetrics : IDisposable
{
    private readonly Meter _meter = new("Duora.ExternalAuthentication");
    private readonly Counter<long> _outcomes;
    public ExternalAuthenticationMetrics() => _outcomes = _meter.CreateCounter<long>("duora.external_authentication.outcomes");
    public void Record(string operation, string outcome) => _outcomes.Add(1,
        new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));
    public void Dispose() => _meter.Dispose();
}
