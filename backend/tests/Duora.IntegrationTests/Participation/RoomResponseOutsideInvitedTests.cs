using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-R01 fora de <c>Invited</c>: aceitar ou recusar uma sala que já passou da fase de convite não muda a sala, as
/// reservas nem as respostas gravadas. A única resposta que vale é o aceite repetido de uma sala <c>Active</c>, que
/// segue 204 como no aceite repetido ainda em <c>Invited</c>.
/// </summary>
public sealed class RoomResponseOutsideInvitedTests(PostgresFixture postgres)
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Active", "accept", HttpStatusCode.NoContent)]
    [InlineData("Active", "reject", HttpStatusCode.NotFound)]
    [InlineData("Completed", "accept", HttpStatusCode.NotFound)]
    [InlineData("Completed", "reject", HttpStatusCode.NotFound)]
    [InlineData("Expired", "accept", HttpStatusCode.NotFound)]
    [InlineData("Expired", "reject", HttpStatusCode.NotFound)]
    public async Task AnswerToARoomThatIsNoLongerInvitedChangesNothing(string state, string answer, HttpStatusCode expected)
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        await PutTheRoomInAsync(scene, state);
        var before = await scene.RoomFactsAsync();
        Assert.Equal(state, before.State);
        using var alice = await scene.SignInAsync(scene.Seed.Alice);

        using var response = await alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/{answer}", null);

        if (expected == HttpStatusCode.NotFound)
        {
            await ProblemAssert.IsProblemAsync(response, expected, "NOT_FOUND");
        }
        else
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(before, await scene.RoomFactsAsync());
    }

    /// <summary>Aceitar de novo ainda em <c>Invited</c> responde 204 sem regravar o aceite nem mexer na versão da sala.</summary>
    [Fact]
    public async Task AcceptingAgainWhileStillInvitedKeepsTheFirstAcceptAndTheRoomVersion()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        using var alice = await scene.SignInAsync(scene.Seed.Alice);
        using (var first = await alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/accept", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        var accepted = await scene.RoomFactsAsync();
        Assert.Equal("Invited", accepted.State);
        Assert.NotNull(accepted.AliceAcceptedAt);
        Assert.Null(accepted.BobAcceptedAt);
        scene.Clock.Advance(TenSeconds);

        using var second = await alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/accept", null);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(string.Empty, await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(accepted, await scene.RoomFactsAsync());
    }

    private static async Task PutTheRoomInAsync(ParticipationScene scene, string state)
    {
        switch (state)
        {
            case "Active":
                await scene.AcceptBothAsync();
                break;
            case "Completed":
                await scene.AcceptBothAsync();
                await scene.CompleteTheGameAsync();
                break;
            case "Expired":
                scene.Clock.SetUtcNow(await scene.InviteDeadlineAsync());
                await scene.IsolateJobAsync("expire-invite");
                Assert.True(await scene.Service.CompleteJobAsync(await scene.ClaimAsync("expire-invite"), Ct));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Estado sem preparo definido.");
        }
    }
}
