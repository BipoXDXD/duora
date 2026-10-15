namespace Duora.IntegrationTests;

public sealed class ComposeFileTests
{
    [Fact]
    public void DevComposeUsesTheSamePostgresImageAsTheTests()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "infra", "compose", "compose.dev.yml"));

        Assert.Contains($"image: {PostgresFixture.Image}", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPostgresImageUsesTheSameVersionAsTheTests()
    {
        var dockerfile = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "infra", "images", "postgres", "Dockerfile"));

        // Tag com variante do Debian e digest: "FROM postgres:18.6-trixie@sha256:...".
        Assert.Contains($"FROM {PostgresFixture.Image}-", dockerfile, StringComparison.Ordinal);
    }
}
