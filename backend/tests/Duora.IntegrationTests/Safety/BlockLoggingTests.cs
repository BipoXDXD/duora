using System.Net;

namespace Duora.IntegrationTests.Safety;

/// <summary>Bloqueio é dado P3 (model.md): quem bloqueou quem não vai para log (spec 19.2), só para a auditoria.</summary>
public sealed class BlockLoggingTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;

    public BlockLoggingTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _logs.Dispose();
    }

    [Fact]
    public async Task NoLogLineMentionsTheBlockedPerson()
    {
        var scenario = new SafetyScenario(_postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var blocked = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(blocker);

        using var blockedResponse = await browser.PutAsync(SafetyScenario.BlockPath(blocked.UserId));
        using var selfResponse = await browser.PutAsync(SafetyScenario.BlockPath(blocker.UserId));

        Assert.Equal(HttpStatusCode.NoContent, blockedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, selfResponse.StatusCode);
        Assert.DoesNotContain(
            _logs.Entries,
            entry => entry.Text.Contains(blocked.UserId.ToString(), StringComparison.OrdinalIgnoreCase)
                || (entry.Exception?.ToString().Contains(blocked.UserId.ToString(), StringComparison.OrdinalIgnoreCase) ?? false));
    }
}
