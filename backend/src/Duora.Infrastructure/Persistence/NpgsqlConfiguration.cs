using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Persistence;

/// <summary>Configuração do provider compartilhada pela aplicação e pelo dotnet-ef em tempo de design.</summary>
internal static class NpgsqlConfiguration
{
    public static DbContextOptionsBuilder UseDuoraNpgsql(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(DatabaseSchemas.MigrationsHistoryTable, DatabaseSchemas.Ops));
}
