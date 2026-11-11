using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Login, cadastro e reenvio de confirmação respondem todo 503 igual: mesmo status, mesmo corpo e o mesmo
/// <c>Retry-After</c> da conta ocupada (<see cref="AccountLockOptions.BusyRetryAfter"/>, 30 s por padrão), qualquer
/// que seja a causa. Um valor por causa diria ao cliente o que falhou e, no lock da linha, que a conta existe
/// (decisão da coordenação, 10/11; #465 e #468). A falha sai do primeiro comando do EF de cada rota.
/// </summary>
[Trait("Threat", "TM-06")]
public sealed class NeutralUnavailabilityResponseTests(NeutralUnavailabilityResponseTests.FailingDatabase database)
    : IClassFixture<NeutralUnavailabilityResponseTests.FailingDatabase>
{
    private const string BusyRetryAfterSeconds = "30";

    private const string Expected =
        "503|{\"code\":\"TEMPORARILY_UNAVAILABLE\",\"status\":503,\"title\":\"Service temporarily unavailable. Try again later.\","
        + "\"type\":\"urn:duora:problem:temporarily-unavailable\"}|Retry-After=" + BusyRetryAfterSeconds;

    private static readonly string[] IdentityRoutes = ["login", "register", "email-confirmation-requests"];

    private static readonly string[] Causes =
    [
        "busy-fila", "busy-lock", "busy-prazo",
        "53300", "cliente-timeout",
        "40001-cru", "40P01-cru", "55P03-cru",
        "40001-npgsql", "40P01-npgsql", "55P03-npgsql",
        "57014-cru",
    ];

    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var route in IdentityRoutes)
        {
            foreach (var cause in Causes)
            {
                cases.Add(route, cause);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryUnavailabilityOnAnIdentityRouteAnswersTheSame(string route, string cause)
    {
        using var browser = new Browser(database.Factory);
        await browser.FetchCsrfTokenAsync();

        database.Failure.Value = Failure(cause);
        string described;
        try
        {
            using var response = await SendAsync(browser, route);
            described = await DescribeAsync(response);
        }
        finally
        {
            database.Failure.Value = null;
        }

        Assert.Equal(Expected, described);
    }

    /// <summary>O 503 próprio do cadastro (documentos legais indisponíveis) segue a mesma regra.</summary>
    [Fact]
    public async Task UnavailableLegalDocumentsAnswerLikeEveryOtherUnavailability()
    {
        var unapproved = SyntheticLegalPublication.Create(approved: false);
        await using var factory = new DuoraWebFactory(database.ConnectionString) { LegalDocuments = unapproved };
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/register", IdentityScenario.RegistrationBody(IdentityScenario.NewEmail(), publication: unapproved));

        Assert.Equal(Expected, await DescribeAsync(response));
    }

    private static Exception Failure(string cause) => cause switch
    {
        "busy-fila" => new AccountBusyException(TimeSpan.FromSeconds(2)),
        "busy-lock" => new AccountBusyException(
            TimeSpan.FromMilliseconds(300), new PostgresException("lock", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable)),
        "busy-prazo" => new AccountBusyException(TimeSpan.FromSeconds(7), new OperationCanceledException()),
        "53300" => new PostgresException("cheio", "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections),
        "cliente-timeout" => new NpgsqlException("timeout", new TimeoutException()),
        "57014-cru" => new PostgresException("timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled),
        _ when cause.EndsWith("-cru", StringComparison.Ordinal) =>
            new PostgresException("contenção", "ERROR", "ERROR", cause[..^"-cru".Length]),
        _ when cause.EndsWith("-npgsql", StringComparison.Ordinal) => new NpgsqlException(
            "contenção", new PostgresException("contenção", "ERROR", "ERROR", cause[..^"-npgsql".Length])),
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
    };

    private static Task<HttpResponseMessage> SendAsync(Browser browser, string route)
    {
        var email = IdentityScenario.NewEmail();
        return route switch
        {
            "login" => browser.PostJsonAsync("/api/v1/auth/login", new { email, password = IdentityScenario.ValidPassword }),
            "register" => browser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(email)),
            "email-confirmation-requests" => browser.PostJsonAsync(
                "/api/v1/auth/email-confirmation-requests", new { email, password = IdentityScenario.ValidPassword }),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }

    /// <summary>Status, corpo sem <c>traceId</c> (que muda a cada requisição) com as chaves em ordem, e <c>Retry-After</c>.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidOperationException("Sem corpo.");
        var body = new JsonObject();
        foreach (var (key, value) in problem.Where(pair => pair.Key != "traceId").OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            body[key] = value?.DeepClone();
        }

        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? string.Join(',', values) : "(nenhum)";
        return $"{(int)response.StatusCode}|{body.ToJsonString()}|Retry-After={retryAfter}";
    }

    /// <summary>A app real, com um interceptador que faz o próximo comando do EF lançar a falha armada.</summary>
    public sealed class FailingDatabase : IAsyncDisposable
    {
        public FailingDatabase(PostgresFixture postgres)
        {
            ConnectionString = postgres.ConnectionString;
            var interceptor = new FailingCommands(Failure);
            Factory = new DuoraWebFactory(postgres.ConnectionString)
            {
                // Dezenas de pedidos do mesmo IP: o teto por IP não é o assunto aqui.
                Settings = new Dictionary<string, string?>
                {
                    ["RateLimiting:LoginPerIp:PermitLimit"] = "1000",
                    ["RateLimiting:RegisterPerIp:PermitLimit"] = "1000",
                },
                TestServices = services =>
                    services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(interceptor)),
            };
        }

        public string ConnectionString { get; }

        public DuoraWebFactory Factory { get; }

        /// <summary>Falha do próximo comando; nula, o banco responde normalmente.</summary>
        public StrongBox<Exception?> Failure { get; } = new();

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private sealed class FailingCommands(StrongBox<Exception?> failure) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfArmed();
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfArmed();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            ThrowIfArmed();
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return ValueTask.FromResult(result);
        }

        private void ThrowIfArmed()
        {
            if (failure.Value is { } armed)
            {
                throw armed;
            }
        }
    }
}
