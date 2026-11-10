using Duora.Infrastructure.Participation;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Rodada cancelada aparece na leitura que a tela de encontros já consulta por polling (spec 5.2: "emitir notificações
/// neutras"). A leitura diz só que a rodada foi cancelada, nunca por quê, e o aviso vale até começar outra rodada do
/// mesmo encontro.
/// </summary>
public sealed class CancelledRoundNoticeTests(PostgresFixture postgres)
{
    private const int MaximumDueJobs = 40;
    private const int SecondsPastStartThatCancelTheBuild = 61;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACheckedInPersonWaitingForARoundThatHasNotStartedSeesNoCancellation()
    {
        await using var scene = await CreateSceneAsync();
        await scene.CheckInAsync(scene.Seed.Alice);

        var status = await scene.StatusAsync(scene.Seed.Alice);

        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: false), status);
    }

    [Fact]
    public async Task ACheckedInPersonSeesTheRoundCancelledWhenTheBuildRunsTooLate()
    {
        await using var scene = await CreateSceneAsync();
        await scene.CheckInAsync(scene.Seed.Alice);
        await scene.CheckInAsync(scene.Seed.Bob);

        await scene.CancelFirstRoundAsync();

        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: true), await scene.StatusAsync(scene.Seed.Alice));
        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: true), await scene.StatusAsync(scene.Seed.Bob));
    }

    [Fact]
    public async Task APersonRegisteredButNotCheckedInIsNotToldAboutTheCancelledRound()
    {
        await using var scene = await CreateSceneAsync();
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(scene.EventId, scene.Seed.Alice, checkIn: false, Ct));

        await scene.CancelFirstRoundAsync();

        Assert.Equal(new ParticipationStatus("Confirmed", null, RoundCancelled: false), await scene.StatusAsync(scene.Seed.Alice));
    }

    [Fact]
    public async Task APersonWhoIsNotRegisteredIsNotToldAboutTheCancelledRound()
    {
        await using var scene = await CreateSceneAsync();

        await scene.CancelFirstRoundAsync();

        Assert.Equal(new ParticipationStatus("Unregistered", null, RoundCancelled: false), await scene.StatusAsync(scene.Seed.Third));
    }

    [Fact]
    public async Task TheNoticeGoesAwayWhenTheNextRoundStartsAndPairsThePerson()
    {
        await using var scene = await CreateSceneAsync();
        await scene.CheckInAsync(scene.Seed.Alice);
        await scene.CheckInAsync(scene.Seed.Bob);
        await scene.CancelFirstRoundAsync();

        await scene.StartSecondRoundAsync();

        var status = await scene.StatusAsync(scene.Seed.Alice);
        Assert.Equal("Invited", status!.State);
        Assert.NotNull(status.RoomId);
        Assert.False(status.RoundCancelled);
    }

    [Fact]
    public async Task TheNoticeGoesAwayWhenTheNextRoundStartsEvenIfThePersonIsStillWaiting()
    {
        await using var scene = await CreateSceneAsync();
        await scene.CheckInAsync(scene.Seed.Alice);
        await scene.CancelFirstRoundAsync();

        await scene.StartSecondRoundAsync();

        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: false), await scene.StatusAsync(scene.Seed.Alice));
    }

    [Fact]
    public async Task ANextRoundThatIsOnlyScheduledKeepsTheNotice()
    {
        await using var scene = await CreateSceneAsync();
        await scene.CheckInAsync(scene.Seed.Alice);
        await scene.CancelFirstRoundAsync();

        await scene.ScheduleSecondRoundAsync();

        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: true), await scene.StatusAsync(scene.Seed.Alice));
    }

    [Fact]
    public async Task APersonWhoseCheckInIsLaterThanTheCancelledRoundStartIsNotToldAboutIt()
    {
        await using var scene = await CreateSceneAsync();
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(scene.EventId, scene.Seed.Alice, checkIn: false, Ct));
        await scene.CancelFirstRoundAsync();

        // O serviço fecha o check-in no início do encontro; o estado é gravado direto para fixar a regra da leitura.
        await scene.MarkCheckedInAsync(scene.Seed.Alice, scene.StartsAt.AddSeconds(SecondsPastStartThatCancelTheBuild + 1));

        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: false), await scene.StatusAsync(scene.Seed.Alice));
    }

    [Fact]
    public async Task APersonCheckedInExactlyWhenTheCancelledRoundStartedIsTold()
    {
        await using var scene = await CreateSceneAsync();
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(scene.EventId, scene.Seed.Alice, checkIn: false, Ct));
        await scene.CancelFirstRoundAsync();

        await scene.MarkCheckedInAsync(scene.Seed.Alice, scene.StartsAt);

        Assert.Equal(new ParticipationStatus("Waiting", null, RoundCancelled: true), await scene.StatusAsync(scene.Seed.Alice));
    }

    private async Task<Scene> CreateSceneAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var source = NpgsqlDataSource.Create(connectionString);
        var clock = new FakeTimeProvider(now);
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select starts_at from events.rounds where id=$1", seed.Rounds[0]);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return new Scene(source, clock, new ParticipationService(source, clock), seed, reader.GetFieldValue<DateTimeOffset>(0));
    }

    private sealed record Scene(NpgsqlDataSource Source, FakeTimeProvider Clock, ParticipationService Service, ParticipationSeed Seed, DateTimeOffset StartsAt)
        : IAsyncDisposable
    {
        private readonly Guid _secondRound = Guid.NewGuid();

        public Guid EventId => Seed.Events[0];

        private DateTimeOffset SecondRoundStartsAt => StartsAt.AddMinutes(2);

        public async Task CheckInAsync(Guid user)
        {
            Assert.Equal(ParticipationOutcome.Done, await Service.RegisterAsync(EventId, user, checkIn: false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await Service.RegisterAsync(EventId, user, checkIn: true, Ct));
        }

        public Task<ParticipationStatus?> StatusAsync(Guid user) => Service.StatusAsync(EventId, user, Ct);

        public async Task CancelFirstRoundAsync()
        {
            Clock.SetUtcNow(StartsAt.AddSeconds(SecondsPastStartThatCancelTheBuild));
            await RunDueJobsAsync(Service);
            Assert.Equal("Cancelled", await RoundStateAsync(Seed.Rounds[0]));
        }

        public async Task ScheduleSecondRoundAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,2,'Scheduled',$3,$4,43)",
                Ct, _secondRound, EventId, SecondRoundStartsAt, SecondRoundStartsAt.AddMinutes(12));
        }

        public async Task StartSecondRoundAsync()
        {
            await ScheduleSecondRoundAsync();
            Clock.SetUtcNow(SecondRoundStartsAt);
            await Service.BuildRoundPairingsAsync(_secondRound, Ct);
            Assert.Equal("Inviting", await RoundStateAsync(_secondRound));
        }

        public async Task MarkCheckedInAsync(Guid user, DateTimeOffset checkedInAt)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            Assert.Equal(1, await Execute(connection, "update events.registrations set state='CheckedIn',checked_in_at=$3 where event_id=$1 and user_id=$2",
                Ct, EventId, user, checkedInAt));
        }

        private async Task<string> RoundStateAsync(Guid round)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select state from events.rounds where id=$1", round);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }

        private static async Task RunDueJobsAsync(ParticipationService service)
        {
            for (var attempt = 0; attempt < MaximumDueJobs; attempt++)
            {
                var job = await service.ClaimJobAsync(Ct);
                if (job is null)
                {
                    return;
                }

                Assert.True(await service.CompleteJobAsync(job, Ct), $"O job {job.Kind} recém-entregue não foi concluído.");
            }

            throw new InvalidOperationException("A fila de jobs não esvaziou.");
        }

        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
