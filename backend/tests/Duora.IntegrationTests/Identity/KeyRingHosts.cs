using System.Net;
using Duora.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Hosts web recriados sobre o mesmo banco, cada um com o key ring e a configuração que o teste der (T-15): o
/// diretório compartilhado entre as fábricas faz o papel do volume nomeado.
/// </summary>
internal sealed class KeyRingHosts(PostgresFixture postgres, Diag455Recorder? diagnostics = null)
{
    public const string SessionCookie = "__Host-duora";
    public const string MePath = "/api/v1/me";

    public DuoraWebFactory HostOn(DirectoryInfo keyRing, IReadOnlyDictionary<string, string?>? settings = null)
    {
        if (diagnostics is null)
        {
            return new(postgres.ConnectionString)
            {
                SharedKeyRing = keyRing,
                Settings = settings ?? new Dictionary<string, string?>(),
            };
        }

        var label = diagnostics.NextHostLabel();
        var logs = diagnostics.Provider(label);
        return new(postgres.ConnectionString, logs)
        {
            SharedKeyRing = keyRing,
            Settings = settings ?? new Dictionary<string, string?>(),
            TestServices = services =>
            {
                WithNpgsqlLogs(services);
                services.AddSingleton<IStartupFilter>(new Diag455ResponseProbe(diagnostics, label));
            },
        };
    }

    /// <summary>
    /// Diagnóstico da #455: o mesmo pool da composição de produção (mesma connection string, MaxPoolSize e Timeout),
    /// só que construído com o logger factory do host, para os eventos do Npgsql chegarem ao provider do diagnóstico.
    /// </summary>
    private static void WithNpgsqlLogs(IServiceCollection services)
    {
        // Só a conexão em Trace (fases da abertura física); comandos e transações ficam de fora para o log caber.
        services.AddLogging(logging => logging
            .AddFilter<Diag455LoggerProvider>("Npgsql", LogLevel.Warning)
            .AddFilter<Diag455LoggerProvider>("Npgsql.Connection", LogLevel.Trace));
        services.AddSingleton(provider =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                MaxPoolSize = database.MaxPoolSize,
                Timeout = (int)database.PoolTimeout.TotalSeconds,
            };
            return new NpgsqlDataSourceBuilder(connection.ConnectionString)
                .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                .Build();
        });
    }

    public async Task<string> SignInOnAFreshHostAsync(
        DirectoryInfo keyRing,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        await using var host = HostOn(keyRing, settings);
        return await SignInAsync(host);
    }

    public async Task<string> SignInAsync(DuoraWebFactory host)
    {
        var scenario = new IdentityScenario(postgres, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        return browser.Cookie(SessionCookie) ?? throw new InvalidOperationException("Login sem cookie de sessão.");
    }

    public static Browser BrowserWithCookie(DuoraWebFactory host, string cookie)
    {
        var browser = new Browser(host);
        browser.Cookies.Add(Browser.BaseAddress, new Cookie(SessionCookie, cookie));
        return browser;
    }
}
