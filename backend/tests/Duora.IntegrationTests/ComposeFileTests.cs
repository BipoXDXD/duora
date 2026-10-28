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

    /// <summary>
    /// Issue #93 (decisões do dono em 28/10/2026): a selfie fica ligada em produção. As chaves do usuário IAM chegam
    /// ao web (STS AssumeRole) e ao worker (Rekognition) só como arquivos em /run/secrets com o nome da chave de
    /// configuração, montados dos arquivos do operador em DUORA_SECRETS_DIR. Nenhuma variável AWS_* em texto.
    /// </summary>
    [Fact]
    public void ProductionSelfieIsEnabledAndGetsAwsKeysOnlyAsSecretFiles()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "infra", "compose");
        var compose = File.ReadAllText(Path.Combine(root, "compose.prod.yml"));
        var web = Between(compose, "\n  web:\n", "\n  worker:\n");
        var worker = Between(compose, "\n  worker:\n", "\n  edge:\n");
        var topLevelSecrets = compose[compose.IndexOf("\nsecrets:\n", StringComparison.Ordinal)..];

        foreach (var (source, target, file) in new[]
        {
            ("selfie_aws_access_key_id", "SelfieVerification__AwsAccessKeyId", "selfie-aws-access-key-id"),
            ("selfie_aws_secret_access_key", "SelfieVerification__AwsSecretAccessKey", "selfie-aws-secret-access-key"),
        })
        {
            foreach (var service in new[] { web, worker })
            {
                Assert.Contains($"- source: {source}\n        target: {target}\n", service, StringComparison.Ordinal);
            }

            Assert.Equal(2, Regex.Count(compose, $@"- source: {source}\b"));
            Assert.Contains($"  {source}:\n    file: ${{DUORA_SECRETS_DIR:?defina DUORA_SECRETS_DIR}}/{file}\n",
                topLevelSecrets, StringComparison.Ordinal);
        }

        foreach (var service in new[] { web, worker })
        {
            Assert.Contains("      SelfieVerification__Enabled: \"true\"\n", service, StringComparison.Ordinal);
            Assert.Contains("      SelfieVerification__PrivacyApproved: \"true\"\n", service, StringComparison.Ordinal);
            Assert.Contains("      SelfieVerification__Region: us-east-1\n", service, StringComparison.Ordinal);
            Assert.Contains("      SelfieVerification__CaptureRoleArn: arn:aws:iam::394877252106:role/duora-selfie-capture\n",
                service, StringComparison.Ordinal);
            Assert.DoesNotContain("SelfieVerification__Minimum", service, StringComparison.Ordinal);
            Assert.DoesNotContain("SelfieVerification__Maximum", service, StringComparison.Ordinal);
        }

        foreach (var file in Directory.GetFiles(root, "compose*.yml"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("AWS_ACCESS_KEY_ID", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AWS_SESSION_TOKEN", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SelfieVerification__AwsAccessKeyId:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SelfieVerification__AwsSecretAccessKey:", text, StringComparison.Ordinal);
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
