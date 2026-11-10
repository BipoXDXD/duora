using System.Diagnostics;
using System.Globalization;

namespace Duora.IntegrationTests;

/// <summary>
/// Etapa de limpeza de fixture com duração medida. Na CI a falha de limpeza reprova a suíte inteira sem dizer por quê;
/// esta mensagem leva a etapa, quanto ela durou e o estado registrado antes, para a próxima falha apontar a causa.
/// </summary>
internal static class FixtureCleanup
{
    /// <summary>Inventário com teto e, depois dele, a remoção, que roda mesmo quando o inventário falha ou estoura.</summary>
    internal static async ValueTask RemoveAfterInventoryAsync(string step, Func<CancellationToken, Task<string>> inventory,
        TimeSpan inventoryCeiling, Func<ValueTask> removal)
    {
        var stateBefore = await InventoryWithinCeilingAsync(inventory, inventoryCeiling);
        await RunAsync(step, removal, stateBefore);
    }

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
    /// A contagem de arquivos só vale com saída zero e um número: o <c>find</c> que falha no meio do pipe não pode virar
    /// uma contagem de zero ou parcial.
    /// </summary>
    internal static string DescribeFileCount(long? exitCode, string stdout)
    {
        if (exitCode is not 0)
        {
            return $"indisponível (saída {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "desconhecida"})";
        }

        return long.TryParse(stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count.ToString(CultureInfo.InvariantCulture)
            : "indisponível (saída 0, contagem ilegível)";
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
