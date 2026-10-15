using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Duora.ArchitectureTests;

/// <summary>
/// Fitness functions da spec 3.3. O compilador já impede referência de projeto fora do grafo declarado;
/// estes testes pegam o que ele não pega: um pacote de framework ou fornecedor entrando no Core, ou um
/// contrato de transporte passando a depender do domínio. Olham as referências gravadas no assembly,
/// ou seja, as que o código realmente usa.
/// </summary>
public sealed class DependencyRulesTests
{
    private const string Core = "Duora.Core";
    private const string Contracts = "Duora.Contracts";
    private const string Infrastructure = "Duora.Infrastructure";
    private const string Web = "Duora.Web";
    private const string Worker = "Duora.Worker";
    private const string WebIdentity = "Duora.Web.Identity";

    private static readonly string[] ProductionAssemblies = [Core, Contracts, Infrastructure, Web, WebIdentity, Worker];

    /// <summary>
    /// Core e Contracts só podem usar a BCL e abstrações do Microsoft.Extensions. Nenhum projeto Duora é permitido
    /// a eles (spec 3.3). Allowlist em vez de denylist: um fornecedor novo é barrado sem ninguém lembrar de listá-lo.
    /// </summary>
    [Theory]
    [InlineData(Core)]
    [InlineData(Contracts)]
    public void InnerAssembliesReferenceOnlyAllowedAssemblies(string assembly)
    {
        var disallowed = ReferencesOf(assembly).Where(name => !IsBaseLibrary(name));

        Assert.Empty(disallowed);
    }

    [Theory]
    [InlineData(Infrastructure, Contracts)]
    [InlineData(Infrastructure, Web)]
    [InlineData(Infrastructure, Worker)]
    public void AssemblyDoesNotReferenceForbiddenProject(string assembly, string forbidden)
    {
        Assert.DoesNotContain(forbidden, ReferencesOf(assembly));
    }

    [Fact]
    public void ReferencesAreReadFromAssemblyMetadata()
    {
        // Sentinela: se a leitura de referências quebrar e devolver vazio, as regras acima passariam à toa.
        Assert.Contains(Infrastructure, ReferencesOf(Web));
    }

    [Fact]
    public void ProductionAssembliesHaveNoReferenceCycles()
    {
        var cycle = FindCycle(ProductionAssemblies.ToDictionary(name => name, ProjectReferencesOf));

        Assert.Null(cycle);
    }

    [Theory]
    [InlineData(Core)]
    [InlineData(Contracts)]
    [InlineData(Infrastructure)]
    [InlineData(Web)]
    [InlineData(WebIdentity)]
    [InlineData(Worker)]
    public void TypesLiveUnderTheirAssemblyNamespace(string assembly)
    {
        var loaded = Assembly.Load(assembly);
        var entryPointType = loaded.EntryPoint?.DeclaringType;
        var misplaced = loaded.GetTypes()
            .Where(type => !type.IsNested && type != entryPointType && !IsGenerated(type))
            .Where(type => type.Namespace != assembly && !(type.Namespace ?? "").StartsWith(assembly + ".", StringComparison.Ordinal))
            .Select(type => type.FullName);

        Assert.Empty(misplaced);
    }

    private static bool IsBaseLibrary(string name) =>
        name is "netstandard" or "System"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            && name.EndsWith(".Abstractions", StringComparison.Ordinal));

    /// <summary>
    /// Tipos que o compilador ou um source generator emitem (atributos embutidos, código do gerador de OpenAPI),
    /// cujo namespace não é escolha nossa. O Program das top-level statements sai pelo entry point.
    /// </summary>
    private static bool IsGenerated(Type type) =>
        type.Name == "<Module>"
        || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || type.IsDefined(typeof(GeneratedCodeAttribute), inherit: false);

    private static string[] ReferencesOf(string assemblyName) =>
        [.. Assembly.Load(assemblyName).GetReferencedAssemblies().Select(reference => reference.Name ?? "")];

    private static string[] ProjectReferencesOf(string assemblyName) =>
        [.. ReferencesOf(assemblyName).Where(ProductionAssemblies.Contains)];

    /// <summary>Busca em profundidade; devolve o caminho do primeiro ciclo encontrado, ou null.</summary>
    private static string? FindCycle(Dictionary<string, string[]> graph)
    {
        var finished = new HashSet<string>();
        var path = new List<string>();

        string? Visit(string node)
        {
            if (path.Contains(node))
            {
                return string.Join(" -> ", path.SkipWhile(n => n != node).Append(node));
            }

            if (!finished.Add(node))
            {
                return null;
            }

            path.Add(node);
            var cycle = graph[node].Select(Visit).FirstOrDefault(found => found is not null);
            path.RemoveAt(path.Count - 1);
            return cycle;
        }

        return graph.Keys.Select(Visit).FirstOrDefault(found => found is not null);
    }
}
