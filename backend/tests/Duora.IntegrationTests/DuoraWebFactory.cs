using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests;

/// <summary>
/// Sobe o Duora.Web em memória apontando para o banco indicado. Ambiente "Testing" explícito para não
/// carregar user-secrets nem appsettings.Development.json da máquina.
/// </summary>
public sealed class DuoraWebFactory(string connectionString, ILoggerProvider? logs = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Duora", connectionString);
        if (logs is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }
}
