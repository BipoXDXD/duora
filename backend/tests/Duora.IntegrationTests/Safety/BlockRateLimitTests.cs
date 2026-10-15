using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

/// <summary>TM-26 e G6: limite em memória por conta, não por sessão nem por IP.</summary>
public sealed class BlockRateLimitTests(PostgresFixture postgres) : IAsyncDisposable
{
    private const int Limit = 2;

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString)
    {
        Settings = new Dictionary<string, string?>
        {
            ["RateLimiting:BlockPerAccount:PermitLimit"] = $"{Limit}",
            ["RateLimiting:BlockPerAccount:Window"] = "01:00:00",
        },
    };

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AttemptAboveTheLimitGets429WithRetryAfterEvenFromAnotherSession()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var blocker = await scenario.CreateAccountAsync();
        var bystander = await scenario.CreateAccountAsync();
        using var first = await scenario.SignedInBrowserAsync(blocker);
        using var second = await scenario.SignedInBrowserAsync(blocker);
        for (var attempt = 0; attempt < Limit; attempt++)
        {
            using var allowed = await first.PutAsync(SafetyScenario.BlockPath(Guid.CreateVersion7()));
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        }

        using var limited = await second.PutAsync(SafetyScenario.BlockPath(bystander.UserId));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.InRange(ProblemAssert.RetryAfterSeconds(limited), 1, 3600);
        Assert.Equal(0, (await scenario.ReadAsync(blocker.UserId, bystander.UserId)).Rows);
    }

    [Fact]
    public async Task OneAccountReachingTheLimitDoesNotLimitAnother()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var noisy = await scenario.CreateAccountAsync();
        var other = await scenario.CreateAccountAsync();
        var target = await scenario.CreateAccountAsync();
        using var noisyBrowser = await scenario.SignedInBrowserAsync(noisy);
        for (var attempt = 0; attempt <= Limit; attempt++)
        {
            using (await noisyBrowser.PutAsync(SafetyScenario.BlockPath(Guid.CreateVersion7())))
            {
            }
        }

        using var otherBrowser = await scenario.SignedInBrowserAsync(other);
        using var response = await otherBrowser.PutAsync(SafetyScenario.BlockPath(target.UserId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
