namespace Duora.UnitTests.Experiences;

/// <summary>Relógio controlado pelo teste, para não depender da hora real nem de pacote extra.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan delta) => _now += delta;
}
