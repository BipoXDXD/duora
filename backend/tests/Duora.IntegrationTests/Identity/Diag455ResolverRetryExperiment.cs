using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// DIAGNÓSTICO #455 (hipótese H1, só local): hosts sem o certificado da chave fazem o DefaultKeyResolver repetir a
/// chave com Thread.Sleep, às vezes dentro da trava do KeyRingProvider; isso prenderia threads do pool e atrasaria a
/// abertura de conexões do Npgsql além de 5 s. Sobe hosts em paralelo, parte deles sobre um volume cuja chave foi
/// cifrada por um certificado ausente, e mede a abertura de conexões e o atraso do pool com as repetições padrão e
/// com MaximumTotalDefaultKeyResolverRetries=0, alternando as condições a cada rodada.
/// </summary>
public sealed class Diag455ResolverRetryExperiment(PostgresFixture postgres) : IDisposable
{
    private const string RetriesSwitch = "Microsoft.AspNetCore.DataProtection.KeyManagement.MaximumTotalDefaultKeyResolverRetries";
    private const string KeyResolverCategory = "Microsoft.AspNetCore.DataProtection.KeyManagement.DefaultKeyResolver";
    private const int KeyIneligibleBecauseMethodFailed = 12;
    private const int RetryingKeyAfterFailure = 73;
    private const double NpgsqlTimeoutMs = 5000;

    private readonly DirectoryInfo _origin = Directory.CreateTempSubdirectory("duora-diag455-origin-");
    private readonly List<DirectoryInfo> _volumes = [];
    private readonly TestCertificate _first = TestCertificates.Create("duora-diag455-1");
    private readonly TestCertificate _second = TestCertificates.Create("duora-diag455-2");

    public void Dispose()
    {
        AppContext.SetData(RetriesSwitch, null);
        _origin.Delete(recursive: true);
        foreach (var volume in _volumes)
        {
            volume.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolverRetriesVersusZeroRetries()
    {
        var hosts = Setting("DIAG455_HOSTS", 6);
        var retired = Setting("DIAG455_RETIRED", 3);
        var rounds = Setting("DIAG455_ROUNDS", 5);
        var openers = Setting("DIAG455_OPENERS", 4);
        var requestsPerHost = Setting("DIAG455_REQUESTS", 4);
        Report($"config hosts={hosts} retired={retired} roundsPerCondition={rounds} openers={openers} " +
            $"requestsPerHost={requestsPerHost} processors={Environment.ProcessorCount} " +
            $"minThreads={MinWorkerThreads()} loadavg={LoadAverage()}");

        // Chave cifrada com _first; os hosts "aposentados" recebem só _second e nunca a decifram.
        await new KeyRingHosts(postgres).SignInOnAFreshHostAsync(_origin, With(_first));
        var originKey = Assert.Single(_origin.GetFiles("key-*.xml"));

        var results = new List<RoundResult>();
        for (var round = 1; round <= rounds; round++)
        {
            // Alterna a ordem para que aquecimento e carga da máquina não favoreçam uma condição.
            var order = round % 2 == 1 ? new[] { false, true } : [true, false];
            foreach (var zeroRetries in order)
            {
                var result = await RunRoundAsync(round, zeroRetries, hosts, retired, openers, requestsPerHost, originKey);
                results.Add(result);
                Report(result.Describe());
            }
        }

        foreach (var zeroRetries in new[] { false, true })
        {
            var subset = results.Where(result => result.ZeroRetries == zeroRetries).ToList();
            var opens = subset.SelectMany(result => result.OpenMs).Order().ToList();
            Report(string.Create(CultureInfo.InvariantCulture,
                $"SUMMARY condition={Condition(zeroRetries)} rounds={subset.Count} opens={opens.Count} " +
                $"p50={Percentile(opens, 0.50):F1} p99={Percentile(opens, 0.99):F1} max={Percentile(opens, 1.0):F1} " +
                $"over5s={opens.Count(ms => ms > NpgsqlTimeoutMs)} openFailures={subset.Sum(r => r.OpenFailures)} " +
                $"maxPoolLagMs={subset.Max(r => r.MaxPoolLagMs):F1} maxPending={subset.Max(r => r.MaxPending)} " +
                $"http503={subset.Sum(r => r.Http503)} httpOther={subset.Sum(r => r.HttpUnexpected)} " +
                $"retryEvents73={subset.Sum(r => r.RetryEvents)} ineligibleEvents12={subset.Sum(r => r.IneligibleEvents)}"));
        }

        // Sanidade do experimento: a chave é por host, então cada condição precisa ter agido de fato.
        Assert.All(results.Where(r => !r.ZeroRetries), r => Assert.True(r.RetryEvents > 0, "Condição padrão sem repetições."));
        Assert.All(results.Where(r => r.ZeroRetries), r => Assert.Equal(0, r.RetryEvents));
    }

    private async Task<RoundResult> RunRoundAsync(int round, bool zeroRetries, int hosts, int retired, int openers,
        int requestsPerHost, FileInfo originKey)
    {
        AppContext.SetData(RetriesSwitch, zeroRetries ? "0" : null);
        var events = new ResolverEventCounter();
        var opens = new ConcurrentBag<double>();
        var openFailures = 0;
        var statuses = new ConcurrentBag<HttpStatusCode>();
        var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Pooling = false,
            Timeout = (int)(NpgsqlTimeoutMs / 1000),
        }.ConnectionString;

        using var sampler = new PoolLagSampler();
        using var stop = new CancellationTokenSource();
        var started = Stopwatch.GetTimestamp();
        var openerTasks = Enumerable.Range(0, openers).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var begin = Stopwatch.GetTimestamp();
                try
                {
                    await using var probe = new NpgsqlConnection(connection);
                    await probe.OpenAsync(CancellationToken.None);
                    opens.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                }
                catch (NpgsqlException)
                {
                    opens.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                    Interlocked.Increment(ref openFailures);
                }
            }
        })).ToList();

        var hostTasks = Enumerable.Range(0, hosts)
            .Select(index => Task.Run(() => RunHostAsync(index < retired, events, statuses, requestsPerHost, originKey)))
            .ToList();
        await Task.WhenAll(hostTasks);
        await stop.CancelAsync();
        await Task.WhenAll(openerTasks);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        return new RoundResult(round, zeroRetries, [.. opens.Order()], openFailures, sampler.MaxLagMs, sampler.MaxPending,
            statuses.Count(status => status == HttpStatusCode.ServiceUnavailable),
            statuses.Count(status => status is not (HttpStatusCode.NoContent or HttpStatusCode.Accepted
                or HttpStatusCode.ServiceUnavailable)),
            events.Retries, events.Ineligible, elapsed, LoadAverage());
    }

    private async Task RunHostAsync(bool retiredCertificate, ResolverEventCounter events,
        ConcurrentBag<HttpStatusCode> statuses, int requestsPerHost, FileInfo originKey)
    {
        var settings = retiredCertificate ? With(_second) : new Dictionary<string, string?>();
        settings["Logging:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Debug";
        await using var host = new DuoraWebFactory(postgres.ConnectionString, events)
        {
            SharedKeyRing = retiredCertificate ? FreshVolumeWith(originKey) : null,
            Settings = settings,
        };

        // Várias requisições na partida a frio: a primeira resolve o anel dentro da trava, as outras esperam nela.
        var browsers = Enumerable.Range(0, requestsPerHost).Select(_ => new Browser(host)).ToList();
        try
        {
            await Task.WhenAll(browsers.Select(async browser =>
            {
                using var csrf = await browser.GetAsync("/api/v1/auth/csrf");
                statuses.Add(csrf.StatusCode);
                using var registered = await browser.PostJsonAsync("/api/v1/auth/register",
                    IdentityScenario.RegistrationBody(IdentityScenario.NewEmail()));
                statuses.Add(registered.StatusCode);
            }));
        }
        finally
        {
            browsers.ForEach(browser => browser.Dispose());
        }
    }

    // Cada host aposentado ganha um volume só com a chave antiga: a nova chave que ele cria não pode vazar para a
    // rodada seguinte, senão a resolução já preferiria a nova e nem avaliaria a antiga.
    private DirectoryInfo FreshVolumeWith(FileInfo originKey)
    {
        var volume = Directory.CreateTempSubdirectory("duora-diag455-volume-");
        lock (_volumes)
        {
            _volumes.Add(volume);
        }
        originKey.CopyTo(Path.Combine(volume.FullName, originKey.Name));
        return volume;
    }

    private static Dictionary<string, string?> With(TestCertificate current) =>
        new()
        {
            ["DataProtection:RequireCertificate"] = "true",
            ["DataProtection:Certificate"] = current.Pem,
            ["DataProtection:PreviousCertificates"] = "",
        };

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value) ? value : fallback;

    private static int MinWorkerThreads()
    {
        ThreadPool.GetMinThreads(out var workers, out _);
        return workers;
    }

    private static string Condition(bool zeroRetries) => zeroRetries ? "retries=0" : "retries=default";

    private static double Percentile(List<double> sorted, double fraction) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string LoadAverage()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sysctl", "-n vm.loadavg")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return "?";
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return output.Replace(' ', '_');
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("DIAG455_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
    }

    private sealed record RoundResult(int Round, bool ZeroRetries, List<double> OpenMs, int OpenFailures,
        double MaxPoolLagMs, long MaxPending, int Http503, int HttpUnexpected, int RetryEvents, int IneligibleEvents,
        double ElapsedMs, string LoadAfter)
    {
        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"round={Round} condition={Condition(ZeroRetries)} elapsedMs={ElapsedMs:F0} opens={OpenMs.Count} " +
            $"p50={Percentile(OpenMs, 0.50):F1} p99={Percentile(OpenMs, 0.99):F1} max={Percentile(OpenMs, 1.0):F1} " +
            $"over5s={OpenMs.Count(ms => ms > NpgsqlTimeoutMs)} openFailures={OpenFailures} " +
            $"maxPoolLagMs={MaxPoolLagMs:F1} maxPending={MaxPending} http503={Http503} httpOther={HttpUnexpected} " +
            $"retryEvents73={RetryEvents} ineligibleEvents12={IneligibleEvents} loadavgAfter={LoadAfter}");
    }

    /// <summary>Conta os eventos do resolvedor; o resto dos logs é descartado.</summary>
    private sealed class ResolverEventCounter : ILoggerProvider
    {
        private int _retries;
        private int _ineligible;

        public int Retries => Volatile.Read(ref _retries);

        public int Ineligible => Volatile.Read(ref _ineligible);

        public ILogger CreateLogger(string categoryName) => new Counter(categoryName == KeyResolverCategory ? this : null);

        public void Dispose()
        {
        }

        private sealed class Counter(ResolverEventCounter? owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => owner is not null;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (owner is null)
                {
                    return;
                }
                if (eventId.Id == RetryingKeyAfterFailure)
                {
                    Interlocked.Increment(ref owner._retries);
                }
                else if (eventId.Id == KeyIneligibleBecauseMethodFailed)
                {
                    Interlocked.Increment(ref owner._ineligible);
                }
            }
        }
    }

    /// <summary>
    /// Atraso do thread pool (copiado da instrumentação do PR #457): a cada 20 ms, numa thread dedicada, enfileira um
    /// item e mede quanto ele espera para rodar; enquanto ele não roda, a espera acumulada também conta.
    /// </summary>
    private sealed class PoolLagSampler : IDisposable
    {
        private const int SamplePeriodMs = 20;

        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private readonly Lock _gate = new();
        private long _queuedAt;
        private double _maxLagMs;
        private long _maxPending;

        public PoolLagSampler()
        {
            _thread = new Thread(SampleLoop) { IsBackground = true, Name = "diag455-pool-lag" };
            _thread.Start();
        }

        public double MaxLagMs
        {
            get
            {
                lock (_gate)
                {
                    return _maxLagMs;
                }
            }
        }

        public long MaxPending
        {
            get
            {
                lock (_gate)
                {
                    return _maxPending;
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join();
            _stop.Dispose();
        }

        private void SampleLoop()
        {
            while (!_stop.Token.WaitHandle.WaitOne(SamplePeriodMs))
            {
                var pending = ThreadPool.PendingWorkItemCount;
                var queuedAt = Volatile.Read(ref _queuedAt);
                lock (_gate)
                {
                    _maxPending = Math.Max(_maxPending, pending);
                    if (queuedAt != 0)
                    {
                        _maxLagMs = Math.Max(_maxLagMs, Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                    }
                }
                if (queuedAt != 0)
                {
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                Volatile.Write(ref _queuedAt, now);
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    var lag = Stopwatch.GetElapsedTime(now).TotalMilliseconds;
                    lock (_gate)
                    {
                        _maxLagMs = Math.Max(_maxLagMs, lag);
                    }
                    Volatile.Write(ref _queuedAt, 0);
                }, null);
            }
        }
    }
}
