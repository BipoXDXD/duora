using System.Data.Common;
using Npgsql;

namespace Duora.Infrastructure;

/// <summary>
/// Responde se o banco aceita conexão agora. Recusa, falha e lentidão viram <see cref="DatabaseStatus.Unreachable"/>
/// com a causa, sem lançar: quem pergunta (readiness, heartbeat) decide o que fazer, e o worker não cai.
/// </summary>
public sealed class DatabaseProbe
{
    private const int TimeoutSeconds = 3;

    private readonly string _connectionString;

    internal DatabaseProbe(string connectionString)
    {
        // O Npgsql não honra o CancellationToken durante o handshake de startup (um servidor que aceita o TCP e
        // não responde segura o Open pelo Timeout inteiro, 15 s por padrão); o Timeout da conexão é o que corta.
        // Sem pool, toda verificação vai até o servidor: uma conexão ociosa do pool não provaria que ele responde.
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = TimeoutSeconds,
            Pooling = false,
        }.ConnectionString;
    }

    public async Task<DatabaseStatus> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return DatabaseStatus.Reachable;
        }
        catch (DbException exception)
        {
            // Inclui recusa de conexão e o NpgsqlException que embrulha TimeoutException.
            return DatabaseStatus.Unreachable(exception);
        }
    }
}

public sealed record DatabaseStatus
{
    private DatabaseStatus(Exception? failure) => Failure = failure;

    public static DatabaseStatus Reachable { get; } = new(failure: null);

    public bool IsReachable => Failure is null;

    /// <summary>Causa da indisponibilidade; nula quando o banco respondeu.</summary>
    public Exception? Failure { get; }

    public static DatabaseStatus Unreachable(Exception failure) => new(failure);
}
