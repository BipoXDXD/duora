using System.Net;
using Duora.Core.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Faz o tempo passar sobre uma prova de step-up sem deixar a sessão morrer. A janela de autenticação recente
/// (<see cref="RecentAuthentication.Window"/>, 1 h) é maior que a inatividade das sessões (15 min administrativa,
/// 30 min normal): a prova só chega ao fim da janela se a sessão continuar em uso. Por isso o relógio avança em
/// passos menores que a menor inatividade, com uma leitura autenticada entre eles.
/// </summary>
internal static class StepUpClock
{
    private const string KeepAlive = "/api/v1/me";

    /// <summary>Menor que a inatividade da sessão administrativa, a mais curta.</summary>
    private static readonly TimeSpan ActivityInterval = SessionLifetime.Administrative.Idle - TimeSpan.FromMinutes(5);

    /// <summary>Primeiro instante em que uma prova feita agora deixa de valer: fim da janela mais 1 tick.</summary>
    public static Task ExpireRecentAuthenticationAsync(Browser browser, FakeTimeProvider time) =>
        AdvanceKeepingTheSessionAliveAsync(browser, time, RecentAuthentication.Window + TimeSpan.FromTicks(1));

    /// <summary>Último instante em que uma prova feita agora ainda vale: o fim da janela, inclusive.</summary>
    public static Task ReachTheEndOfTheWindowAsync(Browser browser, FakeTimeProvider time) =>
        AdvanceKeepingTheSessionAliveAsync(browser, time, RecentAuthentication.Window);

    private static async Task AdvanceKeepingTheSessionAliveAsync(Browser browser, FakeTimeProvider time, TimeSpan total)
    {
        var remaining = total;
        while (remaining > ActivityInterval)
        {
            time.Advance(ActivityInterval);
            remaining -= ActivityInterval;
            using var activity = await browser.GetAsync(KeepAlive);
            Assert.NotEqual(HttpStatusCode.Unauthorized, activity.StatusCode);
        }

        time.Advance(remaining);
    }
}
