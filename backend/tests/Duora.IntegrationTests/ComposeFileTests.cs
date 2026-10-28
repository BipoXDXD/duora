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
    /// Issue #93 (decisões do dono em 28/10/2026): a selfie fica ligada em produção. Cada serviço tem o próprio usuário
    /// IAM, com só o que ele chama: o web faz sts:AssumeRole no papel de captura; o worker cria a sessão, lê o resultado
    /// e roda DetectFaces. Cada par chega só ao seu serviço, como arquivos em /run/secrets com o nome da chave de
    /// configuração, montados dos arquivos do operador em DUORA_SECRETS_DIR. Nenhuma variável AWS_* em texto.
    /// </summary>
    [Theory]
    [InlineData("web", "worker")]
    [InlineData("worker", "web")]
    public void ProductionSelfieKeysReachOnlyTheirOwnServiceAsSecretFiles(string service, string other)
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.prod.yml"));
        var blocks = new Dictionary<string, string>
        {
            ["web"] = Between(compose, "\n  web:\n", "\n  worker:\n"),
            ["worker"] = Between(compose, "\n  worker:\n", "\n  edge:\n"),
        };
        var topLevelSecrets = compose[compose.IndexOf("\nsecrets:\n", StringComparison.Ordinal)..];

        foreach (var (part, target) in new[]
        {
            ("access_key_id", "SelfieVerification__AwsAccessKeyId"),
            ("secret_access_key", "SelfieVerification__AwsSecretAccessKey"),
        })
        {
            var source = $"selfie_{service}_aws_{part}";
            Assert.Contains($"- source: {source}\n        target: {target}\n", blocks[service], StringComparison.Ordinal);
            Assert.DoesNotContain($"selfie_{other}_aws_{part}", blocks[service], StringComparison.Ordinal);
            Assert.Equal(1, Regex.Count(compose, $@"- source: {source}\b"));
            Assert.Contains(
                $"  {source}:\n    file: ${{DUORA_SECRETS_DIR:?defina DUORA_SECRETS_DIR}}/{source.Replace('_', '-')}\n",
                topLevelSecrets, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionSelfieIsEnabledWithoutPlainTextAwsKeys()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "infra", "compose");
        var compose = File.ReadAllText(Path.Combine(root, "compose.prod.yml"));

        foreach (var service in new[] { Between(compose, "\n  web:\n", "\n  worker:\n"), Between(compose, "\n  worker:\n", "\n  edge:\n") })
        {
            // Liga pelo duora.env (Ansible, padrão "true"): um rollback para release anterior à #93 sobe com "false".
            Assert.Contains("      SelfieVerification__Enabled: ${DUORA_SELFIE_ENABLED:-false}\n", service, StringComparison.Ordinal);
            Assert.Contains("      SelfieVerification__PrivacyApproved: \"true\"\n", service, StringComparison.Ordinal);
            Assert.Contains("      SelfieVerification__Region: us-east-1\n", service, StringComparison.Ordinal);
            Assert.DoesNotContain("SelfieVerification__Minimum", service, StringComparison.Ordinal);
            Assert.DoesNotContain("SelfieVerification__Maximum", service, StringComparison.Ordinal);
        }

        // Só o web assume o papel de captura; o worker não recebe o ARN.
        Assert.Contains("      SelfieVerification__CaptureRoleArn: arn:aws:iam::394877252106:role/duora-selfie-capture\n",
            Between(compose, "\n  web:\n", "\n  worker:\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureRoleArn", Between(compose, "\n  worker:\n", "\n  edge:\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("selfie_aws_", compose, StringComparison.Ordinal);
        var ansible = Path.Combine(RepositoryRoot.Find(), "infra", "ansible");
        Assert.Contains("\nDUORA_SELFIE_ENABLED={{ duora_selfie_enabled }}\n",
            File.ReadAllText(Path.Combine(ansible, "roles", "app_dirs", "templates", "duora.env.j2")), StringComparison.Ordinal);
        Assert.Contains("\nduora_selfie_enabled: \"true\"\n",
            File.ReadAllText(Path.Combine(ansible, "group_vars", "all.yml")), StringComparison.Ordinal);
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
