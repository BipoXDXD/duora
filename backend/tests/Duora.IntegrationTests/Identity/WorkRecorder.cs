using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Registra o trabalho caro de uma requisição: o texto de cada comando SQL do EF (sem valores) e cada hash de
/// senha. Serve para provar que dois caminhos que precisam ser indistinguíveis fazem o mesmo trabalho, sem
/// medir tempo (teste estatístico de tempo seria instável).
/// </summary>
internal sealed partial class WorkRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _work = new();

    public IReadOnlyList<string> Snapshot() => [.. _work];

    public void Clear() => _work.Clear();

    public void Install(IServiceCollection services)
    {
        services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(this));
        services.AddScoped<IPasswordHasher<DuoraUser>>(_ => new CountingHasher(this));
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    // Nomes de parâmetro do EF podem carregar índices de lote; o que importa é o formato do comando.
    private void Record(DbCommand command) =>
        _work.Enqueue("sql: " + ParameterName().Replace(Whitespace().Replace(command.CommandText, " "), "@p"));

    private void RecordHash(string operation) => _work.Enqueue("hash: " + operation);

    [GeneratedRegex(@"@\w+")]
    private static partial Regex ParameterName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class CountingHasher(WorkRecorder recorder) : IPasswordHasher<DuoraUser>
    {
        private readonly PasswordHasher<DuoraUser> _inner = new();

        public string HashPassword(DuoraUser user, string password)
        {
            recorder.RecordHash("hash");
            return _inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword)
        {
            recorder.RecordHash("verify");
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
