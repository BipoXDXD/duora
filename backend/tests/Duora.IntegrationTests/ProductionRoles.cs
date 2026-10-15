using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests;

/// <summary>
/// Banco com os papéis de produção: dono <c>duora_migrator</c>, que aplica as migrations, e <c>duora_app</c>, só com
/// os default privileges. Reproduz o trecho SQL de <c>infra/images/postgres/initdb/10-duora-roles.sh</c>; se aquele
/// script mudar, este muda junto.
/// </summary>
internal sealed class ProductionRoles : IAsyncDisposable
{
    private const string Migrator = "duora_migrator";
    private const string App = "duora_app";

    private readonly string _appConnectionString;

    private ProductionRoles(string appConnectionString) => _appConnectionString = appConnectionString;

    public static async Task<ProductionRoles> CreateMigratedDatabaseAsync(
        PostgresFixture postgres,
        CancellationToken cancellationToken)
    {
        // Senha descartável por execução: os papéis existem só no container do teste.
        var password = Guid.NewGuid().ToString("N");
        var database = $"roles_{Guid.NewGuid():N}";
        await using (var server = new NpgsqlConnection(postgres.ConnectionString))
        {
            await server.OpenAsync(cancellationToken);
            // Papéis valem para o servidor inteiro; criados uma vez e reaproveitados pelos outros testes desta classe.
            // Identificadores e senha são gerados aqui (hex), não vêm de fora; DDL não aceita parâmetro.
            await ExecuteAsync(server, $"""
                do $$ begin
                  if not exists (select from pg_roles where rolname = '{Migrator}') then
                    create role {Migrator} login;
                  end if;
                  if not exists (select from pg_roles where rolname = '{App}') then
                    create role {App} login;
                  end if;
                end $$;
                alter role {Migrator} password '{password}';
                alter role {App} password '{password}';
                """, cancellationToken);
            await ExecuteAsync(server, $"create database {database} owner {Migrator}", cancellationToken);
            await ExecuteAsync(server, $"revoke all on database {database} from public", cancellationToken);
            await ExecuteAsync(server, $"grant connect on database {database} to {App}", cancellationToken);
        }

        var superuserOnDatabase = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database };
        await using (var connection = new NpgsqlConnection(superuserOnDatabase.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, $"""
                revoke all on schema public from public;
                alter default privileges for role {Migrator} grant usage on schemas to {App};
                alter default privileges for role {Migrator} grant select, insert, update, delete on tables to {App};
                alter default privileges for role {Migrator} grant usage, select on sequences to {App};
                alter default privileges for role {Migrator} revoke execute on functions from public;
                alter default privileges for role {Migrator} grant execute on functions to {App};
                """, cancellationToken);
        }

        var migrator = new NpgsqlConnectionStringBuilder(superuserOnDatabase.ConnectionString)
        {
            Username = Migrator,
            Password = password,
        };
        await using (var dbContext = PostgresFixture.CreateDbContext(migrator.ConnectionString))
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        var app = new NpgsqlConnectionStringBuilder(migrator.ConnectionString) { Username = App };
        return new ProductionRoles(app.ConnectionString);
    }

    public async Task<NpgsqlConnection> OpenAsAppAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
