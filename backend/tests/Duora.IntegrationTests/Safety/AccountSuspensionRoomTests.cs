using System.Net;
using Duora.Core.Safety;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Participation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Suspensão com sala aberta (spec 8.2, 7.3; issue #299): a sala da pessoa suspensa é abortada e as reservas do par
/// liberadas na mesma transação da medida, como no bloqueio; a outra pessoa não fica presa numa sala morta.
/// </summary>
public sealed class AccountSuspensionRoomTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SuspendingAPersonInAnActiveRoomAbortsItAndReleasesTheReservations()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        await using var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, ct));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value;
        await service.RespondAsync(room, seed.Alice, true, ct);
        await service.RespondAsync(room, seed.Bob, true, ct);
        var identity = new IdentityScenario(connectionString, factory);
        using var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<AccountSuspensions>().SuspendAutomaticallyAsync(
                SuspensionOrder.Automated(seed.Bob, SuspensionReason.SelfieVerificationFailed, "Limite de tentativas."),
                ModerationCommandKey.ForSystem("selfie-verification", Guid.NewGuid()),
                ct);
            Assert.IsType<SuspensionOutcome.Suspended>(outcome);
        }

        using var read = await bob.GetAsync($"/api/v1/rooms/{room}");
        await ProblemAssert.IsProblemAsync(read, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var reservations = Command(c,
            "select count(*) from events.pairing_participants where pairing_id=(select pairing_id from experiences.game_sessions where id=$1) and released_at is null",
            room);
        Assert.Equal(0L, await reservations.ExecuteScalarAsync(ct));
        await using var state = Command(c, "select state from experiences.game_sessions where id=$1", room);
        Assert.Equal("Aborted", await state.ExecuteScalarAsync(ct));
    }
}
