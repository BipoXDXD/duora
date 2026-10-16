using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Duora.Infrastructure.Persistence;

/// <summary>
/// Usado pelo dotnet-ef e pelo bundle de migrations. Gerar migration não abre conexão (placeholder sem credencial).
/// Para aplicar, o job migrador aponta <c>DUORA_MIGRATOR_CONNECTION_FILE</c> para o arquivo de segredo: a connection
/// string não passa em <c>--connection</c>, que fica visível na lista de processos do host. Em desenvolvimento,
/// <c>--connection</c> continua valendo e tem precedência.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DuoraDbContext>
{
    public const string ConnectionFileVariable = "DUORA_MIGRATOR_CONNECTION_FILE";

    private const string PlaceholderConnectionString = "Host=localhost;Database=duora";

    private readonly Func<string, string?> _getEnvironmentVariable;

    public DesignTimeDbContextFactory()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal DesignTimeDbContextFactory(Func<string, string?> getEnvironmentVariable) =>
        _getEnvironmentVariable = getEnvironmentVariable;

    public DuoraDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<DuoraDbContext>();
        builder.UseDuoraNpgsql(ResolveConnectionString());
        return new DuoraDbContext(builder.Options);
    }

    private string ResolveConnectionString()
    {
        var file = _getEnvironmentVariable(ConnectionFileVariable);
        if (string.IsNullOrWhiteSpace(file))
        {
            return PlaceholderConnectionString;
        }

        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"{ConnectionFileVariable} aponta para um arquivo inexistente: {file}");
        }

        // Só a quebra de linha final sai (arquivos criados por editor ou echo); o conteúdo nunca vai para a mensagem.
        var connectionString = File.ReadAllText(file).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionFileVariable} aponta para um arquivo vazio: {file}");
        }

        // A fatia do migrador no orçamento de conexões vale mesmo que o arquivo peça mais (deploy.md).
        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = ConnectionShare.Migrator.MaxPoolSize,
            Timeout = (int)DatabaseOptions.DefaultPoolTimeout.TotalSeconds,
        }.ConnectionString;
    }
}
