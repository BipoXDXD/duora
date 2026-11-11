using System.Text.RegularExpressions;

namespace Duora.ArchitectureTests;

/// <summary>
/// Serialização, deadlock e <c>lock_timeout</c> que chegam à camada HTTP são classificados num lugar só,
/// <c>Http/DatabaseUnavailability.cs</c>, que também decide o <c>Retry-After</c> (#465). Um filtro de endpoint que volte
/// a capturar esses estados responderia diferente das outras rotas; este teste lê o fonte dos projetos web e falha se
/// algum outro arquivo citar os três estados.
/// </summary>
public sealed partial class ContentionClassificationTests
{
    private const string Owner = "Duora.Web/Http/DatabaseUnavailability.cs";

    private static readonly string[] WebProjects = ["Duora.Web", "Duora.Web.Identity"];

    [Fact]
    public void OnlyTheGlobalHandlerClassifiesContention()
    {
        var sources = SourceRoot();
        var files = WebProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(sources, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .ToList();
        Assert.Contains(files, file => Relative(sources, file) == Owner);

        var offenders = files
            .Where(file => Relative(sources, file) != Owner && ContentionState().IsMatch(File.ReadAllText(file)))
            .Select(file => Relative(sources, file))
            .ToList();

        Assert.True(offenders.Count == 0, $"Contenção tratada fora do handler global: {string.Join(", ", offenders)}");
    }

    [GeneratedRegex(@"SerializationFailure|DeadlockDetected|LockNotAvailable|""40001""|""40P01""|""55P03""")]
    private static partial Regex ContentionState();

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>O <c>backend/src</c> do checkout, subindo a partir da pasta de saída do teste.</summary>
    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Duora.Web");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "src");
            }
        }

        throw new InvalidOperationException($"backend/src não encontrado acima de {AppContext.BaseDirectory}.");
    }
}
