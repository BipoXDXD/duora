namespace Duora.Infrastructure.Persistence;

/// <summary>Nomes físicos dos schemas e das tabelas de controle (spec 10.1).</summary>
internal static class DatabaseSchemas
{
    public const string Identity = "identity";

    public const string Media = "media";

    public const string Ops = "ops";

    public const string Profiles = "profiles";

    public const string Safety = "safety";

    public const string MigrationsHistoryTable = "__ef_migrations_history";
}
