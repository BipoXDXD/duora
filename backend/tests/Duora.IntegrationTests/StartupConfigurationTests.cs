using Duora.Web;
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
        // Linha de comando tem a maior precedência: env e user-secrets da máquina não interferem.
        // Porta 0: se a validação falhar em barrar o boot, o teste não disputa porta com ninguém.
        await using var app = DuoraWebApp.Build(
            ["--environment=Testing", "--urls=http://127.0.0.1:0", $"--ConnectionStrings:Duora={connectionString}"]);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ConnectionStrings:Duora", exception.Message, StringComparison.Ordinal);
    }
}
