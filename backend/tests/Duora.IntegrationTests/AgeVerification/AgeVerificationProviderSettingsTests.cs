extern alias worker;
using System.Net;
using System.Security.Cryptography;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Identity;
using Duora.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// O fornecedor simulado só existe em Development e Testing; sem fornecedor, o fluxo fica desligado e a conta
/// continua pendente (ADR-0024, TM-40).
/// </summary>
public sealed class AgeVerificationProviderSettingsTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Fora de Development e Testing o boot do web também exige volume montado e certificado do key ring (#34), que o
    /// teste não tem e que barram a subida antes. Por isso a regra é conferida resolvendo só as options da verificação
    /// etária, com a mesma validação que o <c>ValidateOnStart</c> roda.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task WebRefusesTheFakeProviderOutsideDevelopmentAndTesting(string environment)
    {
        await using var app = BuildWeb(environment, FakeArgs());

        var exception = Assert.Throws<OptionsValidationException>(
            () => app.Services.GetRequiredService<IOptions<AgeVerificationOptions>>().Value);

        Assert.Contains("AgeVerification:Provider=Fake", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task WorkerDoesNotStartWithTheFakeProviderOutsideDevelopmentAndTesting(string environment)
    {
        using var host = BuildWorker(environment, FakeArgs());

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("AgeVerification:Provider", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--AgeVerification:Dispatch:BatchSize=20")]
    [InlineData("--AgeVerification:Dispatch:LeaseDuration=00:01:40")]
    public async Task WorkerDoesNotStartWhenALeaseCannotCoverAWholeBatch(string setting)
    {
        // Lote de 10 com chamada de até 10 s: o lease precisa passar de 100 s, senão outro worker retoma no meio.
        using var host = BuildWorker("Testing", [.. FakeArgs(), setting]);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("AgeVerification:Dispatch", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task WebStartsWithTheFakeProviderInDevelopmentAndTesting(string environment)
    {
        await using var app = BuildWeb(environment, FakeArgs());

        await app.StartAsync(Ct);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task ProductionWithoutAProviderPassesTheAgeVerificationChecks()
    {
        await using var app = BuildWeb("Production");

        var options = app.Services.GetRequiredService<IOptions<AgeVerificationOptions>>().Value;

        Assert.False(options.IsEnabled);
    }

    [Theory]
    [InlineData("--AgeVerification:AllowedFlowOrigins:0=", "AgeVerification:AllowedFlowOrigins")]
    [InlineData("--AgeVerification:AllowedFlowOrigins:0=http://age-check.fake.invalid", "AgeVerification:AllowedFlowOrigins")]
    [InlineData("--AgeVerification:AllowedFlowOrigins:0=https://age-check.fake.invalid/flow", "AgeVerification:AllowedFlowOrigins")]
    [InlineData("--AgeVerification:PolicyVersion=versão inválida", "AgeVerification:PolicyVersion")]
    [InlineData("--AgeVerification:Validity=00:00:00", "AgeVerification:Validity")]
    [InlineData("--AgeVerification:RequestLifetime=-00:01:00", "AgeVerification:RequestLifetime")]
    [InlineData("--AgeVerification:Provider=Outro", "AgeVerification:Provider")]
    [InlineData("--AgeVerification:Fake:SigningSecrets:0=curto", "AgeVerification:Fake:SigningSecrets")]
    public async Task WebDoesNotStartWithInvalidAgeVerificationSettings(string invalidSetting, string expectedKey)
    {
        await using var app = BuildWeb("Testing", [.. FakeArgs(), invalidSetting]);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync(Ct));

        Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDoesNotStartWithMoreThanTwoSigningSecrets()
    {
        // Valor sorteado: nada com cara de segredo fica no repositório (o gitleaks varre o histórico).
        var third = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var app = BuildWeb("Testing", [.. FakeArgs(), $"--AgeVerification:Fake:SigningSecrets:2={third}"]);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync(Ct));

        Assert.Contains("AgeVerification:Fake:SigningSecrets", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAProviderTheRequestIsUnavailableAndNothingIsWritten()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new AgeVerificationScenario(postgres, factory);
        var (account, browser) = await scenario.SignedInPendingAccountAsync();
        using var _ = browser;

        using var response = await AgeVerificationScenario.RequestAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "AGE_VERIFICATION_UNAVAILABLE");
        Assert.Empty(await scenario.VerificationsAsync(account.UserId));
        Assert.Equal("PendingEligibility", await scenario.AccountStatusAsync(account.UserId));
    }

    private static string[] FakeArgs() =>
    [
        .. AgeVerificationScenario.FakeProviderSettings().Select(setting => $"--{setting.Key}={setting.Value}"),
    ];

    private static WebApplication BuildWeb(string environment, params string[] settings) =>
        DuoraWebApp.Build(
        [
            $"--environment={environment}",
            "--urls=http://127.0.0.1:0",
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            $"--Duora:PublicOrigin={DuoraWebFactory.PublicOrigin}",
            $"--DataProtection:KeysDirectory={Path.Combine(Path.GetTempPath(), "duora-keys-unused")}",
            // Como o DuoraWebFactory: volume e certificado do key ring só valem dentro do container (#34). Fora de
            // Development e Testing o próprio boot recusa estes dois valores.
            "--DataProtection:RequireMountedVolume=false",
            "--DataProtection:RequireCertificate=false",
            .. settings,
        ]);

    private static Microsoft.Extensions.Hosting.IHost BuildWorker(string environment, params string[] settings) =>
        WorkerHost.CreateBuilder(
        [
            "--environment", environment,
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. settings,
        ]).Build();
}
