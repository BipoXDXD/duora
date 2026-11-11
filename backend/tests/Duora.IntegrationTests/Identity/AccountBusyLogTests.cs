using System.Diagnostics.Metrics;
using System.Net;
using System.Text.RegularExpressions;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Conta ocupada (503) se distingue do banco indisponível só no log e na métrica, pelo motivo interno. A resposta HTTP
/// continua a mesma do banco indisponível, salvo o <c>Retry-After</c>: diferenciá-las abriria enumeração de contas.
/// O log leva motivo, padrão da rota, teto e traceId; nunca e-mail, conta ou mensagem de exceção.
/// </summary>
public sealed partial class AccountBusyLogTests(PostgresFixture postgres)
{
    private const int AccountBusyEventId = 5031;
    private const string AccountBusyEventName = "AccountBusy";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string HandlerCategory = "Duora.Web.Http.AccountBusyExceptionHandler";

    [Fact]
    public async Task ALockCeilingIsLoggedWithItsReasonRouteCeilingAndTraceId()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString, logs)
        {
            Settings = new Dictionary<string, string?> { ["Identity:AccountLockTimeout"] = "00:00:00.300" },
        };
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        using var browser = new Browser(factory);
        await using var holder = await HoldAccountAsync(account.UserId);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(30, ProblemAssert.RetryAfterSeconds(response));
        var entry = SingleAccountBusyLog(logs);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("AccountLock", entry.Fields["Reason"]?.ToString());
        Assert.Equal(LoginRoute, entry.Fields["Route"]);
        Assert.Equal(300L, entry.Fields["CeilingMilliseconds"]);
        Assert.Equal((string?)body["traceId"], entry.Fields["TraceId"]);
    }

    [Fact]
    public async Task ALoginPastItsDeadlineIsLoggedAsTheLoginDeadline()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString, logs)
        {
            Settings = new Dictionary<string, string?> { ["Identity:LoginTimeout"] = "00:00:01" },
        };
        var email = IdentityScenario.NewEmail();
        Assert.True(EmailAddress.TryParse(email, out var address));
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        using var scope = factory.Services.CreateScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();
        await using (await security.EnterLoginAsync(address, TestContext.Current.CancellationToken))
        {
            using var response = await IdentityScenario.LoginAsync(browser, email, "senha incorreta longa");

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        }

        var entry = SingleAccountBusyLog(logs);
        Assert.Equal("LoginDeadline", entry.Fields["Reason"]?.ToString());
        Assert.Equal(1000L, entry.Fields["CeilingMilliseconds"]);
    }

    [Fact]
    public async Task AQueueTimeoutCarriesTheQueueReason()
    {
        var gate = new AccountCheckGate(new AccountLockOptions());
        var key = Guid.NewGuid();
        var timeout = TimeSpan.FromMilliseconds(100);
        await using var holder = await gate.EnterAsync(key, timeout, TestContext.Current.CancellationToken);

        var busy = await Assert.ThrowsAsync<AccountBusyException>(
            () => gate.EnterAsync(key, timeout, TestContext.Current.CancellationToken));

        Assert.Equal(AccountBusyReason.AccountQueue, busy.Reason);
        Assert.Equal(timeout, busy.WaitCeiling);
    }

    /// <summary>
    /// TM: enumeração e vazamento pelo log. O canário vai no e-mail digitado e na mensagem da exceção (e na interna);
    /// nenhum log da aplicação o repete, nem o Warning da conta ocupada.
    /// </summary>
    [Fact]
    public async Task TheAccountBusyLogNeverCarriesTheEmailOrTheExceptionMessage()
    {
        var canary = $"CANARY-{Guid.NewGuid()}";
        var logs = new CapturingLoggerProvider();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString, logs)
        {
            TestServices = services => services.AddScoped<IPasswordHasher<DuoraUser>>(
                _ => new BusyPasswordHasher(new AccountBusyException(canary, new InvalidOperationException(canary)))),
        };
        using var browser = new Browser(factory);

        using var response = await IdentityScenario.LoginAsync(browser, $"{canary}@example.com", "senha qualquer longa");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        var entry = SingleAccountBusyLog(logs);
        Assert.Equal("Other", entry.Fields["Reason"]?.ToString());
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(logs.Entries, log => log.Text.Contains(canary, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            logs.Entries.SelectMany(log => log.Fields.Values),
            value => value?.ToString()?.Contains(canary, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// TM: enumeração pela resposta. Conta ocupada e banco indisponível respondem os mesmos bytes (status, corpo sem o
    /// traceId e headers), exceto o <c>Retry-After</c>, que já difere: o da conta é o <c>RetryAfter</c> da exceção
    /// (<c>BusyRetryAfter</c> em produção; 2 s neste stub), o do banco é 5 s.
    /// </summary>
    [Fact]
    public async Task AnAccountBusyAnswerIsTheDatabaseUnavailableAnswerExceptRetryAfter()
    {
        await using var busyFactory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.AddScoped<IPasswordHasher<DuoraUser>>(
                _ => new BusyPasswordHasher(new AccountBusyException(TimeSpan.FromSeconds(2), AccountBusyReason.AccountLock, TimeSpan.FromMilliseconds(300)))),
        };
        await using var downFactory = new DuoraWebFactory(new CanaryConnectionString().Value);
        using var busyBrowser = new Browser(busyFactory);
        using var downBrowser = new Browser(downFactory);
        var email = IdentityScenario.NewEmail();

        using var busy = await IdentityScenario.LoginAsync(busyBrowser, email, "senha qualquer longa");
        using var down = await IdentityScenario.LoginAsync(downBrowser, email, "senha qualquer longa");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal(down.StatusCode, busy.StatusCode);
        Assert.Equal(await RawBodyWithoutTraceIdAsync(down), await RawBodyWithoutTraceIdAsync(busy));
        Assert.Equal(HeadersExceptRetryAfterAndDate(down), HeadersExceptRetryAfterAndDate(busy));
        Assert.Equal(2, ProblemAssert.RetryAfterSeconds(busy));
        Assert.Equal(5, ProblemAssert.RetryAfterSeconds(down));
    }

    [Fact]
    public async Task AccountBusyIsCountedByReasonOnly()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.AddScoped<IPasswordHasher<DuoraUser>>(
                _ => new BusyPasswordHasher(new AccountBusyException(TimeSpan.FromSeconds(2), AccountBusyReason.LoginDeadline, TimeSpan.FromSeconds(1)))),
        };
        using var counter = new AccountBusyCounter(factory.Services.GetRequiredService<IMeterFactory>());
        using var browser = new Browser(factory);

        using var response = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), "senha qualquer longa");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var measurement = Assert.Single(counter.Measurements);
        Assert.Equal(1, measurement.Value);
        var tag = Assert.Single(measurement.Tags);
        Assert.Equal("reason", tag.Key);
        Assert.Equal("login_deadline", tag.Value);
    }

    public static TheoryData<AccountBusyReason> NamedReasons =>
        [.. Enum.GetValues<AccountBusyReason>().Where(reason => reason != AccountBusyReason.Other)];

    /// <summary>Motivo novo no enum precisa de rótulo próprio; sem ele, cairia em <c>other</c> em silêncio.</summary>
    [Theory]
    [MemberData(nameof(NamedReasons))]
    public void EveryNamedReasonHasItsOwnTag(AccountBusyReason reason)
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<IMeterFactory>();
        using var counter = new AccountBusyCounter(meters);
        var metrics = new AccountBusyMetrics(meters);

        metrics.Record(reason);

        var tag = Assert.Single(Assert.Single(counter.Measurements).Tags);
        Assert.NotEqual("other", tag.Value);
    }

    /// <summary>
    /// Um motivo fora do enum (versão nova lançando, conversão errada) não pode estourar dentro do handler: o 503
    /// viraria 500. Conta como <c>other</c>.
    /// </summary>
    [Fact]
    public void AReasonOutsideTheEnumIsCountedAsOther()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<IMeterFactory>();
        using var counter = new AccountBusyCounter(meters);
        var metrics = new AccountBusyMetrics(meters);

        metrics.Record((AccountBusyReason)99);

        var measurement = Assert.Single(counter.Measurements);
        var tag = Assert.Single(measurement.Tags);
        Assert.Equal("other", tag.Value);
    }

    private static CapturedLog SingleAccountBusyLog(CapturingLoggerProvider logs)
    {
        var entry = Assert.Single(logs.Entries, log => log.Category == HandlerCategory);
        Assert.Equal(AccountBusyEventId, entry.EventId.Id);
        Assert.Equal(AccountBusyEventName, entry.EventId.Name);
        return entry;
    }

    private static async Task<string> RawBodyWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return TraceIdValue().Replace(text, "\"traceId\":\"\"");
    }

    private static string HeadersExceptRetryAfterAndDate(HttpResponseMessage response)
    {
        string[] varying = ["Retry-After", "Date"];
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !varying.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}");
        return string.Join("\n", headers);
    }

    /// <summary>Outra transação com a linha da conta travada, como uma verificação longa em andamento.</summary>
    private async Task<AccountHolder> HoldAccountAsync(Guid userId)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select 1 from identity.users where id = $1 for no key update",
            connection,
            transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new AccountHolder(connection, transaction);
    }

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceIdValue();

    private sealed class AccountHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>Hasher que estoura com a exceção dada ao verificar (login real ou hash de isca).</summary>
    private sealed class BusyPasswordHasher(AccountBusyException busy) : IPasswordHasher<DuoraUser>
    {
        public string HashPassword(DuoraUser user, string password) => throw busy;

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword) =>
            throw busy;
    }

    /// <summary>Medições do contador de conta ocupada desta aplicação, sem as de testes em paralelo.</summary>
    private sealed class AccountBusyCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];

        public AccountBusyCounter(IMeterFactory meters)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters)
                    && instrument.Meter.Name == AccountBusyMetrics.MeterName
                    && instrument.Name == AccountBusyMetrics.AccountBusy)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                lock (_measurements)
                {
                    _measurements.Add((value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(long Value, KeyValuePair<string, object?>[] Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
