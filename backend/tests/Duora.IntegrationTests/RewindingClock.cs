namespace Duora.IntegrationTests;

/// <summary>
/// Relógio do processo que recua depois da primeira leitura, como num ajuste de NTP: a primeira leitura devolve o relógio
/// do teste e todas as seguintes devolvem <paramref name="rewoundTo"/>. Serve para separar a leitura que decide de
/// todas as que vêm depois dela num mesmo fluxo. Timers continuam no relógio do teste.
/// </summary>
internal sealed class RewindingClock(TimeProvider inner, DateTimeOffset rewoundTo) : TimeProvider
{
    private int _reads;

    public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) == 1 ? inner.GetUtcNow() : rewoundTo;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}
