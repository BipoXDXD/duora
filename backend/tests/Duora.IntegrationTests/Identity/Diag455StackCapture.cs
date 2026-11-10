using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Diagnóstico temporário da issue #455 (PR de diagnóstico, nunca mesclado). Na parada do thread pool, grava um dump
/// do próprio processo pelo diagnostic server do runtime (createdump) e lê as pilhas gerenciadas de todas as threads com
/// ClrMD, agrupando as idênticas. O dump é apagado depois da leitura; só o texto sai.
/// </summary>
internal static class Diag455StackCapture
{
    private const int MaxCapturesPerProcess = 2;
    private const int MaxFramesPerThread = 45;
    private const int MaxSummaryChars = 60_000;
    private const string ForceVariable = "DIAG455_FORCE_STACK_CAPTURE";
    private const int PoolWorkerState = 0x01000000;

    private static int _captures;

    /// <summary>Verdadeiro quando a variável de ambiente pede uma captura forçada, para validar a ferramenta.</summary>
    public static bool Forced => Environment.GetEnvironmentVariable(ForceVariable) == "1";

    /// <summary>
    /// Captura as pilhas uma vez por chamada até o teto do processo. Devolve as linhas do relatório, ou vazio quando o
    /// teto já foi atingido. Nunca lança: falha da captura vira linha do relatório.
    /// </summary>
    public static IReadOnlyList<string> TryCapture(string reason)
    {
        if (Interlocked.Increment(ref _captures) > MaxCapturesPerProcess)
        {
            return [];
        }

        var lines = new List<string> { "capture reason=" + reason };
        var path = DumpPath();
        try
        {
            var started = Stopwatch.GetTimestamp();
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Normal, path, WriteDumpFlags.None);
            var dumpMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"dump written in {dumpMs:F0}ms size={new FileInfo(path).Length / 1024}KB"));
            lines.AddRange(ReadStacks(path));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Diagnóstico não pode derrubar o teste: a falha da captura é o próprio resultado a registrar.
            lines.Add("capture failed: " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            File.Delete(path);
        }

        AppendStepSummary(lines);
        return lines;
    }

    private static List<string> ReadStacks(string path)
    {
        using var target = DataTarget.LoadDump(path);
        using var runtime = target.ClrVersions[0].CreateRuntime();

        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var thread in runtime.Threads)
        {
            if (!thread.IsAlive)
            {
                continue;
            }

            var frames = thread.EnumerateStackTrace().Take(MaxFramesPerThread).Select(Describe).ToList();
            if (frames.Count == 0)
            {
                continue;
            }

            var key = string.Join("\n", frames);
            if (!groups.TryGetValue(key, out var threads))
            {
                threads = [];
                groups[key] = threads;
            }
            threads.Add(string.Create(CultureInfo.InvariantCulture,
                $"{thread.ManagedThreadId}/os{thread.OSThreadId:x}{(IsPoolWorker(thread) ? "/pool" : "")}"));
        }

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"clr={target.ClrVersions[0].Version} threadsWithStacks={groups.Values.Sum(t => t.Count)} distinctStacks={groups.Count}"),
        };
        var index = 0;
        foreach (var (stack, threads) in groups.OrderByDescending(g => g.Value.Count))
        {
            index++;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"stack#{index} count={threads.Count} threads=[{string.Join(", ", threads)}]"));
            lines.AddRange(stack.Split('\n').Select(frame => "    " + frame));
        }
        return lines;
    }

    // TS_TPWorkerThread do runtime: a thread é worker do thread pool.
    private static bool IsPoolWorker(ClrThread thread) => ((int)thread.State & PoolWorkerState) != 0;

    private static string Describe(ClrStackFrame frame)
    {
        if (frame.Method is not { } method)
        {
            return "[" + (frame.FrameName ?? frame.Kind.ToString()) + "]";
        }

        var signature = method.Signature ?? method.Type?.Name + "." + method.Name;
        var il = method.GetILOffset(frame.InstructionPointer);
        return string.Create(CultureInfo.InvariantCulture, $"{signature} +IL_{il:X4}");
    }

    private static string DumpPath()
    {
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var directory = string.IsNullOrEmpty(runnerTemp) ? Path.GetTempPath() : runnerTemp;
        return Path.Combine(directory, string.Create(CultureInfo.InvariantCulture,
            $"diag455-{Environment.ProcessId}-{DateTime.UtcNow:HHmmssfff}.dmp"));
    }

    private static void AppendStepSummary(List<string> lines)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var text = new StringBuilder()
            .AppendLine("<details><summary>diag455 stacks pid " +
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "</summary>")
            .AppendLine()
            .AppendLine("```")
            .AppendJoin(Environment.NewLine, lines)
            .ToString();
        if (text.Length > MaxSummaryChars)
        {
            text = text[..MaxSummaryChars] + Environment.NewLine + "... (truncated)";
        }
        File.AppendAllText(path, text + Environment.NewLine + "```" + Environment.NewLine + "</details>" +
            Environment.NewLine);
    }
}
