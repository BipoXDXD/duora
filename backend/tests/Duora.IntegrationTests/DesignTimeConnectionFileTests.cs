using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.IntegrationTests;

/// <summary>
/// O bundle de migrations usa o DesignTimeDbContextFactory. Em produção a connection string do migrador vem do
/// arquivo apontado por DUORA_MIGRATOR_CONNECTION_FILE, para não passar em --connection (visível na lista de
/// processos do host).
/// </summary>
public sealed class DesignTimeConnectionFileTests(PostgresFixture postgres) : IDisposable
{
    private const string Variable = DesignTimeDbContextFactory.ConnectionFileVariable;

    private readonly DirectoryInfo _secrets = Directory.CreateTempSubdirectory("duora-migrator-");

    public void Dispose() => _secrets.Delete(recursive: true);

    [Fact]
    public void UsesConnectionStringFromFileWithoutTrailingNewline()
    {
        var file = WriteSecret("Host=postgres;Database=duora;Username=duora_migrator;Password=synthetic\n");

        using var context = CreateFactory(file).CreateDbContext([]);

        Assert.Equal("Host=postgres;Database=duora;Username=duora_migrator;Password=synthetic",
            context.Database.GetConnectionString());
    }

    [Fact]
    public async Task MigratesDatabaseWithConnectionStringFromFile()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);
        var file = WriteSecret(connectionString);

        await using var context = CreateFactory(file).CreateDbContext([]);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingFileFailsNamingTheVariable()
    {
        var factory = CreateFactory(Path.Combine(_secrets.FullName, "nao-existe"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateDbContext([]));

        Assert.Contains(Variable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public void EmptyFileFails(string content)
    {
        var factory = CreateFactory(WriteSecret(content));

        Assert.Throws<InvalidOperationException>(() => factory.CreateDbContext([]));
    }

    [Fact]
    public void WithoutVariableUsesPlaceholderSoMigrationsAddNeedsNoCredential()
    {
        var factory = new DesignTimeDbContextFactory(_ => null);

        using var context = factory.CreateDbContext([]);

        Assert.DoesNotContain("Password", context.Database.GetConnectionString(), StringComparison.OrdinalIgnoreCase);
    }

    private static DesignTimeDbContextFactory CreateFactory(string file) =>
        new(name => name == Variable ? file : null);

    private string WriteSecret(string content)
    {
        var path = Path.Combine(_secrets.FullName, "duora_migrator_connection");
        File.WriteAllText(path, content);
        return path;
    }
}
