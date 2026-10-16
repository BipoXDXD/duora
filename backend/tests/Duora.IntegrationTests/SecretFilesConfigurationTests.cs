extern alias worker;
using System.Security.Cryptography.X509Certificates;
using Duora.Web;
using Duora.Web.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkerHost = worker::Duora.Worker.WorkerHost;

namespace Duora.IntegrationTests;

/// <summary>
/// Em produção os segredos chegam como arquivos (Compose secrets em /run/secrets), um por chave, com o nome no
/// formato de variável de ambiente (<c>ConnectionStrings__Duora</c>). Spec 15.4: a aplicação precisa lê-los por
/// configuração explícita; um <c>*_FILE</c> não funciona sozinho.
/// </summary>
public sealed class SecretFilesConfigurationTests : IDisposable
{
    private const string SyntheticConnectionString = "Host=postgres;Database=duora;Username=duora_app;Password=synthetic";

    private readonly DirectoryInfo _secrets = Directory.CreateTempSubdirectory("duora-secrets-");

    public void Dispose() => _secrets.Delete(recursive: true);

    [Fact]
    public async Task WebReadsConnectionStringFromSecretFile()
    {
        WriteSecret("ConnectionStrings__Duora", SyntheticConnectionString);

        await using var app = DuoraWebApp.Build(["--environment=Testing", $"--SecretsDirectory={_secrets.FullName}"]);

        Assert.Equal(SyntheticConnectionString, app.Configuration.GetConnectionString("Duora"));
    }

    [Fact]
    public async Task WebEncryptsTheKeyRingWithTheCertificateFromTheSecretFiles()
    {
        // Mesmos nomes de target do compose.prod.yml; o arquivo de anteriores existe vazio fora de uma rotação.
        var certificate = TestCertificates.Create("duora-key-ring");
        WriteSecret("DataProtection__Certificate", certificate.Pem);
        WriteSecret("DataProtection__PreviousCertificates", "");

        await using var app = DuoraWebApp.Build(["--environment=Testing", $"--SecretsDirectory={_secrets.FullName}"]);

        var loaded = app.Services.GetRequiredService<KeyRingCertificates>();
        var current = Assert.IsType<X509Certificate2>(loaded.Current);
        Assert.Equal(certificate.RawBase64, Convert.ToBase64String(current.RawData));
        Assert.Empty(loaded.Previous);
    }

    [Fact]
    public void WorkerReadsConnectionStringFromSecretFile()
    {
        WriteSecret("ConnectionStrings__Duora", SyntheticConnectionString);

        var builder = WorkerHost.CreateBuilder(["--environment=Testing", $"--SecretsDirectory={_secrets.FullName}"]);

        Assert.Equal(SyntheticConnectionString, builder.Configuration.GetConnectionString("Duora"));
    }

    [Fact]
    public async Task TrailingNewlineOfSecretFileIsNotPartOfTheValue()
    {
        // Arquivos criados com echo ou editores terminam em \n; a senha não pode levar a quebra junto.
        WriteSecret("ConnectionStrings__Duora", SyntheticConnectionString + "\n");

        await using var app = DuoraWebApp.Build(["--environment=Testing", $"--SecretsDirectory={_secrets.FullName}"]);

        Assert.Equal(SyntheticConnectionString, app.Configuration.GetConnectionString("Duora"));
    }

    [Fact]
    public async Task SecretFileWinsOverCommandLineAndEnvironment()
    {
        // O arquivo é a fonte de verdade em produção: uma variável esquecida no host não pode sobrepor o segredo.
        WriteSecret("ConnectionStrings__Duora", SyntheticConnectionString);

        await using var app = DuoraWebApp.Build(
        [
            "--environment=Testing",
            "--ConnectionStrings:Duora=Host=wrong;Database=wrong",
            $"--SecretsDirectory={_secrets.FullName}",
        ]);

        Assert.Equal(SyntheticConnectionString, app.Configuration.GetConnectionString("Duora"));
    }

    [Fact]
    public void WebDoesNotBuildWhenSecretsDirectoryIsMissing()
    {
        // Diretório configurado e ausente é erro de deploy (volume não montado): falha no boot, não sobe sem segredo.
        var missing = Path.Combine(_secrets.FullName, "nao-existe");

        Assert.Throws<DirectoryNotFoundException>(
            () => DuoraWebApp.Build(["--environment=Testing", $"--SecretsDirectory={missing}"]));
    }

    [Fact]
    public void WorkerDoesNotBuildWhenSecretsDirectoryIsMissing()
    {
        var missing = Path.Combine(_secrets.FullName, "nao-existe");

        Assert.Throws<DirectoryNotFoundException>(
            () => WorkerHost.CreateBuilder(["--environment=Testing", $"--SecretsDirectory={missing}"]));
    }

    private void WriteSecret(string name, string value) =>
        File.WriteAllText(Path.Combine(_secrets.FullName, name), value);
}
