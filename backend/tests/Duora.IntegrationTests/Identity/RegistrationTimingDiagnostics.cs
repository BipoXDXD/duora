using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// DIAGNÓSTICO LOCAL da issue #466 (não vai para main): mede o tempo do cadastro por caso e decompõe em hash, SQL
/// e transação, com o custo real do PBKDF2. Só roda com DUORA_TIMING_OUT apontando para um diretório de saída.
/// </summary>
[Trait("Category", "Diagnostic")]
public sealed class RegistrationTimingDiagnostics(PostgresFixture postgres)
{
    private const string Register = "/api/v1/auth/register";
    private static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("DUORA_TIMING_OUT");
    private static int Samples => int.Parse(Environment.GetEnvironmentVariable("DUORA_TIMING_N") ?? "40", CultureInfo.InvariantCulture);
    private const int Warmup = 10;
    private const int HeldWarmup = 3;

    private static Dictionary<string, string?> Limits(TimeSpan? ceiling)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RateLimiting:RegisterPerIp:PermitLimit"] = "1000000",
            ["RateLimiting:RegisterPerAccount:PermitLimit"] = "1000000",
            ["RateLimiting:TokenRedemptionPerIp:PermitLimit"] = "1000000",
            ["RateLimiting:LoginPerIp:PermitLimit"] = "1000000",
            ["RateLimiting:AccountSecurityPerIp:PermitLimit"] = "1000000",
        };
        if (ceiling is { } c)
        {
            settings["Identity:AccountLockTimeout"] = c.ToString("c", CultureInfo.InvariantCulture);
        }
        return settings;
    }

    [Fact]
    public async Task MeasureRegistrationTiming()
    {
        Assert.SkipWhen(OutputDirectory is null, "Diagnóstico #466: defina DUORA_TIMING_OUT.");
        var recorder = new TimingRecorder();
        var rows = new StringBuilder("case,round,phase,ms\n");

        // Fase 1: casos rápidos, teto padrão (5 s), intercalados em ordem aleatória por rodada.
        await using (var factory = NewFactory(recorder, ceiling: null))
        {
            var scenario = new IdentityScenario(postgres, factory);
            var total = Warmup + Samples;
            var pending = new List<string>();
            var confirmed = new List<string>();
            var throttled = new List<string>();
            for (var i = 0; i < total; i++)
            {
                pending.Add(await CreatePendingAsync(factory));
                confirmed.Add((await scenario.CreateConfirmedAccountAsync()).Email);
                var t = await CreatePendingAsync(factory);
                var link = await scenario.ReadLatestLinkAsync(t, "EmailConfirmation");
                await scenario.ScalarAsync(
                    """
                    insert into identity.user_tokens (user_id, login_provider, name, value)
                    values ($1, 'Duora.RegistrationBudget', 'Hourly', $2)
                    on conflict (user_id, login_provider, name) do update set value = excluded.value
                    returning 1
                    """,
                    link.UserId, $"{DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture)}|3");
                throttled.Add(t);
            }

            using var browser = new Browser(factory);
            await browser.FetchCsrfTokenAsync();
            var random = new Random(466);
            for (var round = 0; round < total; round++)
            {
                var cases = new (string Name, string Email)[]
                {
                    ("new", IdentityScenario.NewEmail()),
                    ("pending", pending[round]),
                    ("confirmed", confirmed[round]),
                    ("throttled_budget", throttled[round]),
                };
                random.Shuffle(cases);
                foreach (var (name, email) in cases)
                {
                    var sample = await MeasureAsync(recorder, () => browser.PostJsonAsync(Register, IdentityScenario.RegistrationBody(email)));
                    if (round >= Warmup)
                    {
                        Append(rows, name, round - Warmup, sample);
                    }
                }
            }

            // Concorrência: 8 cadastros de e-mail novo ao mesmo tempo, 10 lotes (pressão de CPU do PBKDF2).
            for (var batch = 0; batch < 12; batch++)
            {
                var tasks = Enumerable.Range(0, 8).Select(async _ =>
                {
                    using var b = new Browser(factory);
                    await b.FetchCsrfTokenAsync();
                    var sw = Stopwatch.StartNew();
                    using var r = await b.PostJsonAsync(Register, IdentityScenario.RegistrationBody(IdentityScenario.NewEmail()));
                    sw.Stop();
                    Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
                    return sw.Elapsed.TotalMilliseconds;
                }).ToArray();
                var times = await Task.WhenAll(tasks);
                if (batch >= 2)
                {
                    for (var k = 0; k < times.Length; k++)
                    {
                        rows.Append(CultureInfo.InvariantCulture, $"new_concurrent8,{(batch - 2) * 8 + k},client_total,{times[k]:F3}\n");
                    }
                }
            }

            // Conta travada no teto padrão (5 s).
            await MeasureHeldAsync(recorder, factory, scenario, "held_ceiling_5000ms", rows);
        }

        foreach (var ceilingMs in new[] { 300, 100 })
        {
            await using var factory = NewFactory(recorder, TimeSpan.FromMilliseconds(ceilingMs));
            await MeasureHeldAsync(recorder, factory, new IdentityScenario(postgres, factory), $"held_ceiling_{ceilingMs}ms", rows);
        }

        Directory.CreateDirectory(OutputDirectory!);
        await File.WriteAllTextAsync(Path.Combine(OutputDirectory!, "samples.csv"), rows.ToString(), TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(OutputDirectory!, "sql-texts.txt"), recorder.Texts.Keys.Order(), TestContext.Current.CancellationToken);
    }

    private DuoraWebFactory NewFactory(TimingRecorder recorder, TimeSpan? ceiling) =>
        new(postgres.ConnectionString)
        {
            RealPasswordHashCost = true,
            Settings = Limits(ceiling),
            TestServices = recorder.Install,
        };

    private static async Task<string> CreatePendingAsync(DuoraWebFactory factory)
    {
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registered = await IdentityScenario.RegisterAsync(browser, email);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        return email;
    }

    private async Task MeasureHeldAsync(
        TimingRecorder recorder, DuoraWebFactory factory, IdentityScenario scenario, string name, StringBuilder rows)
    {
        var heldSamples = Math.Min(Samples, 30);
        var accounts = new List<TestAccount>();
        for (var i = 0; i < HeldWarmup + heldSamples; i++)
        {
            accounts.Add(await scenario.CreateConfirmedAccountAsync());
        }

        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        for (var i = 0; i < accounts.Count; i++)
        {
            await using var holder = await HoldAsync(accounts[i].UserId);
            var sample = await MeasureAsync(recorder, () => browser.PostJsonAsync(Register, IdentityScenario.RegistrationBody(accounts[i].Email)));
            if (i >= HeldWarmup)
            {
                Append(rows, name, i - HeldWarmup, sample);
            }
        }
    }

    private async Task<Holder> HoldAsync(Guid userId)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select 1 from identity.users where id = $1 for no key update", connection, transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new Holder(connection, transaction);
    }

    private sealed class Holder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<IReadOnlyList<(string Phase, double Ms)>> MeasureAsync(
        TimingRecorder recorder, Func<Task<HttpResponseMessage>> send)
    {
        recorder.Clear();
        var stopwatch = Stopwatch.StartNew();
        using var response = await send();
        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var phases = recorder.Snapshot().ToList();
        phases.Add(("client_total", stopwatch.Elapsed.TotalMilliseconds));
        return phases;
    }

    private static void Append(StringBuilder rows, string name, int round, IReadOnlyList<(string Phase, double Ms)> sample)
    {
        foreach (var group in sample.GroupBy(p => p.Phase))
        {
            rows.Append(CultureInfo.InvariantCulture, $"{name},{round},{group.Key},{group.Sum(p => p.Ms):F3}\n");
        }
    }

    /// <summary>Tempos do trabalho do cadastro, só das requisições de cadastro (HttpContext do caminho).</summary>
    private sealed class TimingRecorder
    {
        private readonly ConcurrentQueue<(string, double)> _phases = new();
        private readonly ConcurrentDictionary<object, long> _started = new(ReferenceEqualityComparer.Instance);
        private int _hashIndex;

        public ConcurrentDictionary<string, byte> Texts { get; } = new();

        public void Clear()
        {
            _phases.Clear();
            _started.Clear();
            _hashIndex = 0;
        }

        public IEnumerable<(string, double)> Snapshot() => [.. _phases];

        public void Install(IServiceCollection services)
        {
            services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(
                new CommandTimer(this), new TransactionTimer(this), new ConnectionTimer(this)));
            services.AddScoped<IPasswordHasher<DuoraUser>>(provider =>
                new TimedHasher(this, new PasswordHasher<DuoraUser>(provider.GetRequiredService<IOptions<PasswordHasherOptions>>())));
            services.AddSingleton<IStartupFilter>(new ServerTimer(this));
        }

        private static bool InRegistration() =>
            new HttpContextAccessor().HttpContext?.Request.Path.Value == Register;

        public void Start(object key)
        {
            if (InRegistration())
            {
                _started[key] = Stopwatch.GetTimestamp();
            }
        }

        public void Stop(object key, string phase)
        {
            if (_started.TryRemove(key, out var start))
            {
                _phases.Enqueue((phase, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            }
        }

        public void Record(string phase, double ms) => _phases.Enqueue((phase, ms));

        public string NextHashPhase() => Interlocked.Increment(ref _hashIndex) == 1 ? "hash_password" : "hash_code";

        public static bool Registration => InRegistration();
    }

    private sealed class CommandTimer(TimingRecorder recorder) : DbCommandInterceptor
    {
        private static readonly (string[] Markers, string Phase)[] Categories =
        [
            (["set_config('lock_timeout'"], "sql_lock_timeout_cfg"),
            (["insert into identity.users"], "sql_upsert"),
            (["ops.outbox_events"], "sql_savechanges_outbox"),
            (["from identity.user_tokens"], "sql_budget_read"),
            (["insert into identity.user_tokens", "values"], "sql_budget_write"),
            (["insert into identity.user_tokens"], "sql_code_token"),
            (["consent"], "sql_consents"),
            (["user_roles"], "sql_role"),
            (["from identity.users"], "sql_find_user"),
        ];

        private static string Category(string text)
        {
            var t = text.ToLowerInvariant();
            return Categories.FirstOrDefault(c => c.Markers.All(m => t.Contains(m, StringComparison.Ordinal))).Phase ?? "sql_other";
        }

        private void Begin(DbCommand command)
        {
            if (TimingRecorder.Registration)
            {
                recorder.Texts.TryAdd(Category(command.CommandText) + " :: " + string.Join(' ', command.CommandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 0);
            }
            recorder.Start(command);
        }

        private void End(DbCommand command, string suffix = "") => recorder.Stop(command, Category(command.CommandText) + suffix);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Begin(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            End(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Begin(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            End(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Begin(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            End(command);
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            End(command, "_failed");
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionTimer(TimingRecorder recorder) : DbTransactionInterceptor
    {
        private readonly object _begin = new();
        private readonly object _commit = new();

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            recorder.Start(_begin);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            recorder.Stop(_begin, "tx_begin");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            recorder.Start(_commit);
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            recorder.Stop(_commit, "tx_commit");
            return Task.CompletedTask;
        }
    }

    private sealed class ConnectionTimer(TimingRecorder recorder) : DbConnectionInterceptor
    {
        private readonly object _open = new();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            recorder.Start(_open);
            return ValueTask.FromResult(result);
        }

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            recorder.Stop(_open, "db_connection_open");
            return Task.CompletedTask;
        }
    }

    private sealed class TimedHasher(TimingRecorder recorder, PasswordHasher<DuoraUser> inner) : IPasswordHasher<DuoraUser>
    {
        public string HashPassword(DuoraUser user, string password)
        {
            var start = Stopwatch.GetTimestamp();
            var hash = inner.HashPassword(user, password);
            if (TimingRecorder.Registration)
            {
                recorder.Record(recorder.NextHashPhase(), Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            return hash;
        }

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword) =>
            inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    private sealed class ServerTimer(TimingRecorder recorder) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Path.Value != Register)
                {
                    await following();
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                await following();
                recorder.Record("server_total", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            });
            next(app);
        };
    }
}
