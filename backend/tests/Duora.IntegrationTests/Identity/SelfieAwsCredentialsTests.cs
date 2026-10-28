extern alias worker;

using Duora.Infrastructure.Identity;
using Duora.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// As credenciais do usuário IAM chegam ao SDK só pelos arquivos de segredo (SelfieVerification__AwsAccessKeyId e
/// SelfieVerification__AwsSecretAccessKey), nunca pela cadeia padrão da AWS (variáveis de ambiente, perfis, IMDS).
/// Valores sintéticos.
/// </summary>
public sealed class SelfieAwsCredentialsTests(PostgresFixture postgres)
{
    [Fact]
    public void ConfiguredKeysBecomeTheSdkCredentials()
    {
        var options = new SelfieAwsCredentialOptions
        {
            AwsAccessKeyId = "SYNTHETICACCESSKEYID",
            AwsSecretAccessKey = "synthetic-secret-value",
        };

        var credentials = options.ToAwsCredentials().GetCredentials();

        Assert.Equal("SYNTHETICACCESSKEYID", credentials.AccessKey);
        Assert.Equal("synthetic-secret-value", credentials.SecretKey);
        Assert.False(credentials.UseToken);
    }

    [Theory]
    [InlineData(null, "synthetic-secret-value")]
    [InlineData("SYNTHETICACCESSKEYID", null)]
    [InlineData(" ", "synthetic-secret-value")]
    [InlineData("SYNTHETICACCESSKEYID", "")]
    public void IncompleteKeysAreNotUsable(string? accessKeyId, string? secretAccessKey)
    {
        var options = new SelfieAwsCredentialOptions { AwsAccessKeyId = accessKeyId, AwsSecretAccessKey = secretAccessKey };

        Assert.False(options.IsComplete);
        Assert.Throws<InvalidOperationException>(options.ToAwsCredentials);
    }

    [Theory]
    [InlineData("SelfieVerification:AwsAccessKeyId")]
    [InlineData("SelfieVerification:AwsSecretAccessKey")]
    public async Task EnabledWebDoesNotStartWithoutEitherSecretFile(string missing)
    {
        await using var app = DuoraWebApp.Build(
        [
            "--environment=Testing",
            "--urls=http://127.0.0.1:0",
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            $"--Duora:PublicOrigin={DuoraWebFactory.PublicOrigin}",
            $"--DataProtection:KeysDirectory={Path.Combine(Path.GetTempPath(), "duora-keys-unused")}",
            "--DataProtection:RequireMountedVolume=false",
            "--DataProtection:RequireCertificate=false",
            .. EnabledArguments(missing),
        ]);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SelfieVerification:AwsAccessKeyId", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SelfieVerification:AwsAccessKeyId")]
    [InlineData("SelfieVerification:AwsSecretAccessKey")]
    public async Task EnabledWorkerDoesNotStartWithoutEitherSecretFile(string missing)
    {
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. EnabledArguments(missing),
        ]);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SelfieVerification:AwsAccessKeyId", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-value", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Caminho de produção: os dois arquivos em SecretsDirectory (com a quebra de linha final que um editor deixa)
    /// viram as credenciais do SDK no worker, sem espaço em branco.
    /// </summary>
    [Fact]
    public void WorkerReadsTheKeysFromTheSecretFilesWithoutTrailingNewline()
    {
        var directory = Directory.CreateTempSubdirectory("duora-selfie-secrets-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "SelfieVerification__AwsAccessKeyId"), "AKIDSYNTHETICFILE\n");
            File.WriteAllText(Path.Combine(directory.FullName, "SelfieVerification__AwsSecretAccessKey"), "synthetic-file-secret\n");
            var builder = WorkerHost.CreateBuilder(
            [
                "--environment", "Testing",
                "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
                "--Duora:PublicOrigin=https://duora.test",
                $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
                $"--SecretsDirectory={directory.FullName}",
                .. EnabledArguments("SelfieVerification:AwsAccessKeyId").Where(a => !a.Contains("AwsSecretAccessKey", StringComparison.Ordinal)),
            ]);
            using var host = builder.Build();

            var credentials = host.Services.GetRequiredService<IOptions<SelfieAwsCredentialOptions>>().Value
                .ToAwsCredentials().GetCredentials();

            Assert.Equal("AKIDSYNTHETICFILE", credentials.AccessKey);
            Assert.Equal("synthetic-file-secret", credentials.SecretKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DisabledSelfieStartsWithoutAwsKeys()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        await using var factory = new DuoraWebFactory(connection)
        {
            Settings = new Dictionary<string, string?> { ["SelfieVerification:Enabled"] = "false" },
        };

        Assert.False(factory.Services.GetRequiredService<IOptions<SelfieProviderOptions>>().Value.Enabled);
        Assert.False(factory.Services.GetRequiredService<IOptions<SelfieAwsCredentialOptions>>().Value.IsComplete);
    }

    private static string[] EnabledArguments(string missing) => [.. SelfieTestSettings.Enabled()
        .Where(setting => setting.Key != missing)
        .Select(setting => $"--{setting.Key}={setting.Value}")];
}
