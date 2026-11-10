using System.Diagnostics;
using System.Globalization;
using System.Net;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Os testes de trava do reenvio rodam sem outras classes em paralelo: a medida de tempo não pode levar o ruído da
/// suíte, e as esperas deles pela linha de <c>identity.users</c> entrariam na contagem de conexões esperando de
/// <c>AccountLockTimeoutTests</c>, que lê o banco compartilhado inteiro.
/// </summary>
[CollectionDefinition(nameof(ResendLockSerial), DisableParallelization = true)]
public sealed class ResendLockSerial;

/// <summary>
/// Issue #470: além do status, o tempo do reenvio (API-A03) não pode dizer que a conta existe e está travada. O e-mail
/// inexistente sai antes da trava e responde no piso (500 ms mais até 100 ms de jitter). A conta travada só chega ao
/// 202 depois de desistir da espera; se essa espera fosse o teto do login (<c>Identity:AccountLockTimeout</c>, 5 s em
/// produção), ela passaria do piso, e o jitter não esconde segundos numa amostra só. Aqui se afirma que a mediana das
/// duas fica dentro de <see cref="MedianDifferenceLimit"/>.
/// </summary>
/// <remarks>
/// O teto do login fica em 1,5 s, acima do piso com folga (o padrão de 5 s só deixaria o teste lento): se o reenvio
/// voltasse a esperá-lo, cada amostra da conta travada passaria de 1,5 s. A espera curta do reenvio fica no padrão.
/// </remarks>
[Collection(nameof(ResendLockSerial))]
public sealed class EmailConfirmationResendTimingTests : IAsyncDisposable
{
    private const string Requests = "/api/v1/auth/email-confirmation-requests";

    /// <summary>
    /// Diferença máxima entre as medianas. O jitter do piso é uniforme em 0–100 ms: a mediana de 11 amostras oscila
    /// ~10 ms, e a diferença de duas, ~15 ms; o limite dá folga para a máquina carregada. Fica muito abaixo do que o
    /// teste tem de pegar: com o teto do login, a diferença passa de 900 ms (1,5 s contra no máximo 600 ms).
    /// </summary>
    private static readonly TimeSpan MedianDifferenceLimit = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan LoginLockTimeout = TimeSpan.FromMilliseconds(1500);

    private const int WarmupRounds = 2;
    private const int Samples = 11;

    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;

    public EmailConfirmationResendTimingTests(PostgresFixture postgres)
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
        };
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ALockedPendingAccountAnswersInTheMedianTimeOfAMissingEmail()
    {
        var pending = await RegisterPendingAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var missingEmail = new List<double>();
        var lockedAccount = new List<double>();

        for (var round = 0; round < WarmupRounds + Samples; round++)
        {
            // Alterna a ordem a cada rodada para um efeito de posição não pender para um lado.
            var lockedFirst = round % 2 == 0;
            var first = lockedFirst
                ? await TimeLockedResendAsync(browser, pending)
                : await TimeResendAsync(browser, IdentityScenario.NewEmail());
            var second = lockedFirst
                ? await TimeResendAsync(browser, IdentityScenario.NewEmail())
                : await TimeLockedResendAsync(browser, pending);
            if (round >= WarmupRounds)
            {
                lockedAccount.Add((lockedFirst ? first : second).TotalMilliseconds);
                missingEmail.Add((lockedFirst ? second : first).TotalMilliseconds);
            }
        }

        var missing = Median(missingEmail);
        var locked = Median(lockedAccount);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"#470 medianas (N = {Samples}): e-mail inexistente {missing:F1} ms; conta travada {locked:F1} ms " +
            $"({locked - missing:+0.0;-0.0} ms); limite ±{MedianDifferenceLimit.TotalMilliseconds:F0} ms."));

        // A amostra da conta travada só vale se o reenvio desistiu pela trava: um Warning por pedido travado.
        Assert.Equal(
            WarmupRounds + Samples,
            _logs.Entries.Count(entry => entry.Text.Contains("account busy", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            Math.Abs(locked - missing) <= MedianDifferenceLimit.TotalMilliseconds,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Mediana da conta travada difere da do e-mail inexistente em {locked - missing:+0.0;-0.0} ms " +
                $"(limite ±{MedianDifferenceLimit.TotalMilliseconds:F0} ms)."));
    }

    private async Task<(Guid UserId, string Email)> RegisterPendingAsync()
    {
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(_factory);
        using var registered = await IdentityScenario.RegisterAsync(browser, email);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var link = await new IdentityScenario(_postgres, _factory).ReadLatestLinkAsync(email, "EmailConfirmation");
        return (link.UserId, email);
    }

    private static async Task<TimeSpan> TimeResendAsync(Browser browser, string email)
    {
        var elapsed = Stopwatch.StartNew();
        using var response = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        elapsed.Stop();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return elapsed.Elapsed;
    }

    /// <summary>Só o POST entra no tempo; travar e soltar a linha ficam de fora.</summary>
    private async Task<TimeSpan> TimeLockedResendAsync(Browser browser, (Guid UserId, string Email) pending)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(
            "select 1 from identity.users where id = $1 for update", connection, transaction))
        {
            command.Parameters.AddWithValue(pending.UserId);
            await command.ExecuteNonQueryAsync(ct);
        }

        var elapsed = await TimeResendAsync(browser, pending.Email);
        await transaction.RollbackAsync(ct);
        return elapsed;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
