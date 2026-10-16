namespace Duora.Infrastructure;

/// <summary>
/// Fatia de cada processo no orçamento de conexões do PostgreSQL (spec 10.7; regra SQL 13). O servidor de produção
/// tem <c>max_connections=100</c>; web, worker e migrador somam 55, e o resto cobre a reserva do superusuário, o
/// backup, as sondas de saúde e o acesso administrativo (conta em deploy.md, "Orçamento de conexões").
/// </summary>
public sealed record ConnectionShare
{
    private ConnectionShare(string process, int maxPoolSize)
    {
        Process = process;
        MaxPoolSize = maxPoolSize;
    }

    public static ConnectionShare Web { get; } = new("web", 40);

    public static ConnectionShare Worker { get; } = new("worker", 10);

    /// <summary>Bundle de migrations; usa uma conexão, o resto é folga.</summary>
    public static ConnectionShare Migrator { get; } = new("migrator", 5);

    public string Process { get; }

    /// <summary>Teto e padrão do <c>Maximum Pool Size</c> do processo.</summary>
    public int MaxPoolSize { get; }
}

internal sealed class DatabaseOptions
{
    public const string ConnectionStringName = "Duora";
    public const string Section = "Database";

    /// <summary>Espera padrão por uma conexão do pool (o <c>Timeout</c> do Npgsql, em segundos inteiros).</summary>
    public static readonly TimeSpan DefaultPoolTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaxPoolTimeout = TimeSpan.FromSeconds(30);

    public string ConnectionString { get; set; } = "";

    /// <summary><c>Database:MaxPoolSize</c>; o padrão e o teto vêm da <see cref="ConnectionShare"/> do processo.</summary>
    public int MaxPoolSize { get; set; }

    /// <summary>
    /// <c>Database:PoolTimeout</c>: quanto uma requisição espera por uma conexão (e pela abertura dela) antes de
    /// desistir. Pool esgotado falha rápido e vira 503, em vez de enfileirar requisições até o timeout do comando.
    /// </summary>
    public TimeSpan PoolTimeout { get; set; } = DefaultPoolTimeout;

    public bool FitsIn(ConnectionShare share) =>
        MaxPoolSize >= 1
        && MaxPoolSize <= share.MaxPoolSize
        && PoolTimeout >= TimeSpan.FromSeconds(1)
        && PoolTimeout <= MaxPoolTimeout
        && PoolTimeout.Ticks % TimeSpan.TicksPerSecond == 0;
}
