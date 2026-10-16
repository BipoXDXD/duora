using System.Reflection;
using Duora.Web.Identity;

namespace Duora.Web;

/// <summary>
/// Ajustes para a geração do contrato no build (ADR-0018, docs/api/README.md). O
/// Microsoft.Extensions.ApiDescription.Server executa o Program dentro do processo GetDocument.Insider, com um
/// servidor falso que não atende requisições, e o host passa pela validação de start.
/// </summary>
internal static class BuildTimeOpenApi
{
    private const string GeneratorAssemblyName = "GetDocument.Insider";

    // Só satisfaz o ValidateOnStart: o servidor falso nunca atende requisição, então nenhuma conexão é aberta.
    // Registrar os mesmos serviços de produção mantém o documento igual ao que a app expõe em runtime.
    private const string PlaceholderConnectionString = "Host=build-time-openapi.invalid";

    /// <summary>
    /// Ambiente próprio da geração: sem ele o host sobe como Production, que recusa os atalhos abaixo (key ring sem
    /// volume). Não é Development, para não ler appsettings.Development.json nem user-secrets da máquina de build.
    /// </summary>
    public const string EnvironmentName = DuoraEnvironments.BuildTimeOpenApi;

    private static bool IsGenerating => Assembly.GetEntryAssembly()?.GetName().Name == GeneratorAssemblyName;

    public static WebApplicationOptions BuilderOptions(string[] args) =>
        IsGenerating ? new WebApplicationOptions { Args = args, EnvironmentName = EnvironmentName } : new() { Args = args };

    public static void UsePlaceholderConfigurationWhenGenerating(ConfigurationManager configuration)
    {
        if (!IsGenerating)
        {
            return;
        }

        // A origem e o diretório do key ring também só passam pela validação: nenhuma chave é criada, porque o
        // servidor falso não emite cookie nem token. O build roda fora do container, sem volume para o key ring.
        configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("ConnectionStrings:Duora", PlaceholderConnectionString),
            new KeyValuePair<string, string?>("Duora:PublicOrigin", "https://build-time-openapi.invalid"),
            new KeyValuePair<string, string?>(
                "DataProtection:KeysDirectory",
                Path.Combine(Path.GetTempPath(), "duora-build-time-openapi-keys")),
            new KeyValuePair<string, string?>("DataProtection:RequireMountedVolume", "false"),
        ]);
    }
}
