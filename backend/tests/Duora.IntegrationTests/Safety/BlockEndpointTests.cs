using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-S01 `PUT /me/blocks/{targetUserId}` (spec 8.4, 7.3; INV-03; TM-04, TM-05, TM-24, TM-26). Decisões G3, G4,
/// G6 e G7 de 2026-10-15.
/// </summary>
public sealed class BlockEndpointTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task BlockingAnotherPersonRecordsOneDirectionalRowBumpsBothGuardsAndAudits()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var blocked = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(blocked.UserId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new BlockState(Rows: 1, Audits: 1, BlockerVersion: 1, BlockedVersion: 1), await scenario.ReadAsync(blocker.UserId, blocked.UserId));
        Assert.Equal(0, (await scenario.ReadAsync(blocked.UserId, blocker.UserId)).Rows);
    }

    [Fact]
    public async Task RepeatingTheBlockChangesNothing()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var blocked = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);
        using (await browser.PutAsync(SafetyScenario.BlockPath(blocked.UserId)))
        {
        }

        using var again = await browser.PutAsync(SafetyScenario.BlockPath(blocked.UserId));

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(new BlockState(1, 1, 1, 1), await scenario.ReadAsync(blocker.UserId, blocked.UserId));
    }

    [Fact]
    public async Task BlockingAnUnknownIdAnswers204WithoutLeavingAnyTrace()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var unknown = Guid.CreateVersion7();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(unknown));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await scenario.CountTracesOfAsync(unknown));
        Assert.Equal(0, await scenario.CountTracesOfAsync(blocker.UserId));
    }

    [Fact]
    public async Task BlockingTheEmptyUuidAnswers204WithoutLeavingAnyTrace()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(Guid.Empty));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await scenario.CountTracesOfAsync(blocker.UserId));
    }

    /// <summary>
    /// TM-24 e G3: quem bloqueia não distingue alvo existente, inexistente, já bloqueado ou que já o bloqueou, e o
    /// alvo não fica sabendo de nada pela resposta.
    /// </summary>
    [Fact]
    public async Task ResponseIsTheSameWhateverTheTargetState()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var fresh = await scenario.CreateAccountAsync();
        var alreadyBlocked = await scenario.CreateAccountAsync();
        var blockedMeFirst = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);
        using (await browser.PutAsync(SafetyScenario.BlockPath(alreadyBlocked.UserId)))
        {
        }

        using (var other = await scenario.SignedInBrowserAsync(blockedMeFirst))
        using (await other.PutAsync(SafetyScenario.BlockPath(blocker.UserId)))
        {
        }

        var answers = new List<string>();
        foreach (var target in new[] { fresh.UserId, Guid.CreateVersion7(), alreadyBlocked.UserId, blockedMeFirst.UserId })
        {
            using var response = await browser.PutAsync(SafetyScenario.BlockPath(target));
            answers.Add(await DescribeAsync(response));
        }

        Assert.Single(answers.Distinct());
        Assert.Equal("204||no-store", answers[0]);
    }

    [Fact]
    public async Task SelfBlockIsRejectedWith400()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var account = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(account.UserId));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "SELF_BLOCK");
        Assert.Equal(0, await scenario.CountTracesOfAsync(account.UserId));
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task ReachableAccountsMayBlock(string status)
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync(status);
        var blocked = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(blocked.UserId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await scenario.ReadAsync(blocker.UserId, blocked.UserId)).Rows);
    }

    [Theory]
    [InlineData("PendingEmail")]
    [InlineData("PendingEligibility")]
    [InlineData("DeletionPending")]
    public async Task PendingAccountsAreForbiddenAndNothingIsWritten(string status)
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var blocked = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);
        await scenario.SetStatusAsync(blocker.UserId, status);

        using var response = await browser.PutAsync(SafetyScenario.BlockPath(blocked.UserId));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_NOT_ELIGIBLE");
        Assert.Equal(0, await scenario.CountTracesOfAsync(blocker.UserId));
    }

    /// <summary>TM-04: quem bloqueia vem da sessão; o PUT não tem corpo, e corpo com campos é recusado.</summary>
    [Theory]
    [InlineData("""{"blockerId":"BLOCKED"}""")]
    [InlineData("""{"status":"Active"}""")]
    [InlineData("{}")]
    public async Task BodyIsRejectedAndNothingIsWritten(string json)
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var blocked = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var response = await browser.PutAsync(
            SafetyScenario.BlockPath(blocked.UserId),
            json.Replace("BLOCKED", blocked.UserId.ToString(), StringComparison.Ordinal));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        Assert.Equal(0, await scenario.CountTracesOfAsync(blocker.UserId));
    }

    [Fact]
    public async Task TargetThatIsNotAUuidIsNotFound()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var response = await browser.PutAsync("/api/v1/me/blocks/nao-e-uuid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await scenario.CountTracesOfAsync(blocker.UserId));
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return $"{(int)response.StatusCode}|{body}|{response.Headers.CacheControl}";
    }
}
