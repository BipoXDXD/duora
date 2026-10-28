using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Web;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests;

public sealed class StartupConfigurationTests
{
    /// <summary>
    /// Sobe a mesma app do Program, mas chama StartAsync na thread do teste. O WebApplicationFactory não serve
    /// para boot inválido: a thread dele que roda o Program falha no Run() e descarta o host, e o
    /// DeferredHost.StartAsync pode consultar o IServiceProvider já descartado (ObjectDisposedException,
    /// visto no CI Linux) antes de ver a exceção real.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WebDoesNotStartWithoutConnectionString(string connectionString)
    {
        await using var app = BuildWeb($"--ConnectionStrings:Duora={connectionString}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ConnectionStrings:Duora", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--Database:MaxPoolSize=0")]
    [InlineData("--Database:MaxPoolSize=41")]
    [InlineData("--Database:PoolTimeout=00:00:00")]
    [InlineData("--Database:PoolTimeout=00:00:31")]
    [InlineData("--Database:PoolTimeout=00:00:01.500")]
    public async Task WebDoesNotStartWithAPoolOutsideItsShareOfTheConnectionBudget(string setting)
    {
        await using var app = BuildWeb(setting);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Database:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWebPoolIsFortyConnectionsWaitingAtMostFiveSecondsByDefault()
    {
        await using var app = BuildWeb();

        var connection = new Npgsql.NpgsqlConnectionStringBuilder(
            app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().ConnectionString);

        Assert.Equal(40, connection.MaxPoolSize);
        Assert.Equal(5, connection.Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("duora.app")]
    [InlineData("http://duora.app")]
    [InlineData("https://duora.app/app")]
    public async Task WebDoesNotStartWithoutAnHttpsPublicOrigin(string publicOrigin)
    {
        await using var app = BuildWeb($"--Duora:PublicOrigin={publicOrigin}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Duora:PublicOrigin", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("suporte")]
    [InlineData("suporte@exemplo.com\nBcc: alguem@exemplo.com")]
    public async Task WebDoesNotStartWithoutAValidSupportContact(string supportContact)
    {
        // O contato vai no texto do aviso de segurança: só um endereço de e-mail válido, sem quebra de linha.
        await using var app = BuildWeb($"--Duora:SupportContact={supportContact}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Duora:SupportContact", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nao-e-uma-rede")]
    [InlineData("172.30.10.0/99")]
    public async Task WebDoesNotStartWithAnInvalidProxyNetwork(string network)
    {
        await using var app = BuildWeb($"--ReverseProxy:KnownNetworks:0={network}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ReverseProxy:KnownNetworks", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WebDoesNotStartWithoutAPersistentKeyRing(string keysDirectory)
    {
        await using var app = BuildWeb($"--DataProtection:KeysDirectory={keysDirectory}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:KeysDirectory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebRefusesTechnicalOnlyPhotoReviewInProduction()
    {
        // ADR-0025: sem revisão editorial, foto real não pode ser aprovada só pela checagem técnica. Em Production o
        // boot já cai antes no Data Protection (volume e certificado), então a opção de mídia é conferida direto no
        // container do Web: é a mesma validação que o ValidateOnStart executa.
        await using var app = BuildWeb([
            "--environment=Production",
            "--Media:Review=TechnicalOnly",
            .. CompleteS3Storage,
        ]);

        var exception = Assert.Throws<OptionsValidationException>(
            () => app.Services.GetRequiredService<IOptions<MediaOptions>>().Value);

        Assert.Contains("Media:Review", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebAcceptsHumanPhotoReviewInProduction()
    {
        // Decisão do dono de 2026-10-28: a revisão humana editorial é a política de Production. Ligar o upload continua
        // sendo do inventário do host (#69); aqui só a validação da opção é conferida.
        await using var app = BuildWeb([
            "--environment=Production",
            "--Media:Review=HumanReview",
            .. CompleteS3Storage,
        ]);

        var options = app.Services.GetRequiredService<IOptions<MediaOptions>>().Value;

        Assert.Equal(MediaReviewPolicy.HumanReview, options.Review);
    }

    [Fact]
    public async Task WebRefusesTheFileSystemPhotoStoreInProductionWithoutTheMountedDirectory()
    {
        await using var app = BuildWeb(
            "--environment=Production",
            "--Media:Storage:Provider=FileSystem",
            $"--Media:Storage:FileSystemRoot={Path.Combine(Path.GetTempPath(), $"duora-sem-volume-{Guid.NewGuid():N}")}");

        var exception = Assert.Throws<OptionsValidationException>(
            () => app.Services.GetRequiredService<IOptions<MediaStorageOptions>>().Value);

        Assert.Contains("FileSystemRoot", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDoesNotStartWithPhotoUploadsOnAndNoStorage()
    {
        // Com a chave da exclusão presente, só o storage falta: é ele que a recusa precisa apontar.
        await using var app = BuildWeb(
            "--Media:Review=TechnicalOnly",
            $"--Media:Deletion:EnvironmentId={Guid.NewGuid()}",
            "--Media:Deletion:OperationKeyId=test-v1",
            $"--Media:Deletion:OperationKey={Convert.ToBase64String(new byte[32])}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Media:Storage:Provider", exception.Message, StringComparison.Ordinal);
    }

    private static readonly string[] CompleteS3Storage =
    [
        "--Media:Storage:Provider=S3",
        "--Media:Storage:S3:ServiceUrl=https://storage.example.com",
        "--Media:Storage:S3:Region=us-east-1",
        "--Media:Storage:S3:Bucket=duora-media",
        "--Media:Storage:S3:AccessKeyId=synthetic-access",
        "--Media:Storage:S3:SecretAccessKey=synthetic-secret",
    ];

    /// <summary>
    /// O diretório temporário do teste é só uma pasta, como seria a camada gravável do container se o volume sumisse
    /// do Compose (T-15). Fora do Linux não há tabela de montagens, e o boot também falha.
    /// </summary>
    [Fact]
    public async Task WebDoesNotStartWithTheKeyRingOutsideAMountedVolume()
    {
        await using var app = BuildWeb("--DataProtection:RequireMountedVolume=true");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:KeysDirectory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume persistente", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Desligar a exigência do volume é coisa de desenvolvimento, teste e geração do OpenAPI: em qualquer outro
    /// ambiente o boot recusa, venha o <c>false</c> do Compose, de variável esquecida no host ou de um appsettings que
    /// foi junto na imagem.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Homologacao")]
    public async Task OnlyDevelopmentTestsAndBuildMayTurnOffTheMountedVolumeRequirement(string environment)
    {
        await using var app = BuildWeb($"--environment={environment}", "--DataProtection:RequireMountedVolume=false");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:RequireMountedVolume", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MountedVolumeIsRequiredUnlessConfigurationTurnsItOff()
    {
        Assert.True(new DataProtectionStorageOptions().RequireMountedVolume);
    }

    /// <summary>Production liga a exigência (padrão); sem o certificado do key ring o web não sobe (spec 11.2).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WebDoesNotStartWithoutTheKeyRingCertificateWhenRequired(string certificate)
    {
        await using var app = BuildWeb(
            "--DataProtection:RequireCertificate=true",
            $"--DataProtection:Certificate={certificate}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:Certificate", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DataProtection:Certificate")]
    [InlineData("DataProtection:PreviousCertificates")]
    public async Task WebDoesNotStartWithAMalformedKeyRingCertificate(string key)
    {
        await using var app = BuildWeb($"--{key}=-----BEGIN CERTIFICATE-----lixo-----END CERTIFICATE-----");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDoesNotStartWithACertificateWithoutItsPrivateKey()
    {
        var pem = TestCertificates.Create("sem-chave").Pem;
        var certificateOnly = pem[..(pem.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + 25)];
        await using var app = BuildWeb($"--DataProtection:Certificate={certificateOnly}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:Certificate", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Homologacao")]
    public async Task OnlyDevelopmentTestsAndBuildMayTurnOffTheCertificateRequirement(string environment)
    {
        await using var app = BuildWeb($"--environment={environment}", "--DataProtection:RequireCertificate=false");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:RequireCertificate", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// O certificado é registrado com a configuração do momento do registro, e as options leem a configuração final.
    /// Uma fonte acrescentada depois (aqui, direto no ConfigurationManager já construído) faz a validação ver o
    /// certificado que o Data Protection não recebeu: o boot precisa falhar em vez de gravar o key ring em claro.
    /// </summary>
    [Fact]
    public async Task CertificateSeenOnlyByTheOptionsFailsTheBoot()
    {
        await using var app = BuildWeb();
        ((IConfigurationBuilder)app.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:RequireCertificate"] = "true",
            ["DataProtection:Certificate"] = TestCertificates.Create("so-nas-options").Pem,
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("não chegou ao Data Protection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDoesNotStartWithTwoPairsAsTheCurrentCertificate()
    {
        var pems = TestCertificates.Create("um").Pem + TestCertificates.Create("dois").Pem;
        await using var app = BuildWeb($"--DataProtection:Certificate={pems}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:Certificate", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>O EncryptedXml do Data Protection cifra a chave com RSA; um certificado ECDSA é recusado no boot.</summary>
    [Fact]
    public async Task WebDoesNotStartWithANonRsaCertificate()
    {
        await using var app = BuildWeb($"--DataProtection:Certificate={TestCertificates.CreateEcdsa("ecdsa").Pem}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("DataProtection:Certificate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyRingCertificateIsRequiredUnlessConfigurationTurnsItOff()
    {
        Assert.True(new DataProtectionStorageOptions().RequireCertificate);
    }

    /// <summary>
    /// Linha de comando tem a maior precedência: env e user-secrets da máquina não interferem. Porta 0: se a
    /// validação falhar em barrar o boot, o teste não disputa porta com ninguém. O banco nunca é contatado, porque
    /// a validação falha antes; os valores padrão são válidos para que cada teste invalide só o que testa.
    /// </summary>
    private static WebApplication BuildWeb(params string[] invalidSettings) =>
        DuoraWebApp.Build(
        [
            "--environment=Testing",
            "--urls=http://127.0.0.1:0",
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            $"--Duora:PublicOrigin={DuoraWebFactory.PublicOrigin}",
            $"--DataProtection:KeysDirectory={Path.Combine(Path.GetTempPath(), "duora-keys-unused")}",
            "--DataProtection:RequireMountedVolume=false",
            "--DataProtection:RequireCertificate=false",
            .. invalidSettings,
        ]);
}
