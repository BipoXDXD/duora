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
}
