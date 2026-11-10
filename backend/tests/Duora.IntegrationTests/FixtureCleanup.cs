using System.Diagnostics;
using System.Globalization;

namespace Duora.IntegrationTests;

/// <summary>
/// Etapa de limpeza de fixture com duração medida. Na CI a falha de limpeza reprova a suíte inteira sem dizer por quê;
/// esta mensagem leva a etapa, quanto ela durou e o estado registrado antes, para a próxima falha apontar a causa.
/// </summary>
internal static class FixtureCleanup
{
    internal static async ValueTask RunAsync(string step, Func<ValueTask> action, string stateBefore)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action();
        }
        catch (Exception failure)
        {
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            throw new InvalidOperationException($"Limpeza \"{step}\" falhou depois de {seconds} s ({stateBefore}).", failure);
        }
    }

    /// <summary>
    /// O inventário dentro de um teto rígido: o token avisa quem respeita cancelamento, e o <c>WaitAsync</c> solta a
    /// limpeza mesmo que algo ignore o token. Rodar em <c>Task.Run</c> cobre também um bloqueio síncrono antes da
    /// primeira espera. Falha ou estouro viram texto: sem inventário a remoção ainda precisa rodar.
    /// </summary>
    internal static async Task<string> InventoryWithinCeilingAsync(Func<CancellationToken, Task<string>> inventory,
        TimeSpan ceiling)
    {
        using var cancellation = new CancellationTokenSource(ceiling);
        var running = Task.Run(() => inventory(cancellation.Token), CancellationToken.None);
        try
        {
            return await running.WaitAsync(ceiling, CancellationToken.None);
        }
        catch (Exception failure)
        {
            // A tarefa abandonada pelo teto pode falhar depois; a leitura da exceção evita que ela suba como não observada.
            _ = running.ContinueWith(static abandoned => _ = abandoned.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return $"inventário indisponível: {failure.GetType().Name}: {failure.Message}";
        }
    }
}
