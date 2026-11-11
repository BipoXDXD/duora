namespace Duora.IntegrationTests;

/// <summary>Largada assíncrona dos testes de concorrência (#459): ninguém sai antes de todos chegarem, e ninguém prende thread.</summary>
public sealed class StartingGateTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NobodyStartsBeforeTheLastParticipantArrives()
    {
        var gate = new StartingGate(3);

        var first = gate.ArriveAndWaitAsync(Ceiling, Ct);
        var second = gate.ArriveAndWaitAsync(Ceiling, Ct);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        await gate.ArriveAndWaitAsync(Ceiling, Ct);
        await Task.WhenAll(first, second).WaitAsync(Ceiling, Ct);
    }

    [Fact]
    public async Task TheLastArrivalReleasesEveryone()
    {
        var gate = new StartingGate(3);

        var first = gate.ArriveAndWaitAsync(Ceiling, Ct);
        var second = gate.ArriveAndWaitAsync(Ceiling, Ct);
        var last = gate.ArriveAndWaitAsync(Ceiling, Ct);

        await Task.WhenAll(first, second, last).WaitAsync(Ceiling, Ct);
    }

    [Fact]
    public async Task ASingleParticipantStartsAtOnce()
    {
        var gate = new StartingGate(1);

        await gate.ArriveAndWaitAsync(Ceiling, Ct).WaitAsync(Ceiling, Ct);
    }

    [Fact]
    public async Task MissingParticipantsFailWithACountInsteadOfWaitingForever()
    {
        var gate = new StartingGate(3);

        var first = gate.ArriveAndWaitAsync(Short, Ct);
        var second = gate.ArriveAndWaitAsync(Short, Ct);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => first);
        Assert.Contains("2 de 3", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TimeoutException>(() => second);
    }

    [Fact]
    public async Task AnArrivalBeyondTheParticipantCountIsATestBug()
    {
        var gate = new StartingGate(1);
        await gate.ArriveAndWaitAsync(Ceiling, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ArriveAndWaitAsync(Ceiling, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ParticipantCountMustBePositive(int participants)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StartingGate(participants));
    }

    [Fact]
    public async Task CancellationStopsTheWait()
    {
        var gate = new StartingGate(2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var waiting = gate.ArriveAndWaitAsync(Ceiling, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
