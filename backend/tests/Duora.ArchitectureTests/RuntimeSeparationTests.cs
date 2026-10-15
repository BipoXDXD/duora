using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Duora.ArchitectureTests;

/// <summary>
/// Separação do runtime web (ADR-0019, "Revisão: separação do runtime web"): o Worker roda no runtime .NET puro,
/// então nem ele nem a Infrastructure podem usar o framework compartilhado do ASP.NET Core, e só o Duora.Web pode
/// depender do adaptador que o usa (Duora.Web.Identity). Uma referência a mais quebraria o Worker só em produção,
/// na imagem sem ASP.NET Core; estes testes a pegam no build.
/// </summary>
public sealed class RuntimeSeparationTests
{
    private const string WebIdentity = "Duora.Web.Identity";

    private static readonly string[] ProductionAssemblies =
        ["Duora.Core", "Duora.Contracts", "Duora.Infrastructure", "Duora.Web", WebIdentity, "Duora.Worker"];

    /// <summary>
    /// Assemblies do framework compartilhado Microsoft.AspNetCore.App cujo nome começa por Microsoft.AspNetCore.
    /// O framework também traz pacotes Microsoft.Extensions.*, que existem no NuGet e podem ser usados por qualquer
    /// projeto; e o Microsoft.AspNetCore.Identity.EntityFrameworkCore é pacote NuGet, fora do framework.
    /// </summary>
    private static readonly HashSet<string> AspNetCoreSharedFramework = LoadAspNetCoreSharedFramework();

    [Fact]
    public void AspNetCoreSharedFrameworkListIsNotEmpty()
    {
        // Sentinela: se a leitura do diretório do framework falhar, as regras abaixo passariam à toa.
        Assert.Contains("Microsoft.AspNetCore.Authentication.Cookies", AspNetCoreSharedFramework);
    }

    [Theory]
    [InlineData("Duora.Infrastructure")]
    [InlineData("Duora.Worker")]
    [InlineData("Duora.Core")]
    [InlineData("Duora.Contracts")]
    public void AssemblyDoesNotUseTheAspNetCoreSharedFramework(string assembly)
    {
        var references = Assembly.Load(assembly).GetReferencedAssemblies().Select(reference => reference.Name ?? "");

        Assert.DoesNotContain(references, AspNetCoreSharedFramework.Contains);
    }

    [Fact]
    public void OnlyTheWebHostReferencesTheWebIdentityAdapter()
    {
        var referrers = ProductionAssemblies
            .Where(assembly => Assembly.Load(assembly).GetReferencedAssemblies().Any(r => r.Name == WebIdentity));

        Assert.Equal(["Duora.Web"], referrers);
    }

    /// <summary>
    /// O que decide se o Worker sobe na imagem dotnet/runtime é o runtimeconfig gerado: ele lista os frameworks
    /// exigidos, inclusive os que chegam por dependência transitiva de pacote, que a lista de referências não mostra.
    /// </summary>
    [Fact]
    public void WorkerRuntimeConfigRequiresOnlyTheNetCoreRuntime()
    {
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "Duora.Worker.runtimeconfig.json");
        using var json = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        var options = json.RootElement.GetProperty("runtimeOptions");
        var frameworks = options.TryGetProperty("frameworks", out var list)
            ? list.EnumerateArray().Select(framework => framework.GetProperty("name").GetString())
            : [options.GetProperty("framework").GetProperty("name").GetString()];

        Assert.Equal(["Microsoft.NETCore.App"], frameworks);
    }

    private static HashSet<string> LoadAspNetCoreSharedFramework()
    {
        var directory = Path.GetDirectoryName(typeof(HttpContext).Assembly.Location)
            ?? throw new InvalidOperationException("Diretório do Microsoft.AspNetCore.App não encontrado.");
        return Directory.GetFiles(directory, "Microsoft.AspNetCore.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }
}
