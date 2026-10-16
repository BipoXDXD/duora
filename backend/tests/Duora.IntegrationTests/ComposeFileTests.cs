using System.Text.RegularExpressions;

namespace Duora.IntegrationTests;

public sealed class ComposeFileTests
{
    [Fact]
    public void DevComposeUsesTheSamePostgresImageAsTheTests()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.dev.yml"));

        Assert.Contains($"image: {PostgresFixture.Image}", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPostgresImageUsesTheSameVersionAsTheTests()
    {
        var dockerfile = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "infra", "images", "postgres", "Dockerfile"));

        // Tag com variante do Debian e digest: "FROM postgres:18.6-trixie@sha256:...".
        Assert.Contains($"FROM {PostgresFixture.Image}-", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// T-15: o diretório que o web recebe em DataProtection__KeysDirectory é exatamente onde o volume nomeado é
    /// montado, e só o web monta o key ring (spec 11.2: o worker não emite cookies). O boot também recusa diretório
    /// que não seja volume; este teste pega a divergência antes do deploy.
    /// </summary>
    [Fact]
    public void ProductionKeyRingDirectoryIsTheNamedVolumeMountedOnlyInTheWeb()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.prod.yml"));

        var directory = Assert.Single(
            Regex.Matches(compose, @"^\s+DataProtection__KeysDirectory:\s*(\S+)\s*$", RegexOptions.Multiline)).Groups[1].Value;
        var mount = Assert.Single(Regex.Matches(compose, @"^\s+-\s*dataprotection:(\S+)\s*$", RegexOptions.Multiline));

        Assert.Equal(directory, mount.Groups[1].Value);
        Assert.Contains(mount.Value, Between(compose, "\n  web:\n", "\n  worker:\n"), StringComparison.Ordinal);
        Assert.Contains("\n  dataprotection:\n", Between(compose, "\nvolumes:\n", "\nsecrets:\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A exigência do volume só é desligável fora de Production, e o boot recusa o <c>false</c> em Production; o
    /// Compose de produção não pode nem mencioná-la nem rodar o web em outro ambiente.
    /// </summary>
    [Fact]
    public void ProductionWebRunsAsProductionWithoutTurningOffTheMountedVolume()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.prod.yml"));

        Assert.DoesNotContain("RequireMountedVolume", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireCertificate", compose, StringComparison.Ordinal);
        Assert.Contains(
            "\n      ASPNETCORE_ENVIRONMENT: Production\n",
            Between(compose, "\n  web:\n", "\n  worker:\n"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 11.2: o certificado do key ring e os anteriores chegam ao web como os arquivos que o Data Protection lê
    /// (DataProtection__Certificate e DataProtection__PreviousCertificates), e a nenhum outro serviço.
    /// </summary>
    [Fact]
    public void ProductionKeyRingCertificateReachesOnlyTheWeb()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.prod.yml"));
        var web = Between(compose, "\n  web:\n", "\n  worker:\n");
        var topLevelSecrets = compose[compose.IndexOf("\nsecrets:\n", StringComparison.Ordinal)..];

        foreach (var (source, target) in new[]
        {
            ("dataprotection_certificate", "DataProtection__Certificate"),
            ("dataprotection_previous_certificates", "DataProtection__PreviousCertificates"),
        })
        {
            Assert.Contains($"- source: {source}\n        target: {target}\n", web, StringComparison.Ordinal);
            Assert.Equal(1, Regex.Count(compose, $@"- source: {source}\b"));
            Assert.Contains($"/{source}\n", topLevelSecrets, StringComparison.Ordinal);
        }
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + 1, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"Trecho entre {start.Trim()} e {end.Trim()} não encontrado.");
        return text[from..to];
    }
}
