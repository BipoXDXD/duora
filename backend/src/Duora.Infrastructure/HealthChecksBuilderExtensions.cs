using Microsoft.Extensions.DependencyInjection;

namespace Duora.Infrastructure;

public static class HealthChecksBuilderExtensions
{
    public static IHealthChecksBuilder AddDuoraDatabase(this IHealthChecksBuilder builder, params string[] tags) =>
        builder.AddCheck<DatabaseHealthCheck>("database", tags: tags);
}
