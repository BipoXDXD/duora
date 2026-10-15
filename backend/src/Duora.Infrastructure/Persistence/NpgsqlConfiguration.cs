using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Duora.Infrastructure.Persistence;

/// <summary>Configuração do provider compartilhada pela aplicação e pelo dotnet-ef em tempo de design.</summary>
internal static class NpgsqlConfiguration
{
    public static DbContextOptionsBuilder UseDuoraNpgsql(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, Configure);

    public static DbContextOptionsBuilder UseDuoraNpgsql(this DbContextOptionsBuilder builder, NpgsqlDataSource dataSource) =>
        builder.UseNpgsql(dataSource, Configure);

    private static void Configure(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(DatabaseSchemas.MigrationsHistoryTable, DatabaseSchemas.Ops);
}
