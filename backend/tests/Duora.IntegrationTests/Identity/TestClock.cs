using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

internal static class TestClock
{
    /// <summary>
    /// Relógio falso que parte do instante real (as chaves de Data Protection não nascem "no futuro"), truncado ao
    /// segundo: o PostgreSQL guarda microssegundos, e um instante com ticks de 100 ns voltaria do banco diferente.
    /// </summary>
    public static FakeTimeProvider StartingNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new FakeTimeProvider(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)));
    }
}
