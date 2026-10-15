using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// G3 e TM-24 além da resposta: quem bloqueia não pode distinguir alvo existente de inexistente pelo trabalho que o
/// servidor faz (tempo de resposta). O teste compara a sequência de comandos SQL de cada pedido, que é o que define
/// o tempo, sem depender de medição de relógio no CI.
/// </summary>
public sealed partial class BlockSideChannelTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly CommandRecorder _commands = new();
    private readonly DuoraWebFactory _factory;

    public BlockSideChannelTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(_commands)),
        };
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>
    /// Alvo inexistente (ou o UUID vazio) faz exatamente o mesmo trabalho que um alvo existente já bloqueado: as
    /// mesmas leituras, a mesma transação e os mesmos locks, só com dados diferentes.
    /// </summary>
    [Fact]
    public async Task UnknownTargetRunsTheSameCommandsAsAnExistingAlreadyBlockedTarget()
    {
        var scenario = new SafetyScenario(_postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var existing = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);
        using (await browser.PutAsync(SafetyScenario.BlockPath(existing.UserId)))
        {
        }

        var existingWork = await RecordAsync(browser, existing.UserId);
        var unknownWork = await RecordAsync(browser, Guid.CreateVersion7());
        var emptyWork = await RecordAsync(browser, Guid.Empty);

        Assert.NotEmpty(existingWork);
        Assert.Contains(existingWork, command => command.Contains("for update", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(existingWork, unknownWork);
        Assert.Equal(existingWork, emptyWork);
    }

    private async Task<List<string>> RecordAsync(Identity.Browser browser, Guid target)
    {
        _commands.Clear();
        using var response = await browser.PutAsync(SafetyScenario.BlockPath(target));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return _commands.Snapshot();
    }

    /// <summary>Texto normalizado de cada comando, na ordem: parâmetros e espaços não contam.</summary>
    private sealed partial class CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public void Clear() => _texts.Clear();

        public List<string> Snapshot() => [.. _texts];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        private void Record(DbCommand command) => _texts.Enqueue(Whitespace().Replace(command.CommandText, " ").Trim());
    }
}
