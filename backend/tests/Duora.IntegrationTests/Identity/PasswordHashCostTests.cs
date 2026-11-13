using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Participation;
using Duora.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Os hosts de teste derivam senhas com o custo mínimo do mesmo PBKDF2. Estes testes prendem as duas pontas: o ambiente
/// Production segue com o custo real, inclusive na isca, e nenhum outro atalho entrou junto.
/// </summary>
public sealed class PasswordHashCostTests(PostgresFixture postgres)
{
    private const int ProductionIterations = 100_000;
    private const int FastIterations = 1;
    private const int HmacSha512 = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Composição do ambiente Production, sem subir o servidor: o boot exige um volume montado, que só existe no container.
    /// </summary>
    [Fact]
    public async Task TheProductionEnvironmentHashesPasswordsAndTheDecoyAtTheRealCost()
    {
        await using var app = DuoraWebApp.Build(
        [
            "--environment=Production",
            "--urls=http://127.0.0.1:0",
            "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x",
            $"--Duora:PublicOrigin={DuoraWebFactory.PublicOrigin}",
            $"--DataProtection:KeysDirectory={Path.Combine(Path.GetTempPath(), "duora-keys-unused")}",
            $"--DataProtection:Certificate={TestCertificates.Create("custo-do-hash").Pem}",
        ]);
        await using var scope = app.Services.CreateAsyncScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<DuoraUser>>();
        var capture = new CapturingHasher();

        scope.ServiceProvider.GetRequiredService<DecoyPasswordHash>().Verify(capture, IdentityScenario.ValidPassword);

        Assert.True(app.Environment.IsProduction());
        Assert.Equal((HmacSha512, ProductionIterations), Parameters(hasher.HashPassword(null!, IdentityScenario.ValidPassword)));
        Assert.Equal((HmacSha512, ProductionIterations), Parameters(Assert.Single(capture.Verified)));
    }

    [Theory]
    [InlineData(true, ProductionIterations)]
    [InlineData(false, FastIterations)]
    public async Task TheDecoyOfALoginWithoutAccountCostsTheSameAsAStoredPassword(bool realCost, int iterations)
    {
        var capture = new CapturingHasher();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            RealPasswordHashCost = realCost,
            TestServices = services => services.AddSingleton<IPasswordHasher<DuoraUser>>(capture),
        };
        using var browser = new Browser(factory);

        using var response = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), IdentityScenario.ValidPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal((HmacSha512, iterations), Parameters(Assert.Single(capture.Verified)));
    }

    [Fact]
    public async Task TheCompositionWithoutTheTestOverrideStoresPasswordsAtTheRealCost()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { RealPasswordHashCost = true };
        var scenario = new IdentityScenario(postgres, factory);

        var account = await scenario.CreateConfirmedAccountAsync();

        Assert.Equal((HmacSha512, ProductionIterations), Parameters(await StoredHashAsync(scenario, account)));
        using var browser = await scenario.SignedInBrowserAsync(account);
    }

    [Fact]
    public async Task TestHostsKeepTheAlgorithmAndOnlyLowerTheIterations()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);

        var account = await scenario.CreateConfirmedAccountAsync();

        Assert.Equal((HmacSha512, FastIterations), Parameters(await StoredHashAsync(scenario, account)));
        using var browser = await scenario.SignedInBrowserAsync(account);
    }

    [Fact]
    public async Task AHashStoredAtTheRealCostLogsInOnATestHostAndIsNotRewritten()
    {
        await using var real = new DuoraWebFactory(postgres.ConnectionString) { RealPasswordHashCost = true };
        var account = await new IdentityScenario(postgres, real).CreateConfirmedAccountAsync();
        await using var fast = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, fast);
        var stored = await StoredHashAsync(scenario, account);

        using var browser = await scenario.SignedInBrowserAsync(account);

        Assert.Equal(stored, await StoredHashAsync(scenario, account));
    }

    [Fact]
    public async Task EachSeedStoresAVerifiableHashAtTheRealCost()
    {
        await using var first = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        await using var second = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;

        var firstSeed = await BrowserScenarioSeed.InitializeAsync(first.AppConnectionString, now, Ct);
        var secondSeed = await BrowserScenarioSeed.InitializeAsync(second.AppConnectionString, now, Ct);

        foreach (var (database, user) in new[] { (first, firstSeed.Alice), (second, secondSeed.Alice) })
        {
            await using var factory = new DuoraWebFactory(database.AppConnectionString);
            var hash = await new IdentityScenario(database.AppConnectionString, factory).ScalarTextAsync(
                "select password_hash from identity.users where id = $1", user);
            Assert.Equal((HmacSha512, ProductionIterations), Parameters(hash));
            Assert.Equal(PasswordVerificationResult.Success,
                new PasswordHasher<DuoraUser>().VerifyHashedPassword(null!, hash, IdentityScenario.ValidPassword));
        }
    }

    [Fact]
    public async Task TestHostsDoNotWatchTheConfigurationFiles()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);

        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        var files = configuration.Providers.OfType<JsonConfigurationProvider>().ToList();
        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.False(file.Source.ReloadOnChange));
    }

    /// <summary>Guarda o hash que cada verificação recebeu: é o único jeito de ver a isca, que não sai do serviço.</summary>
    private sealed class CapturingHasher : IPasswordHasher<DuoraUser>
    {
        private readonly PasswordHasher<DuoraUser> _inner = new();

        public ConcurrentQueue<string> Verified { get; } = new();

        public string HashPassword(DuoraUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword)
        {
            Verified.Enqueue(hashedPassword);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private static Task<string> StoredHashAsync(IdentityScenario scenario, TestAccount account) =>
        scenario.ScalarTextAsync("select password_hash from identity.users where id = $1", account.UserId);

    // Formato V3 do ASP.NET Core Identity: versão (1 byte), PRF (4) e iterações (4), em big-endian.
    private static (int Prf, int Iterations) Parameters(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(1, bytes[0]);
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4)));
    }
}
