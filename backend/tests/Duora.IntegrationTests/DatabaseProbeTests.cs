using Duora.Infrastructure;

namespace Duora.IntegrationTests;

/// <summary>
/// A sonda promete não lançar: quem pergunta (readiness, heartbeat) decide o que fazer. Só o cancelamento de quem
/// perguntou atravessa, porque aí ninguém espera mais pela resposta.
/// </summary>
public sealed class DatabaseProbeTests
{
    /// <summary>Sem host, o Npgsql recusa com <see cref="ArgumentNullException"/>, que não é uma <c>DbException</c>.</summary>
    private const string ConnectionStringWithoutHost = "Username=duora;Database=duora";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFailureOutsideTheDriverExceptionsIsReportedAsUnreachable()
    {
        var status = await new DatabaseProbe(ConnectionStringWithoutHost).CheckAsync(Ct);

        Assert.False(status.IsReachable);
        Assert.IsType<ArgumentNullException>(status.Failure);
    }

    [Fact]
    public async Task TheCancellationOfWhoeverAskedPassesThrough()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DatabaseProbe(new CanaryConnectionString().Value).CheckAsync(cancelled.Token));
    }
}
