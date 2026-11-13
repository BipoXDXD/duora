using System.Reflection;
using Duora.IntegrationTests;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.ArchitectureTests.Migrations;

/// <summary>
/// Toda operação destrutiva no <c>Up</c> das migrations versionadas tem linha revisada na allowlist, e toda linha da
/// allowlist ainda corresponde a uma operação. Migration nova que apaga dado ou troca constraint só passa com linha
/// nova, visível no review.
/// </summary>
public sealed class RepositoryMigrationsTests
{
    private const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    [Fact]
    public void EveryDestructiveOperationInTheMigrationsIsInTheReviewedAllowlist()
    {
        var allowlist = DestructiveMigrationAllowlist.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), DestructiveMigrationGuard.AllowlistPath)));

        var violations = DestructiveMigrationGuard.FindViolations(RepositoryMigrations(), allowlist);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryMigrationClassHasItsVersionedFile()
    {
        var root = RepositoryRoot.Find();

        var missing = RepositoryMigrations()
            .Select(migration => $"{DestructiveMigrationGuard.MigrationsDirectory}/{migration.Id}.cs")
            .Where(path => !File.Exists(Path.Combine(root, path)))
            .ToList();

        Assert.Empty(missing);
    }

    private static List<MigrationUp> RepositoryMigrations()
    {
        var migrations = Assembly.Load("Duora.Infrastructure").GetTypes()
            .Where(type => type.IsSubclassOf(typeof(Migration)) && !type.IsAbstract)
            .Select(type => type.GetCustomAttribute<MigrationAttribute>() is { } attribute ? UpOf(attribute.Id, type) : null)
            .OfType<MigrationUp>()
            .ToList();

        Assert.NotEmpty(migrations);
        return migrations;
    }

    private static MigrationUp UpOf(string id, Type type)
    {
        var migration = Activator.CreateInstance(type, nonPublic: true) as Migration
            ?? throw new InvalidOperationException($"{type} não instanciou como Migration.");
        migration.ActiveProvider = Provider;
        return new MigrationUp(id, migration.UpOperations);
    }
}
