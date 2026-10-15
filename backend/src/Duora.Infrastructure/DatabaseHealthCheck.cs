using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Duora.Infrastructure;

internal sealed class DatabaseHealthCheck(DatabaseProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = await probe.CheckAsync(cancellationToken);
        return status.IsReachable
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, exception: status.Failure);
    }
}
