using Duora.Infrastructure.Participation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;
using static Duora.IntegrationTests.TestTime;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// O estado final da rodada no <c>close-round</c>, pelas chamadas do worker. A regra é a de <c>Round</c> no domínio:
/// só a rodada em andamento encerra, a que não chegou a começar é cancelada e a que já terminou não muda. No banco,
/// <c>Inviting</c> representa a rodada em andamento, porque nenhuma escrita grava <c>Running</c>.
/// </summary>
public sealed class RoundCloseStateTests(PostgresFixture postgres)
{
    private const int MaximumDueJobs = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARoundCancelledByALateBuildStaysCancelledWhenItsCloseJobRuns()
    {
        await using var scene = await CreateSceneAsync();
        var service = new ParticipationService(scene.Source, scene.Clock);
        scene.Clock.SetUtcNow(scene.StartsAt.AddSeconds(61));
        await RunDueJobsAsync(service);
        var cancelled = await scene.RoundRowAsync();
        Assert.Equal("Cancelled", cancelled.State);

        scene.Clock.SetUtcNow(scene.Deadline);
        await RunDueJobsAsync(service);

        var after = await scene.RoundRowAsync();
        Assert.Equal("Cancelled", after.State);
        Assert.Equal(cancelled.Version, after.Version);
        Assert.Equal("Completed", await scene.EventStateAsync());
    }

    [Fact]
    public async Task ARoundThatNeverStartedIsCancelledAtItsDeadlineAndStaysCancelledAfterMatchingResumes()
    {
        await using var scene = await CreateSceneAsync();
        var scheduled = await scene.RoundRowAsync();
        Assert.Equal("Scheduled", scheduled.State);
        var paused = new ParticipationService(scene.Source, scene.Clock, Options.Create(new ParticipationOptions { MatchingEnabled = false }));

        scene.Clock.SetUtcNow(scene.Deadline);
        await RunDueJobsAsync(paused);

        var after = await scene.RoundRowAsync();
        Assert.Equal("Cancelled", after.State);
        Assert.Equal(scheduled.Version + 1, after.Version);
        Assert.Equal("Completed", await scene.EventStateAsync());

        await RunDueJobsAsync(new ParticipationService(scene.Source, scene.Clock));

        Assert.Equal(after, await scene.RoundRowAsync());
    }

    [Fact]
    public async Task ARoundInProgressStillClosesAtItsDeadline()
    {
        await using var scene = await CreateSceneAsync();
        var service = new ParticipationService(scene.Source, scene.Clock);
        scene.Clock.SetUtcNow(scene.StartsAt);
        await RunDueJobsAsync(service);
        var inProgress = await scene.RoundRowAsync();
        Assert.Equal("Inviting", inProgress.State);

        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        await RunDueJobsAsync(service);
        Assert.Equal(inProgress, await scene.RoundRowAsync());

        scene.Clock.SetUtcNow(scene.Deadline);
        await RunDueJobsAsync(service);

        var after = await scene.RoundRowAsync();
        Assert.Equal("Closed", after.State);
        Assert.Equal(inProgress.Version + 1, after.Version);
        Assert.Equal("Completed", await scene.EventStateAsync());
    }

    private async Task<Scene> CreateSceneAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select starts_at,deadline_at from events.rounds where id=$1", seed.Rounds[0]);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return new Scene(source, new FakeTimeProvider(now), seed.Events[0], seed.Rounds[0],
            reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1));
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

    private sealed record Scene(NpgsqlDataSource Source, FakeTimeProvider Clock, Guid EventId, Guid RoundId, DateTimeOffset StartsAt, DateTimeOffset Deadline) : IAsyncDisposable
    {
        public async Task<(string State, long Version)> RoundRowAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select state,version from events.rounds where id=$1", RoundId);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetString(0), reader.GetInt64(1));
        }

        public async Task<string> EventStateAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select state from events.events where id=$1", EventId);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }

        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
