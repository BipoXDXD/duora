namespace Duora.IntegrationTests;

public sealed class ComposeFileTests
{
    [Fact]
    public void DevComposeUsesTheSamePostgresImageAsTheTests()
    {
        var compose = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "infra", "compose", "compose.dev.yml"));

        Assert.Contains($"image: {PostgresFixture.Image}", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPostgresImageUsesTheSameVersionAsTheTests()
    {
        var dockerfile = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "infra", "images", "postgres", "Dockerfile"));

        // Tag com variante do Debian e digest: "FROM postgres:18.6-trixie@sha256:...".
        Assert.Contains($"FROM {PostgresFixture.Image}-", dockerfile, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"global.json não encontrado acima de {AppContext.BaseDirectory}.");
    }
}
