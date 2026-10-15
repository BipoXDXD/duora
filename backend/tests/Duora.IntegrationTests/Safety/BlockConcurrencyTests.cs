using System.Net;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Bloqueio concorrente (concurrency.md C2; spec 10.3): a guarda das duas pessoas serializa os pedidos e o
/// `UNIQUE (blocker_id, blocked_id)` é a última defesa. Sem check-then-insert.
/// </summary>
public sealed class BlockConcurrencyTests(PostgresFixture postgres) : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>
    /// Barreira determinística: um terceiro tranca as guardas, os dois pedidos ficam esperando por ele, e só então
    /// ele solta. Os dois disputam o mesmo instante e só um efeito sobra.
    /// </summary>
    [Fact]
    public async Task TwoSimultaneousIdenticalBlocksLeaveOneRowOneAuditAndOneVersionBump()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var blocked = await scenario.CreateAccountAsync();
        using var tabA = await scenario.SignedInBrowserAsync(blocker);
        using var tabB = await scenario.SignedInBrowserAsync(blocker);
        await using var holder = await HoldGuardsAsync(blocker.UserId, blocked.UserId);

        var first = tabA.PutAsync(SafetyScenario.BlockPath(blocked.UserId));
        var second = tabB.PutAsync(SafetyScenario.BlockPath(blocked.UserId));
        await WaitUntilBlockedByAsync(holder, expectedWaiters: 2);
        await holder.ReleaseAsync();
        using var firstResponse = await first;
        using var secondResponse = await second;

        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);
        Assert.Equal(new BlockState(1, 1, 1, 1), await scenario.ReadAsync(blocker.UserId, blocked.UserId));
    }

    /// <summary>A bloqueia B enquanto B bloqueia A: guardas na mesma ordem, sem deadlock, dois registros.</summary>
    [Fact]
    public async Task CrossedBlocksDoNotDeadlockAndBothAreRecorded()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var ana = await scenario.CreateAccountAsync();
        var bruno = await scenario.CreateAccountAsync();
        using var anaBrowser = await scenario.SignedInBrowserAsync(ana);
        using var brunoBrowser = await scenario.SignedInBrowserAsync(bruno);
        await using var holder = await HoldGuardsAsync(ana.UserId, bruno.UserId);

        var anaBlocks = anaBrowser.PutAsync(SafetyScenario.BlockPath(bruno.UserId));
        var brunoBlocks = brunoBrowser.PutAsync(SafetyScenario.BlockPath(ana.UserId));
        await WaitUntilBlockedByAsync(holder, expectedWaiters: 2);
        await holder.ReleaseAsync();
        using var anaResponse = await anaBlocks;
        using var brunoResponse = await brunoBlocks;

        Assert.Equal(HttpStatusCode.NoContent, anaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, brunoResponse.StatusCode);
        Assert.Equal(new BlockState(1, 1, 2, 2), await scenario.ReadAsync(ana.UserId, bruno.UserId));
        Assert.Equal(new BlockState(1, 1, 2, 2), await scenario.ReadAsync(bruno.UserId, ana.UserId));
    }

    /// <summary>Corrida livre repetida, para interleavings que a barreira não prevê (concurrency.md, item 2).</summary>
    [Fact]
    public async Task RepeatedFreeRacesAlwaysLeaveOneRow()
    {
        const int Rounds = 10;
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        using var tabA = await scenario.SignedInBrowserAsync(blocker);
        using var tabB = await scenario.SignedInBrowserAsync(blocker);
        for (var round = 0; round < Rounds; round++)
        {
            var blocked = await scenario.CreateAccountAsync();
            var responses = await Task.WhenAll(
                tabA.PutAsync(SafetyScenario.BlockPath(blocked.UserId)),
                tabB.PutAsync(SafetyScenario.BlockPath(blocked.UserId)));

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            // A versão de quem bloqueia sobe uma vez por pessoa bloqueada; a do bloqueado, uma vez só.
            Assert.Equal(new BlockState(1, 1, round + 1, 1), await scenario.ReadAsync(blocker.UserId, blocked.UserId));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<GuardHolder> HoldGuardsAsync(Guid personA, Guid personB)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (var create = new NpgsqlCommand(
            "insert into safety.user_guards (user_id) select unnest($1::uuid[]) on conflict do nothing", connection))
        {
            create.Parameters.AddWithValue(new[] { personA, personB });
            await create.ExecuteNonQueryAsync(Ct);
        }

        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var lockGuards = new NpgsqlCommand(
            "select user_id from safety.user_guards where user_id = any($1) order by user_id for update",
            connection,
            transaction))
        {
            lockGuards.Parameters.AddWithValue(new[] { personA, personB });
            await lockGuards.ExecuteNonQueryAsync(Ct);
        }

        return new GuardHolder(connection, transaction);
    }

    private async Task WaitUntilBlockedByAsync(GuardHolder holder, int expectedWaiters)
    {
        await using var observer = new NpgsqlConnection(postgres.ConnectionString);
        await observer.OpenAsync(Ct);
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            // O segundo pedido espera pelo primeiro (lock da tupla), não pelo terceiro: conta quem está parado no
            // lock das guardas, seja qual for o elo da fila.
            await using var command = new NpgsqlCommand(
                """
                select count(*) from pg_stat_activity
                where pid <> $1 and wait_event_type = 'Lock' and cardinality(pg_blocking_pids(pid)) > 0
                  and query like '%safety.user_guards%for update%'
                """,
                observer);
            command.Parameters.AddWithValue(holder.ProcessId);
            if ((long)(await command.ExecuteScalarAsync(Ct) ?? 0L) >= expectedWaiters)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Os {expectedWaiters} pedidos não chegaram ao lock das guardas.");
            await Task.Yield();
        }
    }

    private sealed class GuardHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public int ProcessId => connection.ProcessID;

        public Task ReleaseAsync() => transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
