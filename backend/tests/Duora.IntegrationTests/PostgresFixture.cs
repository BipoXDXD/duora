using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Duora.IntegrationTests.PostgresFixture))]

namespace Duora.IntegrationTests;

/// <summary>
/// Um PostgreSQL descartável por execução da suíte, na mesma versão fixada no Compose de desenvolvimento
/// (garantido por <see cref="ComposeFileTests"/>). As migrations são aplicadas aqui, como fará o job migrador;
/// a aplicação nunca migra no startup.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18.6";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal DuoraDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(ConnectionString);
        return new DuoraDbContext(options.Options);
    }

    /// <summary>Cria um banco vazio, sem migrations, no mesmo servidor e devolve a connection string dele.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"empty_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        // Identificador gerado aqui (prefixo + hex), não vem de fora; CREATE DATABASE não aceita parâmetro.
        await using var command = new NpgsqlCommand($"create database {name}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}
