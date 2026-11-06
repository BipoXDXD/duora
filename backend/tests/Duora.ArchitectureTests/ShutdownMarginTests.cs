using System.Globalization;
using System.Text.RegularExpressions;
using Duora.IntegrationTests;
using Duora.Web;
using Duora.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Duora.ArchitectureTests;

/// <summary>
/// O host precisa desistir de esperar antes que o Docker mande SIGKILL: o prazo de parada de cada host fica abaixo
/// do <c>stop_grace_period</c> do seu serviço na referência revisada <c>compose.prod.yml</c>. Com prazos iguais, a
/// margem é zero. A produção usa <c>/opt/duora/compose.coolify.json</c>, fora do repositório e fora do alcance deste
/// teste. Sobe só o composition root, sem iniciar nada, então não precisa de banco.
/// </summary>
public sealed class ShutdownMarginTests
{
    private const string SyntheticConnectionString = "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x";
    private const string SyntheticPublicOrigin = "--Duora:PublicOrigin=https://duora.test";

    [Fact]
    public void TheWorkerGivesUpBeforeItsStopGracePeriodEnds()
    {
        using var host = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            SyntheticConnectionString,
            SyntheticPublicOrigin,
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
        ]).Build();

        AssertGivesUpBefore(StopGracePeriodOf("worker"), host.Services);
    }

    [Fact]
    public async Task TheWebGivesUpBeforeItsStopGracePeriodEnds()
    {
        await using var app = DuoraWebApp.Build(
        [
            "--environment=Testing",
            SyntheticConnectionString,
            SyntheticPublicOrigin,
            $"--DataProtection:KeysDirectory={Path.Combine(Path.GetTempPath(), "duora-keys-unused")}",
            "--DataProtection:RequireMountedVolume=false",
            "--DataProtection:RequireCertificate=false",
        ]);

        AssertGivesUpBefore(StopGracePeriodOf("web"), app.Services);
    }

    private static void AssertGivesUpBefore(TimeSpan stopGracePeriod, IServiceProvider services)
    {
        var shutdownTimeout = services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;

        Assert.True(
            shutdownTimeout < stopGracePeriod,
            $"HostOptions.ShutdownTimeout ({shutdownTimeout}) precisa ficar abaixo do stop_grace_period ({stopGracePeriod}).");
    }

    private static TimeSpan StopGracePeriodOf(string service)
    {
        // Referência revisada compose.prod.yml, não o arquivo que a produção executa.
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.prod.yml"));
        var block = Assert.Single(Regex.Matches(
            compose, $@"^  {service}:\n(?<body>(?:(?:    .*)?\n)+)", RegexOptions.Multiline)).Groups["body"].Value;
        var seconds = Assert.Single(Regex.Matches(
            block, @"^    stop_grace_period: (?<seconds>\d+)s$", RegexOptions.Multiline)).Groups["seconds"].Value;

        return TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture));
    }
}
