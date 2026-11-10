using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Diagnóstico temporário da issue #455 (PR de diagnóstico, nunca mesclado). Grava, com relógio monotônico desde o
/// início do teste e hora UTC, os logs do Npgsql dos hosts do teste, o estado do thread pool a cada 250 ms e uma sonda
/// de abertura síncrona de conexão física numa thread dedicada, alternando GSS padrão e <c>GssEncryptionMode=Disable</c>.
/// A saída vai para o stdout e para <c>$RUNNER_TEMP/backend-test-results/diag455-*.diag</c> (artefato em falha).
/// </summary>
internal sealed class Diag455Recorder : IDisposable
{
    private const string Prefix = "[diag455]";
    private const int SamplePeriodMs = 250;
    private const int ProbePeriodMs = 1000;
    private const int ProbeTimeoutSeconds = 15;
    private const int StallThresholdMs = 500;

    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly string _test;
    private readonly Lock _gate = new();
    private readonly StreamWriter? _file;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _sampler;
    private readonly Thread _prober;
    private readonly ILoggerFactory _probeGssLogs;
    private readonly ILoggerFactory _probeNoGssLogs;
    private readonly NpgsqlDataSource _probeGss;
    private readonly NpgsqlDataSource _probeNoGss;
    private int _hosts;
    private bool _closed;

    // Escrito só pelo sampler e lido no Dispose, depois do Join.
    private bool _stallCaptured;

    // Resumo do teste, escrito no Dispose.
    private int _physicalOpens;
    private double _maxPhysicalOpenMs;
    private int _timeouts;
    private long _maxPending;
    private double _maxPoolLagMs;
    private readonly ProbeStats _gssStats = new();
    private readonly ProbeStats _noGssStats = new();

    // Sonda do thread pool: instante em que o item foi enfileirado; 0 quando nenhum está pendente.
    private long _poolProbeQueuedAt;
    private double _lastPoolLagMs;

    public Diag455Recorder(string connectionString)
    {
        _test = TestContext.Current.TestMethod?.MethodName ?? "unknown";
        _file = OpenFile(_test);

        _probeGssLogs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Provider("probe-gss")));
        _probeNoGssLogs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Provider("probe-nogss")));
        var gss = ProbeConnection(connectionString);
        var noGss = ProbeConnection(connectionString);
        noGss.GssEncryptionMode = GssEncryptionMode.Disable;
        _probeGss = new NpgsqlDataSourceBuilder(gss.ConnectionString).UseLoggerFactory(_probeGssLogs).Build();
        _probeNoGss = new NpgsqlDataSourceBuilder(noGss.ConnectionString).UseLoggerFactory(_probeNoGssLogs).Build();

        Write("recorder", string.Create(CultureInfo.InvariantCulture,
            $"start pid={Environment.ProcessId} cpus={Environment.ProcessorCount} loadavg={LoadAverage()} " +
            $"probeGssMode={gss.GssEncryptionMode} probeSslMode={gss.SslMode}"));

        _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "diag455-sampler" };
        _prober = new Thread(ProbeLoop) { IsBackground = true, Name = "diag455-prober" };
        _sampler.Start();
        _prober.Start();
    }

    /// <summary>Provider que grava os logs do Npgsql (Trace) e os avisos/erros da aplicação com o rótulo da origem.</summary>
    public Diag455LoggerProvider Provider(string source) => new(this, source);

    public string NextHostLabel() => "host" + Interlocked.Increment(ref _hosts).ToString(CultureInfo.InvariantCulture);

    public void Record(string source, string category, LogLevel level, EventId eventId, string message,
        Exception? exception, object? state)
    {
        // Só as aberturas dos hosts entram no resumo; as da sonda têm estatística própria.
        var duration = PhysicalOpenDuration(message, state);
        if (duration is { } ms && source.StartsWith("host", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _physicalOpens++;
                _maxPhysicalOpenMs = Math.Max(_maxPhysicalOpenMs, ms);
            }
        }

        if (IsTimeout(exception))
        {
            Interlocked.Increment(ref _timeouts);
        }

        var text = string.Create(CultureInfo.InvariantCulture,
            $"{category}[{eventId.Id}:{eventId.Name}] {level}: {message}{Describe(exception)}");
        Write(source, text);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _sampler.Join();
        _prober.Join();

        string summary;
        lock (_gate)
        {
            summary = string.Create(CultureInfo.InvariantCulture,
                $"test={_test} hostPhysicalOpens={_physicalOpens} maxHostPhysicalOpenMs={_maxPhysicalOpenMs:F1} " +
                $"timeouts={_timeouts} maxPending={_maxPending} maxPoolLagMs={_maxPoolLagMs:F1} " +
                $"probeGss={_gssStats} probeNoGss={_noGssStats}");
        }
        Write("summary", summary);
        AppendStepSummary(summary);

        _probeGss.Dispose();
        _probeNoGss.Dispose();
        _probeGssLogs.Dispose();
        _probeNoGssLogs.Dispose();
        _stop.Dispose();
        lock (_gate)
        {
            _closed = true;
            _file?.Dispose();
        }

        // O passo de testes esconde a saída de teste verde e só sobe o .diag em falha: a parada capturada reprova o
        // teste para as pilhas chegarem ao artefato. Só neste PR de diagnóstico.
        if (_stallCaptured)
        {
            throw new InvalidOperationException(
                "diag455: parada do thread pool capturada; pilhas no stdout e no diag455-*.diag do artefato.");
        }
    }

    private void SampleLoop()
    {
        var lastCompleted = ThreadPool.CompletedWorkItemCount;
        var lastAdvance = Stopwatch.GetTimestamp();
        var captured = false;
        while (!_stop.Token.WaitHandle.WaitOne(SamplePeriodMs))
        {
            var pending = ThreadPool.PendingWorkItemCount;
            var completed = ThreadPool.CompletedWorkItemCount;
            var probe = PoolProbe();
            lock (_gate)
            {
                _maxPending = Math.Max(_maxPending, pending);
            }
            Write("sampler", string.Create(CultureInfo.InvariantCulture, $"{probe} loadavg={LoadAverage()}"));

            if (completed != lastCompleted)
            {
                lastCompleted = completed;
                lastAdvance = Stopwatch.GetTimestamp();
            }

            var stalledMs = Stopwatch.GetElapsedTime(lastAdvance).TotalMilliseconds;
            var stalled = pending > 0 && stalledMs >= StallThresholdMs;
            if (!captured && (stalled || Diag455StackCapture.Forced))
            {
                captured = true;
                _stallCaptured = stalled;
                CaptureStacks(string.Create(CultureInfo.InvariantCulture,
                    $"completedStuckMs={stalledMs:F0} pending={pending} completed={completed} " +
                    $"threads={ThreadPool.ThreadCount} forced={!stalled}"));
            }
        }
    }

    /// <summary>
    /// Pilhas de todas as threads no meio da parada (#455): mostram onde as threads do pool estão presas. A captura
    /// suspende o processo enquanto o dump é gravado, então só roda na primeira parada de cada teste.
    /// </summary>
    private void CaptureStacks(string reason)
    {
        Write("stacks", "capturing " + reason);
        foreach (var line in Diag455StackCapture.TryCapture(reason))
        {
            Write("stacks", line);
        }
    }

    /// <summary>
    /// Mede quanto um item enfileirado no thread pool espera para rodar. Com starvation, o item fica pendente por
    /// segundos e o atraso aparece aqui mesmo que os contadores pareçam normais.
    /// </summary>
    private string PoolProbe()
    {
        var queuedAt = Volatile.Read(ref _poolProbeQueuedAt);
        if (queuedAt != 0)
        {
            var waiting = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            TrackPoolLag(waiting);
            return string.Create(CultureInfo.InvariantCulture, $"poolProbe=outstanding:{waiting:F1}ms");
        }

        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _poolProbeQueuedAt, now);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var lag = Stopwatch.GetElapsedTime(now).TotalMilliseconds;
            Volatile.Write(ref _lastPoolLagMs, lag);
            TrackPoolLag(lag);
            Volatile.Write(ref _poolProbeQueuedAt, 0);
        }, null);
        return string.Create(CultureInfo.InvariantCulture, $"poolProbe=lastLag:{Volatile.Read(ref _lastPoolLagMs):F1}ms");
    }

    private void TrackPoolLag(double lag)
    {
        lock (_gate)
        {
            _maxPoolLagMs = Math.Max(_maxPoolLagMs, lag);
        }
    }

    /// <summary>
    /// Abertura síncrona sem pool numa thread dedicada: não depende do thread pool. Rápida enquanto o host espera 5 s
    /// aponta para starvation; lenta também aponta para rede, handshake ou servidor.
    /// </summary>
    private void ProbeLoop()
    {
        var useGss = true;
        do
        {
            var (source, dataSource, stats) = useGss
                ? ("probe-gss", _probeGss, _gssStats)
                : ("probe-nogss", _probeNoGss, _noGssStats);
            useGss = !useGss;

            var started = Stopwatch.GetTimestamp();
            string outcome;
            try
            {
                using var connection = dataSource.OpenConnection();
                outcome = "ok";
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                outcome = "error " + e.GetType().Name + ": " + e.Message;
            }
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_gate)
            {
                stats.Add(ms, outcome == "ok");
            }
            Write(source, string.Create(CultureInfo.InvariantCulture, $"syncOpenMs={ms:F1} {outcome}"));
        }
        while (!_stop.Token.WaitHandle.WaitOne(ProbePeriodMs));
    }

    private void Write(string source, string text)
    {
        var elapsed = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} t=+{elapsed:F1}ms utc={DateTime.UtcNow:HH:mm:ss.fff} test={_test} src={source} " +
            $"tid={Environment.CurrentManagedThreadId} tp=threads:{ThreadPool.ThreadCount},pending:" +
            $"{ThreadPool.PendingWorkItemCount},completed:{ThreadPool.CompletedWorkItemCount} | {text}");
        lock (_gate)
        {
            Console.Out.WriteLine(line);
            if (!_closed)
            {
                _file?.WriteLine(line);
                _file?.Flush();
            }
        }
    }

    private static NpgsqlConnectionStringBuilder ProbeConnection(string connectionString) =>
        new(connectionString)
        {
            Pooling = false,
            Timeout = ProbeTimeoutSeconds,
            ApplicationName = "diag455-probe",
        };

    private static double? PhysicalOpenDuration(string message, object? state)
    {
        if (!message.StartsWith("Opened physical connection", StringComparison.Ordinal)
            || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            return null;
        }

        foreach (var (key, value) in values)
        {
            if (key == "DurationMs" && value is IConvertible convertible)
            {
                return convertible.ToDouble(CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    private static bool IsTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException || current.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null)
        {
            return "";
        }

        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.GetType().Name + ": " + current.Message);
        }
        return " | exception: " + string.Join(" <- ", parts) + " | stack: " +
            exception.StackTrace?.ReplaceLineEndings(" / ");
    }

    private static string LoadAverage()
    {
        const string LoadAveragePath = "/proc/loadavg";
        return File.Exists(LoadAveragePath) ? File.ReadAllText(LoadAveragePath).Trim() : "n/a";
    }

    private static StreamWriter? OpenFile(string test)
    {
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (string.IsNullOrEmpty(runnerTemp))
        {
            return null;
        }

        // Mesmo diretório e extensão que o job já sobe como artefato em falha (ci.yml, "Preservar telemetria").
        var directory = Directory.CreateDirectory(Path.Combine(runnerTemp, "backend-test-results"));
        var name = string.Create(CultureInfo.InvariantCulture,
            $"diag455-{Environment.ProcessId}-{test}-{DateTime.UtcNow:HHmmssfff}.diag");
        return new StreamWriter(Path.Combine(directory.FullName, name), append: false);
    }

    private static void AppendStepSummary(string summary)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, "- diag455: `" + summary + "`" + Environment.NewLine);
        }
    }

    private sealed class ProbeStats
    {
        private int _count;
        private int _failures;
        private double _total;
        private double _max;

        public void Add(double ms, bool succeeded)
        {
            _count++;
            _failures += succeeded ? 0 : 1;
            _total += ms;
            _max = Math.Max(_max, ms);
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"n:{_count},fail:{_failures},avgMs:{(_count == 0 ? 0 : _total / _count):F1},maxMs:{_max:F1}");
    }
}

/// <summary>Logger do diagnóstico da #455: Npgsql em qualquer nível; o resto só a partir de Warning.</summary>
internal sealed class Diag455LoggerProvider(Diag455Recorder recorder, string source) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Diag455Logger(recorder, source, categoryName);

    public void Dispose()
    {
    }

    private sealed class Diag455Logger(Diag455Recorder recorder, string source, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            category.StartsWith("Npgsql", StringComparison.Ordinal) || logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Marcadores de ação do usuário repetem a cada comando e não dizem nada sobre a abertura.
            if (IsEnabled(logLevel) && eventId.Name is not ("StartUserAction" or "EndUserAction"))
            {
                recorder.Record(source, category, logLevel, eventId, formatter(state, exception), exception, state);
            }
        }
    }
}
