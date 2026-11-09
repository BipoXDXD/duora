using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Safety;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-R04: a saída segura vale também para quem deixou de ser elegível. Só a exigência de elegibilidade sai da rota:
/// sessão, CSRF, conta de participante, participação na sala e o portão da conta suspensa continuam valendo.
/// </summary>
public sealed class RoomExitEligibilityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Restricted", "Active")]
    [InlineData("Restricted", "Invited")]
    [InlineData("PendingEligibility", "Active")]
    [InlineData("PendingEmail", "Active")]
    [InlineData("DeletionPending", "Active")]
    [InlineData("email-not-confirmed", "Active")]
    [InlineData("selfie-not-passed", "Active")]
    public async Task AnIneligibleParticipantLeavesReleasesBothAndCanRepeat(string ineligibleState, string openState)
    {
        await using var scene = await OpenRoomAsync(openState);
        var before = await scene.RoomRowAsync();
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        using var bob = await scene.SignInAsync("bob", scene.Seed.Bob);
        await scene.MakeIneligibleAsync(scene.Seed.Alice, ineligibleState);

        using (var exit = await alice.PostJsonAsync(scene.ExitPath, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        }

        var after = (State: "Aborted", Version: before.Version + 1);
        Assert.Equal(after, await scene.RoomRowAsync());
        Assert.Equal(0L, await scene.ActiveReservationsAsync());
        var departedAt = await scene.DepartedAtAsync(scene.Seed.Alice);
        Assert.NotNull(departedAt);
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Bob));

        // Com uma das contas inelegível a sala some para as duas, igual a sala inexistente, sem revelar o motivo.
        using (var partnerRoom = await bob.GetAsync(scene.RoomPath))
        {
            Assert.Equal(HttpStatusCode.NotFound, partnerRoom.StatusCode);
        }

        using (var partnerStatus = await bob.GetAsync($"/api/v1/events/{scene.Seed.Events[0]}/participation"))
        {
            Assert.Equal(HttpStatusCode.OK, partnerStatus.StatusCode);
            using var body = JsonDocument.Parse(await partnerStatus.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Waiting", body.RootElement.GetProperty("state").GetString());
        }

        scene.Clock.Advance(TimeSpan.FromSeconds(5));
        using (var repeated = await alice.PostJsonAsync(scene.ExitPath, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        }

        Assert.Equal(after, await scene.RoomRowAsync());
        Assert.Equal(0L, await scene.ActiveReservationsAsync());
        Assert.Equal(departedAt, await scene.DepartedAtAsync(scene.Seed.Alice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAccountOutsideTheRoomGets404WithoutEffectOrForeignData(bool outsiderIsIneligible)
    {
        await using var scene = await OpenRoomAsync("Active");
        var before = await scene.RoomRowAsync();
        using var third = await scene.SignInAsync("third", scene.Seed.Third);
        if (outsiderIsIneligible)
        {
            await scene.MakeIneligibleAsync(scene.Seed.Third, "Restricted");
        }

        using var exit = await third.PostJsonAsync(scene.ExitPath, null);

        Assert.Equal(HttpStatusCode.NotFound, exit.StatusCode);
        var body = await exit.Content.ReadAsStringAsync(Ct);
        foreach (var foreignId in new[] { scene.Room, scene.Seed.Alice, scene.Seed.Bob })
        {
            Assert.DoesNotContain(foreignId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Active", body, StringComparison.Ordinal);
        Assert.Equal(before, await scene.RoomRowAsync());
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
    }

    [Fact]
    public async Task ExitWithoutSessionAnswers401AndChangesNothing()
    {
        await using var scene = await OpenRoomAsync("Active");
        var before = await scene.RoomRowAsync();
        using var anonymous = new Browser(scene.Factory);
        await anonymous.FetchCsrfTokenAsync();

        using var exit = await anonymous.PostJsonAsync(scene.ExitPath, null);

        await ProblemAssert.IsProblemAsync(exit, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal(before, await scene.RoomRowAsync());
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
    }

    [Fact]
    public async Task AnIneligibleParticipantStillNeedsTheCsrfToken()
    {
        await using var scene = await OpenRoomAsync("Active");
        var before = await scene.RoomRowAsync();
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        await scene.MakeIneligibleAsync(scene.Seed.Alice, "Restricted");

        using var exit = await alice.PostJsonAsync(scene.ExitPath, null, new MutationHeaders(SendCsrfToken: false));

        await ProblemAssert.IsProblemAsync(exit, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.Equal(before, await scene.RoomRowAsync());
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
    }

    [Fact]
    public async Task LockContentionOnExitAnswers503WithRetryAfterAndChangesNothing()
    {
        await using var scene = await OpenRoomAsync("Active");
        var before = await scene.RoomRowAsync();
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        await scene.MakeIneligibleAsync(scene.Seed.Alice, "Restricted");
        await using var holder = await scene.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await Execute(holder, "select 1 from safety.user_guards where user_id=$1 for update", Ct, scene.Seed.Alice);

        using var exit = await alice.PostJsonAsync(scene.ExitPath, null);

        Assert.Equal(TimeSpan.FromSeconds(2), exit.Headers.RetryAfter?.Delta);
        await ProblemAssert.IsProblemAsync(exit, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        await transaction.RollbackAsync(Ct);
        Assert.Equal(before, await scene.RoomRowAsync());
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Alice));
    }

    [Fact]
    public async Task APrivilegedAccountStaysForbidden()
    {
        await using var scene = await OpenRoomAsync("Active");
        var before = await scene.RoomRowAsync();
        var suspensions = new AccountSuspensionScenario(scene.ConnectionString, scene.Factory, scene.Clock);
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using (moderator)
        {
            using var exit = await moderator.PostJsonAsync(scene.ExitPath, null);

            Assert.Equal(HttpStatusCode.Forbidden, exit.StatusCode);
        }

        Assert.Equal(before, await scene.RoomRowAsync());
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
    }

    /// <summary>A suspensão real já aborta a sala e libera as reservas na própria transação; o portão da conta suspensa não muda.</summary>
    [Fact]
    public async Task ASuspendedParticipantIsAlreadyOutAndTheSuspensionGateStays()
    {
        await using var scene = await OpenRoomAsync("Active");
        var suspensions = new AccountSuspensionScenario(scene.ConnectionString, scene.Factory, scene.Clock);
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using (moderator)
        {
            await AccountSuspensionScenario.SuspendAsync(moderator, scene.Seed.Alice);
        }

        var suspended = await scene.RoomRowAsync();
        Assert.Equal("Aborted", suspended.State);
        Assert.Equal(0L, await scene.ActiveReservationsAsync());

        using var exit = await alice.PostJsonAsync(scene.ExitPath, null);

        await ProblemAssert.IsProblemAsync(exit, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
        Assert.Equal(suspended, await scene.RoomRowAsync());
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Alice));
    }

    private async Task<OpenRoom> OpenRoomAsync(string openState)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, Ct));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], Ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, Ct))!.RoomId!.Value;
        if (openState == "Active")
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, Ct));
        }

        return new OpenRoom(connectionString, source, clock, factory, new IdentityScenario(connectionString, factory), seed, room);
    }

    private sealed class OpenRoom(string connectionString, NpgsqlDataSource source, FakeTimeProvider clock, DuoraWebFactory factory, IdentityScenario identity, ParticipationSeed seed, Guid room) : IAsyncDisposable
    {
        public string ConnectionString => connectionString;

        public FakeTimeProvider Clock => clock;

        public DuoraWebFactory Factory => factory;

        public ParticipationSeed Seed => seed;

        public Guid Room => room;

        public string RoomPath => $"/api/v1/rooms/{room}";

        public string ExitPath => $"{RoomPath}/exit";

        public Task<Browser> SignInAsync(string name, Guid user) =>
            identity.SignedInBrowserAsync(new(user, $"{name}@example.com", IdentityScenario.ValidPassword));

        public ValueTask<NpgsqlConnection> OpenConnectionAsync() => source.OpenConnectionAsync(Ct);

        public async Task MakeIneligibleAsync(Guid user, string ineligibleState)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            var changed = ineligibleState switch
            {
                "email-not-confirmed" => await Execute(connection, "update identity.users set email_confirmed=false where id=$1", Ct, user),
                "selfie-not-passed" => await Execute(connection, "delete from identity.selfie_verifications where user_id=$1", Ct, user),
                _ => await Execute(connection, "update identity.users set status=$2 where id=$1", Ct, user, ineligibleState),
            };
            Assert.Equal(1, changed);
        }

        public async Task<(string State, long Version)> RoomRowAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select state,version from experiences.game_sessions where id=$1", room);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetString(0), reader.GetInt64(1));
        }

        public async Task<long> ActiveReservationsAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select count(*) from events.pairing_participants where user_id=any($1) and released_at is null", new[] { seed.Alice, seed.Bob });
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task<DateTimeOffset?> DepartedAtAsync(Guid user)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select departed_at from experiences.game_participants where game_session_id=$1 and user_id=$2", room, user);
            return await query.ExecuteScalarAsync(Ct) is DateTime departedAt ? new DateTimeOffset(departedAt) : null;
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
