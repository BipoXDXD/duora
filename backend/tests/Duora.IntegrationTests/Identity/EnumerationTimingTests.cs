using System.Diagnostics;
using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Enumeração por tempo (TM-06): os caminhos de conta existente, pendente e inexistente fazem o mesmo trabalho
/// caro. Mede-se o trabalho (comandos SQL e hashes), não o relógio; só o piso de latência do login é medido, e
/// apenas como limite inferior, que não oscila.
/// </summary>
[Trait("Threat", "TM-06")]
public sealed class EnumerationTimingTests(PostgresFixture postgres)
{
    private const string WrongPassword = "senha que nao confere com nada";

    [Fact]
    public async Task RegistrationDoesTheSameWorkWhetherTheEmailIsNewPendingOrConfirmed()
    {
        var recorder = new WorkRecorder();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { TestServices = recorder.Install };
        var scenario = new IdentityScenario(postgres, factory);
        var confirmed = await scenario.CreateConfirmedAccountAsync();
        var pending = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var pendingRegistration = await IdentityScenario.RegisterAsync(browser, pending);

        var forNew = await RecordAsync(recorder, () => browser.PostJsonAsync(
            "/api/v1/auth/register", IdentityScenario.RegistrationBody(IdentityScenario.NewEmail())));
        var forPending = await RecordAsync(recorder, () => browser.PostJsonAsync(
            "/api/v1/auth/register", IdentityScenario.RegistrationBody(pending)));
        var forConfirmed = await RecordAsync(recorder, () => browser.PostJsonAsync(
            "/api/v1/auth/register", IdentityScenario.RegistrationBody(confirmed.Email)));

        Assert.Single(forNew, step => step.StartsWith("hash: ", StringComparison.Ordinal));
        Assert.Equal(forNew, forPending);
        Assert.Equal(forNew, forConfirmed);
    }

    [Fact]
    public async Task PasswordResetRequestDoesTheSameWorkForEveryKindOfAddress()
    {
        var recorder = new WorkRecorder();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { TestServices = recorder.Install };
        var scenario = new IdentityScenario(postgres, factory);
        var confirmed = await scenario.CreateConfirmedAccountAsync();
        var pending = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var pendingRegistration = await IdentityScenario.RegisterAsync(browser, pending);

        var forUnknown = await RecordAsync(recorder, () => browser.PostJsonAsync(
            "/api/v1/auth/password-reset-requests", new { email = IdentityScenario.NewEmail() }));
        var forPending = await RecordAsync(recorder, () => browser.PostJsonAsync(
            "/api/v1/auth/password-reset-requests", new { email = pending }));
        var forConfirmed = await RecordAsync(recorder, () => browser.PostJsonAsync(
            "/api/v1/auth/password-reset-requests", new { email = confirmed.Email }));

        Assert.NotEmpty(forUnknown);
        Assert.Equal(forUnknown, forPending);
        Assert.Equal(forUnknown, forConfirmed);
    }

    [Fact]
    public async Task LoginDoesOneHashForUnknownUnconfirmedAndLockedAccounts()
    {
        var recorder = new WorkRecorder();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { TestServices = recorder.Install };
        var scenario = new IdentityScenario(postgres, factory);
        var locked = await scenario.CreateConfirmedAccountAsync();
        await scenario.ScalarAsync(
            "update identity.users set lockout_end = now() + interval '1 hour' where id = $1 returning 1",
            locked.UserId);
        var pending = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var pendingRegistration = await IdentityScenario.RegisterAsync(browser, pending);

        foreach (var email in new[] { IdentityScenario.NewEmail(), pending, locked.Email })
        {
            var work = await RecordAsync(recorder, () => IdentityScenario.LoginAsync(browser, email, WrongPassword));
            Assert.Single(work, step => step.StartsWith("hash: ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A senha errada numa conta existente grava a falha (lockout); os demais casos não gravam. O piso de
    /// latência das respostas de falha encobre essa diferença: nenhuma falha responde antes dele.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedLoginNeverAnswersBeforeTheLatencyFloor(bool accountExists)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = accountExists ? (await scenario.CreateConfirmedAccountAsync()).Email : IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        var stopwatch = Stopwatch.StartNew();

        using var response = await IdentityScenario.LoginAsync(browser, email, WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300), $"Falha respondeu em {stopwatch.Elapsed}.");
    }

    private static async Task<IReadOnlyList<string>> RecordAsync(
        WorkRecorder recorder,
        Func<Task<HttpResponseMessage>> send)
    {
        recorder.Clear();
        using var response = await send();
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized,
            $"Status {response.StatusCode}.");
        return recorder.Snapshot();
    }
}
