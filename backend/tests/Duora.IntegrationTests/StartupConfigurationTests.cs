using Duora.Web;
using Microsoft.AspNetCore.Builder;
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

    /// <summary>
    /// Linha de comando tem a maior precedência: env e user-secrets da máquina não interferem. Porta 0: se a
    /// validação falhar em barrar o boot, o teste não disputa porta com ninguém. O banco nunca é contatado, porque
    /// a validação falha antes; os valores padrão são válidos para que cada teste invalide só o que testa.
    /// </summary>
    private static WebApplication BuildWeb(string invalidSetting) =>
        DuoraWebApp.Build(
        [
            "--environment=Testing",
            "--urls=http://127.0.0.1:0",
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            $"--Duora:PublicOrigin={DuoraWebFactory.PublicOrigin}",
            invalidSetting,
        ]);
}
