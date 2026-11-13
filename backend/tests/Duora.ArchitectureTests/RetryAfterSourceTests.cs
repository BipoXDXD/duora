using System.Text.RegularExpressions;

namespace Duora.ArchitectureTests;

/// <summary>
/// O <c>Retry-After</c> de um 503 causado pelo banco (contenção, pool, servidor fora, timeout de comando) vem de
/// <c>Http/DatabaseUnavailability.cs</c>, fonte única (#465, #473, #493). Um endpoint que escolhe o valor por conta
/// própria deixa a rota responder diferente das outras para a mesma falha. Como o fonte não diz a causa de uma
/// constante nomeada, o teste não tenta adivinhá-la: só os arquivos de <see cref="Writers"/> podem chamar
/// <c>SetRetryAfter</c> ou escrever o header, cada um com o motivo de não ser banco. Arquivo novo na lista é decisão de
/// revisão, e o resto passa por <c>DatabaseUnavailability</c>.
/// </summary>
public sealed partial class RetryAfterSourceTests
{
    private const string Owner = "Duora.Web/Http/DatabaseUnavailability.cs";

    /// <summary>
    /// Quem escreve o <c>Retry-After</c> por conta própria, e por que a causa não é o banco. O <c>ApiProblems</c> é o
    /// escritor comum (<c>SetRetryAfter</c>), usado por <c>DatabaseUnavailability</c> e pelos 429.
    /// </summary>
    private static readonly Dictionary<string, string> Writers = new()
    {
        ["Duora.Web/Http/ApiProblems.cs"] = "define SetRetryAfter e monta o 503 e o 429 com o prazo recebido",
        ["Duora.Web/Media/PhotoEndpoints.cs"] = "fila de mídia, limite de pendentes e storage do upload",
        ["Duora.Web/Media/MediaAdmission.cs"] = "falha de I/O do storage no gate de mídia (o banco vai para a fonte)",
        ["Duora.Web/Media/MediaAssetEndpoints.cs"] = "falha de leitura do derivado no storage",
        ["Duora.Web/Safety/PhotoReviewEndpoints.cs"] = "revisão sem evidência, prazo vencido ou I/O (o 57014 vai para a fonte)",
        ["Duora.Web/Safety/PhotoReviewEvidenceResult.cs"] = "I/O do storage na entrega (o Npgsql vai para a fonte)",
        ["Duora.Web/Safety/RegistrationReconciliationEndpoints.cs"] = "statement_timeout de 30 s das rotas de reconciliação",
        ["Duora.Web/Accounts/SelfieVerificationEndpoints.cs"] = "pausa de 1 hora da selfie, calculada",
        ["Duora.Web/Http/ServiceUnavailableOpenApi.cs"] = "declara o header no contrato OpenAPI, não o escreve na resposta",
    };

    private static readonly string[] WebProjects = ["Duora.Web", "Duora.Web.Identity"];

    [Fact]
    public void OnlyTheAllowlistedFilesWriteTheRetryAfterHeader()
    {
        var offenders = Offenders(WritesRetryAfter, skip: Writers.Keys.ToHashSet());

        Assert.True(
            offenders.Count == 0,
            $"Retry-After escrito fora da lista; use DatabaseUnavailability (banco) ou justifique o arquivo em Writers: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryAllowlistedFileStillWritesTheRetryAfterHeader()
    {
        var sources = SourceRoot();

        var stale = Writers.Keys
            .Where(file => !WritesRetryAfter(File.ReadAllText(Path.Combine(sources, file))))
            .ToList();

        Assert.True(stale.Count == 0, $"Arquivo na lista que já não escreve o Retry-After: {string.Join(", ", stale)}");
    }

    [Fact]
    public void NoEndpointPassesALiteralDurationToTheRetryAfterHeader()
    {
        var offenders = Offenders(PassesLiteralDuration, skip: [Owner, "Duora.Web/Http/ApiProblems.cs"]);

        Assert.True(
            offenders.Count == 0,
            $"Retry-After com prazo literal; use DatabaseUnavailability (banco) ou uma constante nomeada (outra causa): {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("ApiProblems.SetRetryAfter(http, ReviewUnavailableRetryAfter);")]
    [InlineData("ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));")]
    [InlineData("SetRetryAfter(\n    http,\n    BusyRetryAfter);")]
    [InlineData("http.Response.Headers.RetryAfter = \"2\";")]
    [InlineData("http.Response.Headers[\"Retry-After\"] = \"2\";")]
    [InlineData("http.Response.Headers[HeaderNames.RetryAfter] = \"2\";")]
    [InlineData("http.Response.Headers.Append(\"Retry-After\", \"2\");")]
    [InlineData("catch (PostgresException) { if (x) { SetRetryAfter(http, Wait); } }")]
    public void TheDetectorFindsEveryWayOfWritingTheHeader(string source)
    {
        Assert.True(WritesRetryAfter(source));
    }

    [Theory]
    [InlineData("return DatabaseUnavailability.Unavailable(http, error);")]
    [InlineData("return DatabaseUnavailability.Contention(http);")]
    [InlineData("private static readonly TimeSpan StorageRetryAfter = TimeSpan.FromSeconds(2);")]
    [InlineData("// ApiProblems.SetRetryAfter(http, Wait);")]
    [InlineData("/// <c>Retry-After</c> do 503; Headers.RetryAfter = \"2\" era o jeito antigo.")]
    [InlineData("/* SetRetryAfter(http, Wait); */ return StorageUnavailable(http);")]
    [InlineData("http.Response.Headers.CacheControl = \"no-store\";")]
    [InlineData("var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : Fallback;")]
    public void TheDetectorIgnoresWhatIsNotAWrite(string source)
    {
        Assert.False(WritesRetryAfter(source));
    }

    [Theory]
    [InlineData("ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));", true)]
    [InlineData("ApiProblems.Unavailable(http, TimeSpan.FromMinutes(1));", true)]
    [InlineData("Unavailable(\n    http,\n    TimeSpan.FromSeconds(5));", true)]
    [InlineData("ApiProblems.SetRetryAfter(http, StorageRetryAfter);", false)]
    [InlineData("return StorageUnavailable(http, TimeSpan.FromSeconds(2));", false)]
    [InlineData("// SetRetryAfter(http, TimeSpan.FromSeconds(2));", false)]
    [InlineData("private static readonly TimeSpan StorageRetryAfter = TimeSpan.FromSeconds(2);", false)]
    public void TheLiteralDetectorSeesOnlyTheCallsItsRuleCovers(string source, bool expected)
    {
        Assert.Equal(expected, PassesLiteralDuration(source));
    }

    // Chamada de SetRetryAfter, ou escrita do header por propriedade, indexador ou Append/Add/Set com o nome dele.
    [GeneratedRegex(
        @"\bSetRetryAfter\s*\(|\bHeaders\b[^;]*?(?:\.RetryAfter\b|HeaderNames\.RetryAfter\b|""Retry-After"")",
        RegexOptions.Singleline)]
    private static partial Regex RetryAfterWrite();

    // SetRetryAfter(http, TimeSpan.FromSeconds(2)) ou Unavailable(http, TimeSpan.From...), com quebra de linha. O \b
    // evita pegar StorageUnavailable(.
    [GeneratedRegex(@"\b(?:SetRetryAfter|Unavailable)\s*\([^;{}]*\bTimeSpan\.From", RegexOptions.Singleline)]
    private static partial Regex LiteralDuration();

    [GeneratedRegex(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    private static bool WritesRetryAfter(string source) => RetryAfterWrite().IsMatch(WithoutComments(source));

    private static bool PassesLiteralDuration(string source) => LiteralDuration().IsMatch(WithoutComments(source));

    private static string WithoutComments(string source) => Comments().Replace(source, " ");

    private static List<string> Offenders(Func<string, bool> detector, IReadOnlyCollection<string> skip)
    {
        var sources = SourceRoot();
        var files = WebProjects
            .SelectMany(project =>
                Directory.EnumerateFiles(Path.Combine(sources, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .ToList();
        Assert.Contains(files, file => Relative(sources, file) == Owner);

        return files
            .Where(file => !skip.Contains(Relative(sources, file)) && detector(File.ReadAllText(file)))
            .Select(file => Relative(sources, file))
            .ToList();
    }

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
