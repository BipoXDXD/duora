using System.Data.Common;
using System.Net;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Conta do alvo apagada entre a checagem de existência e o upsert da guarda: a FK recusa (`23503`) e a resposta é
/// a mesma de alvo inexistente (`204`, nada gravado), nunca `500`.
/// </summary>
public sealed class BlockTargetDeletedTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly DeleteBeforeGuardUpsert _deleter;
    private readonly DuoraWebFactory _factory;

    public BlockTargetDeletedTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _deleter = new DeleteBeforeGuardUpsert(postgres.ConnectionString);
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(_deleter)),
        };
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task TargetDeletedMidwayAnswersLikeAnUnknownTarget()
    {
        var scenario = new SafetyScenario(_postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var target = await InsertBareAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);
        _deleter.Arm(target);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(target));

        Assert.True(_deleter.Deleted, "O cenário não apagou o alvo antes do upsert.");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await scenario.CountTracesOfAsync(target));
        Assert.Equal(0, await scenario.CountTracesOfAsync(blocker.UserId));
    }

    /// <summary>Conta sem sessões nem aceites, para poder ser apagada sem violar outras FKs.</summary>
    private async Task<Guid> InsertBareAccountAsync()
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into identity.users (id, status, created_at, email_confirmed, phone_number_confirmed,
                                        two_factor_enabled, lockout_enabled, access_failed_count)
            values ($1, 'Active', now(), true, false, false, true, 0)
            """,
            connection);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Apaga a conta armada logo antes do upsert das guardas, numa conexão própria e já commitada.</summary>
    private sealed class DeleteBeforeGuardUpsert(string connectionString) : DbCommandInterceptor
    {
        private Guid? _target;

        public bool Deleted { get; private set; }

        public void Arm(Guid target) => _target = target;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_target is { } target && command.CommandText.Contains("insert into safety.user_guards", StringComparison.Ordinal))
            {
                _target = null;
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var delete = new NpgsqlCommand("delete from identity.users where id = $1", connection);
                delete.Parameters.AddWithValue(target);
                Deleted = await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
