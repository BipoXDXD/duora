using Npgsql;

namespace Duora.IntegrationTests;

/// <summary>
/// O servidor de teste tem folga de conexões para a suíte em paralelo: com o padrão de 100, a suíte inteira chegava a
/// 95 conexões e os testes de concorrência do MFA recebiam 53300 (slots reservados ao superusuário) como 500.
/// </summary>
public sealed class TestServerCapacityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task TheTestServerAllowsTheConfiguredNumberOfConnections()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("show max_connections", connection);

        Assert.Equal(
            PostgresFixture.MaxConnections.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
