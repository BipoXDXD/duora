using System.Diagnostics;
using System.Globalization;
using System.Net;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>A medida de tempo roda sozinha, depois das classes paralelas, para o ruído da suíte não entrar nela.</summary>
[CollectionDefinition(nameof(SerialTimingMeasurement), DisableParallelization = true)]
public sealed class SerialTimingMeasurement;

/// <summary>
/// Issue #466 (decisão da coordenação, 10/11: (iv-a)): o tempo de resposta do cadastro não distingue um e-mail novo de
/// uma conta travada por outra operação nem de uma conta com o orçamento de emissão esgotado. Os três respondem 202
/// idêntico; aqui se afirma que o tempo também: a mediana das diferenças para o e-mail novo, pareadas por rodada, fica
/// dentro de <see cref="MedianDifferenceLimit"/>.
/// </summary>
/// <remarks>
/// <para>
/// O hash tem custo fixo (<see cref="FixedCostPasswordHasher"/>) em vez do PBKDF2 real: o que se mede é a forma do
/// caminho (quantos hashes e quanto se espera pela trava), e um custo de CPU real oscila com a carga do runner a ponto de
/// encobrir ou inventar diferença. Com o custo fixo, cada hash pulado aparece como <see cref="HashCost"/> a menos.
/// </para>
/// <para>
/// O teto do login (<c>Identity:AccountLockTimeout</c>) fica em 300 ms, o mesmo dos outros testes de trava: se o cadastro
/// voltasse a usá-lo, a conta travada responderia ~300 ms mais devagar. O teto do cadastro fica no padrão.
/// </para>
/// </remarks>
[Collection(nameof(SerialTimingMeasurement))]
public sealed class RegistrationTimingTests : IAsyncDisposable
{
    private const string Register = "/api/v1/auth/register";
    private const string GaveUpLogMessage = "gave up waiting for the account row lock";

    /// <summary>Custo de cada hash. Maior que o limite, para um hash pulado nunca caber na tolerância.</summary>
    private static readonly TimeSpan HashCost = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Limite da mediana das diferenças pareadas. Cobre o resíduo esperado (o SQL que a desistência não roda, ~5–7 ms
    /// medidos na #466, e o teto curto do cadastro) mais o erro da mediana numa máquina carregada: com loadavg ~30, o
    /// desvio por amostra chegou a 36–39 ms e o orçamento esgotado deu −12,2 ms. Mesmo assim fica abaixo do que o
    /// teste tem de pegar: um hash pulado custa ao menos <see cref="HashCost"/>, e as sabotagens deram
    /// −53 ms (hash do código só no caminho que emite) e +294 ms (cadastro de volta ao teto do login).
    /// </summary>
    private static readonly TimeSpan MedianDifferenceLimit = TimeSpan.FromMilliseconds(25);

    private static readonly TimeSpan LoginLockTimeout = TimeSpan.FromMilliseconds(300);

    private const int WarmupRounds = 5;
    private const int Samples = 31;

    /// <summary>Orçamento persistente de emissão por conta e por hora (cadastro e reenvio).</summary>
    private const int IssuanceBudget = 3;

    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;

    public RegistrationTimingTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs)
        {
            Settings = new Dictionary<string, string?>
            {
                ["Identity:AccountLockTimeout"] = LoginLockTimeout.ToString("c", CultureInfo.InvariantCulture),
                // Os limites por IP e por conta responderiam 429 antes do caminho medido.
                ["RateLimiting:RegisterPerIp:PermitLimit"] = "1000",
                ["RateLimiting:RegisterPerAccount:PermitLimit"] = "1000",
            },
            TestServices = services =>
            {
                services.RemoveAll<IPasswordHasher<DuoraUser>>();
                services.AddScoped<IPasswordHasher<DuoraUser>, FixedCostPasswordHasher>();
            },
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ANewEmailAHeldAccountAndAnExhaustedIssuanceBudgetAnswerInTheSameMedianTime()
    {
        var held = await Scenario.CreateConfirmedAccountAsync();
        var exhausted = await ExhaustIssuanceBudgetAsync();
        var exhaustedOutbox = await Scenario.CountOutboxEventsAsync(exhausted);
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var times = new Dictionary<RegistrationCase, List<double>>
        {
            [RegistrationCase.NewEmail] = [],
            [RegistrationCase.HeldAccount] = [],
            [RegistrationCase.ExhaustedBudget] = [],
        };
        var order = Enum.GetValues<RegistrationCase>();
        var random = new Random(466);

        for (var round = 0; round < WarmupRounds + Samples; round++)
        {
            random.Shuffle(order);
            foreach (var registrationCase in order)
            {
                var elapsed = registrationCase switch
                {
                    RegistrationCase.NewEmail => await TimeRegistrationAsync(browser, IdentityScenario.NewEmail()),
                    RegistrationCase.HeldAccount => await TimeHeldRegistrationAsync(browser, held),
                    RegistrationCase.ExhaustedBudget => await TimeRegistrationAsync(browser, exhausted),
                    _ => throw new UnreachableException(),
                };
                if (round >= WarmupRounds)
                {
                    times[registrationCase].Add(elapsed.TotalMilliseconds);
                }
            }
        }

        // Diferença pareada por rodada: uma rodada lenta pela carga da máquina pesa nos três casos e se cancela.
        var heldAccount = MedianDifferenceFromNewEmail(times, RegistrationCase.HeldAccount);
        var exhaustedBudget = MedianDifferenceFromNewEmail(times, RegistrationCase.ExhaustedBudget);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"#466 N = {Samples}; medianas: e-mail novo {Median(times[RegistrationCase.NewEmail]):F1} ms, " +
            $"conta travada {Median(times[RegistrationCase.HeldAccount]):F1} ms, " +
            $"orçamento esgotado {Median(times[RegistrationCase.ExhaustedBudget]):F1} ms; " +
            $"desvio por amostra do e-mail novo {StandardDeviation(times[RegistrationCase.NewEmail]):F1} ms; " +
            $"diferença pareada (mediana): conta travada {heldAccount:+0.0;-0.0} ms, " +
            $"orçamento esgotado {exhaustedBudget:+0.0;-0.0} ms; limite ±{MedianDifferenceLimit.TotalMilliseconds:F0} ms."));

        // Os casos só valem se seguiram o caminho que dizem: a desistência pela trava e o cadastro recusado pelo orçamento.
        Assert.Equal(
            WarmupRounds + Samples,
            _logs.Entries.Count(entry => entry.Text.Contains(GaveUpLogMessage, StringComparison.Ordinal)));
        Assert.Equal(exhaustedOutbox, await Scenario.CountOutboxEventsAsync(exhausted));
        AssertWithinLimit("conta travada", heldAccount);
        AssertWithinLimit("orçamento esgotado", exhaustedBudget);
    }

    private enum RegistrationCase
    {
        NewEmail,
        HeldAccount,
        ExhaustedBudget,
    }

    private static void AssertWithinLimit(string registrationCase, double difference) =>
        Assert.True(
            Math.Abs(difference) <= MedianDifferenceLimit.TotalMilliseconds,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{registrationCase}: mediana da diferença para o e-mail novo de {difference:+0.0;-0.0} ms " +
                $"(limite ±{MedianDifferenceLimit.TotalMilliseconds:F0} ms)."));

    /// <summary>Conta pendente cujos cadastros já gastaram as emissões da hora; o próximo é recusado sem gravar.</summary>
    private async Task<string> ExhaustIssuanceBudgetAsync()
    {
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(_factory);
        for (var i = 0; i < IssuanceBudget; i++)
        {
            using var registered = await IdentityScenario.RegisterAsync(browser, email);
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        }

        return email;
    }

    private static async Task<TimeSpan> TimeRegistrationAsync(Browser browser, string email)
    {
        var elapsed = Stopwatch.StartNew();
        using var response = await browser.PostJsonAsync(Register, IdentityScenario.RegistrationBody(email));
        elapsed.Stop();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return elapsed.Elapsed;
    }

    /// <summary>Só o POST entra no tempo; travar e soltar a linha ficam de fora.</summary>
    private async Task<TimeSpan> TimeHeldRegistrationAsync(Browser browser, TestAccount account)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = new NpgsqlCommand(
            "select 1 from identity.users where id = $1 for no key update", connection, transaction))
        {
            command.Parameters.AddWithValue(account.UserId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var elapsed = await TimeRegistrationAsync(browser, account.Email);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        return elapsed;
    }

    private static double MedianDifferenceFromNewEmail(Dictionary<RegistrationCase, List<double>> times, RegistrationCase other) =>
        Median(times[other].Zip(times[RegistrationCase.NewEmail], (sample, newEmail) => sample - newEmail));

    private static double StandardDeviation(List<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>O hasher do Identity com o custo trocado por uma espera fixa e estável por hash.</summary>
    private sealed class FixedCostPasswordHasher(IOptions<PasswordHasherOptions> options) : IPasswordHasher<DuoraUser>
    {
        private readonly PasswordHasher<DuoraUser> _inner = new(options);

        public string HashPassword(DuoraUser user, string password)
        {
            Thread.Sleep(HashCost);
            return _inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword)
        {
            Thread.Sleep(HashCost);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
