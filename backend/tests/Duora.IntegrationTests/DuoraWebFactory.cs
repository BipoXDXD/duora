using System.Globalization;
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
/// diretório temporário apagado no fim, a menos que receba um <see cref="SharedKeyRing"/>.
/// </summary>
public sealed class DuoraWebFactory(string connectionString, ILoggerProvider? logs = null)
    : WebApplicationFactory<Program>
{
    /// <summary>Mesma origem do cliente de teste (https://localhost), como a SPA servida pelo próprio domínio.</summary>
    public const string PublicOrigin = "https://localhost";

    public string Origin { get; init; } = PublicOrigin;

    public string? ContentRoot { get; init; }

    private DirectoryInfo? _ownKeyRing;

    /// <summary>
    /// Key ring passado pelo teste, que fica responsável por apagá-lo. Duas fábricas com o mesmo diretório simulam o
    /// container recriado sobre o mesmo volume (T-15). Nulo: diretório temporário próprio, apagado no fim.
    /// </summary>
    public DirectoryInfo? SharedKeyRing { get; init; }

    private DirectoryInfo KeyRing => SharedKeyRing ?? (_ownKeyRing ??= Directory.CreateTempSubdirectory("duora-keys-"));
    private readonly DirectoryInfo _media = Directory.CreateTempSubdirectory("duora-media-");

    /// <summary>Relógio da aplicação; nulo usa o do sistema.</summary>
    public TimeProvider? Time { get; init; }

    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>Troca pontual de serviços (contadores, interceptadores); nunca de banco ou de regra.</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    /// <summary>
    /// Configuração extra, aplicada por último (vence user-secrets em Development) e também sobre os padrões de mídia
    /// abaixo; um valor <c>null</c> apaga o padrão.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>Diretório do object storage de teste (adaptador de filesystem), apagado no fim.</summary>
    public string MediaRoot => _media.FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (ContentRoot is not null)
        {
            builder.UseContentRoot(ContentRoot);
        }
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("ConnectionStrings:Duora", connectionString);
        builder.UseSetting("Duora:PublicOrigin", Origin);
        builder.UseSetting("DataProtection:KeysDirectory", KeyRing.FullName);
        builder.UseSetting("DataProtection:RequireMountedVolume", "false");
        // Sem certificado por padrão (como em Development); o teste que exige o certificado o passa em Settings.
        // Upload ligado com storage em diretório temporário e aprovação técnica (dados sintéticos, ADR-0025).
        var settings = new Dictionary<string, string?>
        {
            ["DataProtection:RequireCertificate"] = "false",
            ["Media:Review"] = "TechnicalOnly",
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = _media.FullName,
            // O banco compartilhado acumula uploads de todas as classes de teste; o teto global (somando contas) só
            // vale onde o teste o configura num banco próprio (UploadQueueLimitTests).
            ["Media:MaxQueuedUploads"] = int.MaxValue.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in Settings)
        {
            settings[key] = value;
        }

        // UseSetting chega à configuração antes do registro de serviços; o ConfigureAppConfiguration abaixo, não. O
        // certificado do key ring é lido no registro (o Data Protection só o aceita ali).
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(settings)
            {
                ["ConnectionStrings:Duora"] = connectionString,
                ["Duora:PublicOrigin"] = Origin,
                ["DataProtection:KeysDirectory"] = KeyRing.FullName,
                ["DataProtection:RequireMountedVolume"] = "false",
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
        _ownKeyRing?.Delete(recursive: true);
        _media.Delete(recursive: true);
    }
}
