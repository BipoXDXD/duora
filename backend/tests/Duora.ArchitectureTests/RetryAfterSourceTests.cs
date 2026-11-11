using System.Text.RegularExpressions;

namespace Duora.ArchitectureTests;

/// <summary>
/// O <c>Retry-After</c> de um 503 causado pelo banco (contenção, pool, servidor fora, timeout de comando) vem de
/// <c>Http/DatabaseUnavailability.cs</c>, fonte única (#465, #473, #493). Um endpoint que escolhe o valor por conta
/// própria deixa a rota responder diferente das outras para a mesma falha. Estes testes leem o fonte dos projetos web
/// e barram os dois jeitos de fazer isso: um prazo literal passado direto ao header e um <c>catch</c> de erro do
/// Npgsql que grava o header. O prazo de outra causa (fila de mídia, storage, capacidade) fica numa constante
/// nomeada perto dela, o que estes testes aceitam.
/// </summary>
public sealed partial class RetryAfterSourceTests
{
    private static readonly string[] Owners =
    [
        "Duora.Web/Http/DatabaseUnavailability.cs",
        "Duora.Web/Http/ApiProblems.cs",
    ];

    private static readonly string[] WebProjects = ["Duora.Web", "Duora.Web.Identity"];

    [Fact]
    public void NoEndpointPassesALiteralDurationToTheRetryAfterHeader()
    {
        var offenders = Offenders(InlineDuration());

        Assert.True(
            offenders.Count == 0,
            $"Retry-After com prazo literal; use DatabaseUnavailability (banco) ou uma constante nomeada (outra causa): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoNpgsqlCatchSetsTheRetryAfterHeaderItself()
    {
        var offenders = Offenders(NpgsqlCatchSettingHeader());

        Assert.True(
            offenders.Count == 0,
            $"catch de erro do Npgsql decide o Retry-After; use DatabaseUnavailability: {string.Join(", ", offenders)}");
    }

    private static List<string> Offenders(Regex pattern)
    {
        var sources = SourceRoot();
        var files = WebProjects
            .SelectMany(project =>
                Directory.EnumerateFiles(Path.Combine(sources, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .ToList();
        Assert.Contains(files, file => Relative(sources, file) == Owners[0]);

        return files
            .Where(file => !Owners.Contains(Relative(sources, file)) && pattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Relative(sources, file))
            .ToList();
    }

    // SetRetryAfter(http, TimeSpan.FromSeconds(2)) ou Unavailable(http, TimeSpan.From...), com qualquer nome de
    // contexto e quebra de linha.
    [GeneratedRegex(@"(?:SetRetryAfter|Unavailable)\s*\([^;{}]*TimeSpan\.From", RegexOptions.Singleline)]
    private static partial Regex InlineDuration();

    // catch (PostgresException ...) { ... SetRetryAfter(...) } sem outra chave no meio.
    [GeneratedRegex(@"catch[^{};]*(?:PostgresException|NpgsqlException)[^{};]*\{[^{}]*SetRetryAfter", RegexOptions.Singleline)]
    private static partial Regex NpgsqlCatchSettingHeader();

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>O <c>backend/src</c> do checkout, subindo a partir da pasta de saída do teste.</summary>
    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
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
