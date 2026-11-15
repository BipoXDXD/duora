using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Duora.IntegrationTests;

/// <summary>
/// Etapa de limpeza de fixture com duração medida. Na CI a falha de limpeza reprova a suíte inteira sem dizer por quê;
/// esta mensagem leva a etapa, quanto ela durou e o estado registrado antes, para a próxima falha apontar a causa.
/// </summary>
internal static partial class FixtureCleanup
{
    /// <summary>
    /// Inventário com teto e, depois dele, a remoção, que roda mesmo quando o inventário falha ou estoura. A remoção que
    /// dá certo vai para <paramref name="record"/> com a duração; a que falha leva a duração na exceção.
    /// </summary>
    internal static async ValueTask RemoveAfterInventoryAsync(string step, Func<CancellationToken, Task<string>> inventory,
        TimeSpan inventoryCeiling, Func<ValueTask> removal, Action<string> record)
    {
        var stateBefore = await InventoryWithinCeilingAsync(inventory, inventoryCeiling);
        var elapsed = await RunAsync(step, removal, stateBefore);
        record($"Limpeza \"{step}\" levou {Seconds(elapsed)} s ({stateBefore}).");
    }

    internal static async ValueTask<TimeSpan> RunAsync(string step, Func<ValueTask> action, string stateBefore)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action();
        }
        catch (Exception failure)
        {
            throw new InvalidOperationException(
                $"Limpeza \"{step}\" falhou depois de {Seconds(Stopwatch.GetElapsedTime(started))} s ({stateBefore}).", failure);
        }

        return Stopwatch.GetElapsedTime(started);
    }

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// A contagem de arquivos só vale com um número e com saída zero, ou com saída 1 em que todo erro do
    /// <c>find</c> é de arquivo ou diretório que sumiu durante a varredura. Os testes criam e apagam bancos em
    /// paralelo, e o que sumiu já não está no diretório, então não contá-lo é o resultado certo. Qualquer outro
    /// erro, como permissão ou E/S, ou uma saída 1 sem erro que a explique, não pode virar uma contagem parcial.
    /// </summary>
    internal static string DescribeFileCount(long? exitCode, string stdout, string stderr)
    {
        if (exitCode is not 0 && !(exitCode is 1 && OnlyVanishedEntries(stderr)))
        {
            return $"indisponível (saída {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "desconhecida"})";
        }

        return long.TryParse(stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count.ToString(CultureInfo.InvariantCulture)
            : $"indisponível (saída {exitCode?.ToString(CultureInfo.InvariantCulture)}, contagem ilegível)";
    }

    private static bool OnlyVanishedEntries(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(line => VanishedEntry().IsMatch(line));
    }

    /// <summary>Mensagem do GNU find com <c>LC_ALL=C</c> para entrada que sumiu entre a listagem e a leitura.</summary>
    [GeneratedRegex("^find: .+: No such file or directory$")]
    private static partial Regex VanishedEntry();

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
