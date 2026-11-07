namespace Duora.IntegrationTests;

/// <summary>
/// Relógio que explode numa leitura escolhida, contada a partir de <see cref="ExplodeOnRead"/>, para o teste derrubar
/// um passo exato de um fluxo real. Fora dessa leitura, delega ao relógio do teste.
/// </summary>
internal sealed class ScriptedClock(TimeProvider inner) : TimeProvider
{
    private int _reads;
    private int _target;

    public const string MessageCanary = "CANARY-mensagem-do-relogio";

    public void ExplodeOnRead(int read)
    {
        Volatile.Write(ref _reads, 0);
        Volatile.Write(ref _target, read);
    }

    public override DateTimeOffset GetUtcNow() =>
        Interlocked.Increment(ref _reads) == Volatile.Read(ref _target) ? throw new ScriptedClockException() : inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}

internal sealed class ScriptedClockException() : Exception(ScriptedClock.MessageCanary);
