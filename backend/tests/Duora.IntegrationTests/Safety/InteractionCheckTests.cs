using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// `CheckInteraction` (INV-03, spec 7.3): o bloqueio é direcional no registro e bilateral no efeito, vale a partir do
/// commit e responde de forma neutra, sem dizer quem bloqueou.
/// </summary>
public sealed class InteractionCheckTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task PeopleWithoutBlockMayInteract()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var ana = await scenario.CreateAccountAsync();
        var bruno = await scenario.CreateAccountAsync();

        Assert.Equal(InteractionVerdict.Allowed, await CheckAsync(ana.UserId, bruno.UserId));
    }

    [Fact]
    public async Task BlockSeparatesThePairInBothDirections()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var ana = await scenario.CreateAccountAsync();
        var bruno = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(ana);

        using (await browser.PutAsync(SafetyScenario.BlockPath(bruno.UserId)))
        {
        }

        Assert.Equal(InteractionVerdict.Unavailable, await CheckAsync(ana.UserId, bruno.UserId));
        Assert.Equal(InteractionVerdict.Unavailable, await CheckAsync(bruno.UserId, ana.UserId));
    }

    [Fact]
    public async Task BlockDoesNotReachThirdParties()
    {
        var scenario = new SafetyScenario(postgres, _factory);
        var ana = await scenario.CreateAccountAsync();
        var bruno = await scenario.CreateAccountAsync();
        var carla = await scenario.CreateAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(ana);

        using (await browser.PutAsync(SafetyScenario.BlockPath(bruno.UserId)))
        {
        }

        Assert.Equal(InteractionVerdict.Allowed, await CheckAsync(ana.UserId, carla.UserId));
        Assert.Equal(InteractionVerdict.Allowed, await CheckAsync(bruno.UserId, carla.UserId));
    }

    [Fact]
    public async Task InteractionWithOneselfIsAProgrammingError() =>
        await Assert.ThrowsAsync<ArgumentException>(() => CheckAsync(Guid.CreateVersion7(), null));

    private async Task<InteractionVerdict> CheckAsync(Guid personA, Guid? personB)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var blocks = scope.ServiceProvider.GetRequiredService<BlockService>();
        return await blocks.CheckInteractionAsync(personA, personB ?? personA, Ct);
    }
}
