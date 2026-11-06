using System.Buffers.Binary;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Participation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Os hosts de teste derivam senhas com o custo mínimo do mesmo PBKDF2. Estes testes prendem as duas pontas: a composição
/// de produção segue com o custo real, e nenhum outro atalho entrou junto.
/// </summary>
public sealed class PasswordHashCostTests(PostgresFixture postgres)
{
    private const int ProductionIterations = 100_000;
    private const int FastIterations = 1;
    private const int HmacSha512 = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheProductionCompositionStoresPasswordsAtTheRealCost()
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
