using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests;

/// <summary>
/// Sobe o Duora.Web em memória apontando para o banco indicado. Ambiente "Testing" explícito para não
/// carregar user-secrets nem appsettings.Development.json da máquina. Cada fábrica tem key ring próprio, num
/// diretório temporário apagado no fim.
/// </summary>
public sealed class DuoraWebFactory(string connectionString, ILoggerProvider? logs = null)
    : WebApplicationFactory<Program>
{
    /// <summary>Mesma origem do cliente de teste (https://localhost), como a SPA servida pelo próprio domínio.</summary>
    public const string PublicOrigin = "https://localhost";

    private readonly DirectoryInfo _keyRing = Directory.CreateTempSubdirectory("duora-keys-");

    /// <summary>Relógio da aplicação; nulo usa o do sistema.</summary>
    public TimeProvider? Time { get; init; }

    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>Troca pontual de serviços (contadores, interceptadores); nunca de banco ou de regra.</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    /// <summary>Configuração extra, aplicada por último (vence user-secrets em Development).</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("ConnectionStrings:Duora", connectionString);
        builder.UseSetting("Duora:PublicOrigin", PublicOrigin);
        builder.UseSetting("DataProtection:KeysDirectory", _keyRing.FullName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(Settings)
            {
                ["ConnectionStrings:Duora"] = connectionString,
                ["Duora:PublicOrigin"] = PublicOrigin,
                ["DataProtection:KeysDirectory"] = _keyRing.FullName,
            }));

        if (Time is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(Time));
        }

        if (TestServices is not null)
        {
            builder.ConfigureTestServices(TestServices);
        }

        if (logs is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _keyRing.Delete(recursive: true);
    }
}
