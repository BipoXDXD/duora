using System.Globalization;
using System.Net;
using System.Text.Json;
using Duora.Contracts.Participation;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;
using static Duora.IntegrationTests.TestTime;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// O prazo da sala ativa (T-18, INV-08) no PostgreSQL real, com relógio falso, pelas duas chamadas que o worker faz em
/// produção: <c>ClaimJobAsync</c> e <c>CompleteJobAsync</c> do job <c>game-deadline</c>, e a ação HTTP que chega entre o
/// prazo e o job.
/// </summary>
public sealed class GameDeadlineTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheGameDeadlineJobIsNotDeliveredOneTickBeforeTheDeadlineAndExpiresTheRoomAtIt()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        Assert.Equal(("Active", 2L, 0L), (active.State, active.ActiveReservations, active.Actions));

        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        Assert.Null(await scene.Service.ClaimJobAsync(Ct));
        Assert.Equal("Pending|0", await scene.GameDeadlineJobAsync());
        Assert.Equal(active, await scene.RoomFactsAsync());

        scene.Clock.SetUtcNow(scene.Deadline);
        var job = await scene.Service.ClaimJobAsync(Ct);

        Assert.NotNull(job);
        Assert.Equal(("game-deadline", scene.Room), (job.Kind, job.AggregateId));
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        Assert.Equal(new RoomFacts("Expired", active.Version + 1, ActiveReservations: 0, Actions: 0), await scene.RoomFactsAsync());
        Assert.Equal("Done|1", await scene.GameDeadlineJobAsync());
    }

    /// <summary>
    /// A fila não entrega antes do prazo; aqui o job é adiantado à mão para exercitar a segunda barreira, a do próprio motor.
    /// Entregue cedo, o job volta à fila para o próprio prazo sem ser consumido nem gastar tentativa, e expira a sala quando o
    /// relógio chega lá.
    /// </summary>
    [Fact]
    public async Task AGameDeadlineJobDeliveredOneTickEarlyReturnsToTheQueueAndExpiresTheRoomAtTheDeadline()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        await scene.AnticipateGameDeadlineJobAsync();

        var early = await scene.Service.ClaimJobAsync(Ct);

        Assert.NotNull(early);
        Assert.Equal(("game-deadline", scene.Room), (early.Kind, early.AggregateId));
        Assert.False(await scene.Service.CompleteJobAsync(early, Ct));
        Assert.Equal(active, await scene.RoomFactsAsync());
        Assert.Equal(new GameDeadlineJob("Pending", 0, scene.Deadline), await scene.GameDeadlineJobRowAsync());
        Assert.Null(await scene.Service.ClaimJobAsync(Ct));

        scene.Clock.SetUtcNow(scene.Deadline);
        var onTime = await scene.Service.ClaimJobAsync(Ct);

        Assert.NotNull(onTime);
        Assert.Equal(early.Id, onTime.Id);
        Assert.True(await scene.Service.CompleteJobAsync(onTime, Ct));
        Assert.Equal(new RoomFacts("Expired", active.Version + 1, ActiveReservations: 0, Actions: 0), await scene.RoomFactsAsync());
        Assert.Equal("Done|1", await scene.GameDeadlineJobAsync());
    }

    /// <summary>
    /// O relógio do processo recua para antes do prazo logo depois da leitura que decide concluir o job. O motor do jogo
    /// decide no instante dessa decisão: expira a sala em vez de responder "ainda não venceu" e deixar o job <c>Done</c>
    /// sem efeito.
    /// </summary>
    [Fact]
    public async Task AGameDeadlineJobDecidedAtTheDeadlineExpiresTheRoomEvenIfTheClockRewindsBeforeItDuringCompletion()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);
        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.Equal(("game-deadline", scene.Room), (job.Kind, job.AggregateId));
        var rewinding = new ParticipationService(scene.Source, new RewindingClock(scene.Clock, scene.Deadline - OneTick));

        Assert.True(await rewinding.CompleteJobAsync(job, Ct));

        Assert.Equal(new RoomFacts("Expired", active.Version + 1, ActiveReservations: 0, Actions: 0), await scene.RoomFactsAsync());
        Assert.Equal("Done|1", await scene.GameDeadlineJobAsync());
        Assert.Equal(scene.Deadline, await JobRows.CompletedAtAsync(scene.Source, "game-deadline", scene.Room, Ct));
    }

    /// <summary>
    /// Duas conclusões simultâneas do mesmo job entregue cedo (o mesmo worker repetindo, ou outra instância com o mesmo token)
    /// devolvem o job à fila uma vez só: a tentativa não é devolvida duas vezes e o prazo fica o do job.
    /// </summary>
    [Fact]
    public async Task ConcurrentCompletionsOfAnEarlyGameDeadlineJobRequeueItOnce()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        await scene.AnticipateGameDeadlineJobAsync();
        var early = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(early);

        var results = await Task.WhenAll(
            Task.Run(() => scene.Service.CompleteJobAsync(early, Ct), Ct),
            Task.Run(() => scene.Service.CompleteJobAsync(early, Ct), Ct));

        Assert.All(results, completed => Assert.False(completed));
        Assert.Equal(active, await scene.RoomFactsAsync());
        Assert.Equal(new GameDeadlineJob("Pending", 0, scene.Deadline), await scene.GameDeadlineJobRowAsync());
    }

    /// <summary>
    /// O reagendamento obedece ao fencing: A faz o claim cedo e perde o lease; B faz o claim de novo, ainda antes do prazo, e
    /// fica com o token seguinte. A conclusão atrasada de A não toca a linha de B; só a de B devolve o job à fila.
    /// </summary>
    [Fact]
    public async Task AStaleCompletionOfAnEarlyGameDeadlineJobDoesNotRequeueTheNewClaim()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - TimeSpan.FromMinutes(2));
        await scene.AnticipateGameDeadlineJobAsync();
        var first = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(first);

        scene.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = await scene.Service.ClaimJobAsync(Ct);

        Assert.NotNull(second);
        Assert.Equal((first.Id, first.Token + 1), (second.Id, second.Token));
        Assert.False(await scene.Service.CompleteJobAsync(first, Ct));
        Assert.Equal("InFlight|2", await scene.GameDeadlineJobAsync());

        Assert.False(await scene.Service.CompleteJobAsync(second, Ct));
        Assert.Equal(new GameDeadlineJob("Pending", 1, scene.Deadline), await scene.GameDeadlineJobRowAsync());
        Assert.Equal(active, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// Entre o prazo e o job a sala segue <c>Active</c> no banco. Quem decide é o relógio do servidor (spec 10.4, "Timeout
    /// vs ação"; T-18): a ação é recusada sem gravar nada e o job, quando roda, ainda expira a sala. Uma ação nova recebe,
    /// antes e depois do job, a mesma <c>404</c> de uma sala inexistente, em status, corpo sem <c>traceId</c> e headers. A
    /// leitura da sala continua mostrando o estado do job (<c>Active</c> antes, <c>Expired</c> depois).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task AnActionAtOrAfterTheDeadlineButBeforeTheJobIsRefusedWithoutEffect(int secondsAfterDeadline)
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline.AddSeconds(secondsAfterDeadline));

        using var late = await ChooseOptionAsync(alice, scene.Room, active.Version);

        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(late));
        var unknownRoom = await UnknownRoomAsync(alice, active.Version);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(late, Ct));
        Assert.Equal(active, await scene.RoomFactsAsync());
        Assert.Equal("Pending|0", await scene.GameDeadlineJobAsync());

        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        var expired = new RoomFacts("Expired", active.Version + 1, ActiveReservations: 0, Actions: 0);
        Assert.Equal(expired, await scene.RoomFactsAsync());

        using var afterTheJob = await ChooseOptionAsync(alice, scene.Room, active.Version);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(afterTheJob, Ct));
        Assert.Equal(expired, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// Uma ação nova com <c>expectedVersion</c> desatualizado, no prazo e antes do job, recebe a mesma <c>404</c> da ação
    /// atrasada e de uma sala inexistente, não o <c>409</c> de conflito: o prazo é checado antes da versão. Nada é gravado.
    /// </summary>
    [Fact]
    public async Task ANewActionWithAStaleVersionAtTheDeadlineGetsTheSame404()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        using (var applied = await PostActionAsync(alice, scene.Room, ChooseOption(active.Version)))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);

        using var stale = await PostActionAsync(alice, scene.Room, ChooseOption(active.Version));

        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(stale));
        Assert.Equal(await UnknownRoomAsync(alice, active.Version), await NeutralResponse.DescribeAsync(stale, Ct));
        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());
        Assert.Equal("Pending|0", await scene.GameDeadlineJobAsync());
    }

    /// <summary>Reutilizar um <c>actionId</c> já aplicado com outro corpo, antes do prazo, continua <c>400 VALIDATION_FAILED</c>.</summary>
    [Fact]
    public async Task AReusedActionIdBeforeTheDeadlineIsStillAValidationFailure()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        var chosen = ChooseOption(active.Version);
        using (var applied = await PostActionAsync(alice, scene.Room, chosen))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = await scene.RoomFactsAsync();

        using var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(chosen));

        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(reused));
        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// A regra de replay vale até o job rodar: a ação aplicada um tick antes do prazo, reenviada depois do prazo e antes do
    /// job, responde <c>204</c> sem gravar de novo, e o mesmo <c>actionId</c> com outro corpo continua <c>400</c>. Depois do
    /// job, os dois recebem <c>404</c>.
    /// </summary>
    [Fact]
    public async Task AnActionAppliedInTimeIsReplayedAfterTheDeadlineUntilTheJobRuns()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        var chosen = ChooseOption(active.Version);
        using (var applied = await PostActionAsync(alice, scene.Room, chosen))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = new RoomFacts("Active", active.Version + 1, ActiveReservations: 2, Actions: 1);
        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());
        scene.Clock.SetUtcNow(scene.Deadline);

        using (var replay = await PostActionAsync(alice, scene.Room, chosen))
        using (var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(chosen)))
        {
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(reused));
        }

        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());

        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        var expired = await scene.RoomFactsAsync();
        Assert.Equal("Expired", expired.State);

        using (var replay = await PostActionAsync(alice, scene.Room, chosen))
        using (var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(chosen)))
        {
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, reused.StatusCode);
        }

        Assert.Equal(expired, await scene.RoomFactsAsync());
    }

    /// <summary>Só o prazo virou <c>404</c>: uma ação inválida antes do prazo continua <c>400 VALIDATION_FAILED</c>, sem gravar.</summary>
    [Fact]
    public async Task AnInvalidActionBeforeTheDeadlineIsStillAValidationFailure()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        using var invalid = await alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/actions",
            new RoomActionRequest(Guid.NewGuid(), active.Version.ToString(CultureInfo.InvariantCulture), "choose-option", "morning", "no-such-option"));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(invalid));
        Assert.Equal(active, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// O prazo só vale para a ação bem formada. Um payload malformado no prazo é recusado antes de a sala ser lida: a
    /// resposta é igual à do mesmo payload numa sala inexistente, e nada é gravado. O handler recusa a versão inválida com
    /// <c>400 VALIDATION_FAILED</c>, assim como o <c>actionId</c> ausente, que chega ao serviço como GUID vazio; JSON inválido
    /// (aqui truncado em <c>{"actionId":</c>), GUID malformado e <c>actionId</c> como string vazia ou <c>null</c> falham no
    /// model binding com <c>400 INVALID_REQUEST</c>.
    /// </summary>
    [Theory]
    [InlineData("""{"actionId":"6f1c2a4e-0000-4000-8000-000000000001","expectedVersion":"0","type":"choose-option","cardId":"morning","optionId":"tea"}""", "VALIDATION_FAILED")]
    [InlineData("""{"actionId":"not-a-guid","expectedVersion":"1","type":"choose-option","cardId":"morning","optionId":"tea"}""", "INVALID_REQUEST")]
    [InlineData("""{"actionId":"","expectedVersion":"1","type":"choose-option","cardId":"morning","optionId":"tea"}""", "INVALID_REQUEST")]
    [InlineData("""{"actionId":null,"expectedVersion":"1","type":"choose-option","cardId":"morning","optionId":"tea"}""", "INVALID_REQUEST")]
    [InlineData("""{"expectedVersion":"1","type":"choose-option","cardId":"morning","optionId":"tea"}""", "VALIDATION_FAILED")]
    // JSON truncado: o literal bruto termina em {"actionId": e não envia valor algum.
    [InlineData("""{"actionId":""", "INVALID_REQUEST")]
    public async Task AMalformedActionAtTheDeadlineIsRejectedBeforeTheRoomIsRead(string json, string code)
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);

        using var malformed = await alice.PostRawJsonAsync($"/api/v1/rooms/{scene.Room}/actions", json);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(malformed));
        using var unknown = await alice.PostRawJsonAsync($"/api/v1/rooms/{Guid.NewGuid()}/actions", json);
        Assert.Equal(await NeutralResponse.DescribeAsync(unknown, Ct), await NeutralResponse.DescribeAsync(malformed, Ct));
        Assert.Equal(active, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// Issue #463: numa sala <c>Completed</c>, uma ação nova no prazo ou depois dele recebe a mesma <c>404</c> da sala
    /// <c>Expired</c> e de uma sala inexistente, em status, corpo sem <c>traceId</c> e headers, antes e depois do job, sem
    /// gravar nada. O job não muda a sala <c>Completed</c>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task ANewActionInACompletedRoomAtOrAfterTheDeadlineGetsTheSame404BeforeAndAfterTheJob(int secondsAfterDeadline)
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        await scene.CompleteRoomAsync();
        var completed = await scene.RoomFactsAsync();
        Assert.Equal(("Completed", 0L, 2L), (completed.State, completed.ActiveReservations, completed.Actions));
        scene.Clock.SetUtcNow(scene.Deadline.AddSeconds(secondsAfterDeadline));

        using var late = await ChooseOptionAsync(alice, scene.Room, completed.Version);

        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(late));
        var unknownRoom = await UnknownRoomAsync(alice, completed.Version);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(late, Ct));
        Assert.Equal(completed, await scene.RoomFactsAsync());

        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.Equal(("game-deadline", scene.Room), (job.Kind, job.AggregateId));
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        Assert.Equal(completed, await scene.RoomFactsAsync());

        using var afterTheJob = await ChooseOptionAsync(alice, scene.Room, completed.Version);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(afterTheJob, Ct));
        Assert.Equal(completed, await scene.RoomFactsAsync());
    }

    /// <summary>Só o depois do prazo mudou: numa sala <c>Completed</c>, uma ação nova antes do prazo continua <c>400 VALIDATION_FAILED</c>, sem gravar.</summary>
    [Fact]
    public async Task ANewActionInACompletedRoomBeforeTheDeadlineIsStillAValidationFailure()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        await scene.CompleteRoomAsync();
        var completed = await scene.RoomFactsAsync();
        Assert.Equal("Completed", completed.State);
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        using var refused = await ChooseOptionAsync(alice, scene.Room, completed.Version);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(refused));
        Assert.Equal(completed, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// Resíduo do replay na sala <c>Completed</c>: o job <c>game-deadline</c> não muda a sala, e a rota só responde
    /// <c>404</c> direto para <c>Aborted</c> e <c>Expired</c>. Por isso, mesmo depois do prazo e do job, a ação que completou
    /// a sala, reenviada, responde <c>204</c> sem gravar de novo, e o mesmo <c>actionId</c> com outro corpo responde
    /// <c>400</c>. Só o autor vê isso, e só sobre a própria ação.
    /// </summary>
    [Fact]
    public async Task TheActionThatCompletedTheRoomIsStillReplayedAfterTheDeadlineAndTheJob()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var completing = await scene.CompleteRoomAsync();
        var completed = await scene.RoomFactsAsync();
        Assert.Equal("Completed", completed.State);
        scene.Clock.SetUtcNow(scene.Deadline.AddSeconds(60));
        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.Equal(("game-deadline", scene.Room), (job.Kind, job.AggregateId));
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        Assert.Equal("Done|1", await scene.GameDeadlineJobAsync());

        using var replay = await PostActionAsync(alice, scene.Room, completing);
        using var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(completing));

        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(reused));
        Assert.Equal(completed, await scene.RoomFactsAsync());
    }

    /// <summary>Na sala <c>Completed</c> depois do prazo, a ação nova com <c>expectedVersion</c> desatualizado também recebe a <c>404</c> neutra, não o <c>409</c>.</summary>
    [Fact]
    public async Task ANewActionWithAStaleVersionInACompletedRoomAfterTheDeadlineGetsTheSame404()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        await scene.CompleteRoomAsync();
        var completed = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);

        using var stale = await ChooseOptionAsync(alice, scene.Room, completed.Version - 1);

        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal(await UnknownRoomAsync(alice, completed.Version - 1), await NeutralResponse.DescribeAsync(stale, Ct));
        Assert.Equal(completed, await scene.RoomFactsAsync());
    }

    /// <summary>Quem não participa da sala <c>Completed</c> recebe, depois do prazo, a mesma <c>404</c> de uma sala inexistente.</summary>
    [Fact]
    public async Task AnOutsiderActingOnACompletedRoomAfterTheDeadlineGetsTheNeutral404()
    {
        await using var scene = await ActiveRoomAsync();
        using var third = await scene.SignInThirdAsync();
        await scene.CompleteRoomAsync();
        var completed = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);

        using var outsider = await ChooseOptionAsync(third, scene.Room, completed.Version);

        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(await UnknownRoomAsync(third, completed.Version), await NeutralResponse.DescribeAsync(outsider, Ct));
        Assert.Equal(completed, await scene.RoomFactsAsync());
    }

    [Fact]
    public async Task AnActionOneTickBeforeTheDeadlineIsApplied()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        using var inTime = await ChooseOptionAsync(alice, scene.Room, active.Version);

        Assert.Equal(HttpStatusCode.NoContent, inTime.StatusCode);
        Assert.Equal(new RoomFacts("Active", active.Version + 1, ActiveReservations: 2, Actions: 1), await scene.RoomFactsAsync());
    }

    /// <summary>A mesma ação numa sala que não existe: a resposta que a sala vencida não pode distinguir.</summary>
    private static async Task<string> UnknownRoomAsync(Browser browser, long version)
    {
        using var unknown = await ChooseOptionAsync(browser, Guid.NewGuid(), version);
        return await NeutralResponse.DescribeAsync(unknown, Ct);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("code").GetString();
    }

    private static RoomActionRequest ChooseOption(long version) =>
        new(Guid.NewGuid(), version.ToString(CultureInfo.InvariantCulture), "choose-option", "morning", "tea");

    private static RoomActionRequest SkipCardWithTheSameId(RoomActionRequest action) =>
        action with { Type = "skip-card", OptionId = null };

    private static Task<HttpResponseMessage> PostActionAsync(Browser browser, Guid room, RoomActionRequest action) =>
        browser.PostJsonAsync($"/api/v1/rooms/{room}/actions", action);

    private static Task<HttpResponseMessage> ChooseOptionAsync(Browser browser, Guid room, long version) =>
        browser.PostJsonAsync($"/api/v1/rooms/{room}/actions",
            new RoomActionRequest(Guid.NewGuid(), version.ToString(CultureInfo.InvariantCulture), "choose-option", "morning", "tea"));

    private async Task<Scene> ActiveRoomAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        var eventId = seed.Events[0];
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, Ct));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], Ct);
        var room = (await service.StatusAsync(eventId, seed.Alice, Ct))!.RoomId!.Value;
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, Ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, Ct));
        var scene = new Scene(new DuoraWebFactory(connectionString) { Time = clock }, connectionString, source, clock, service, seed, room);
        await scene.IsolateGameDeadlineJobAsync();
        return scene;
    }

    private sealed record RoomFacts(string State, long Version, long ActiveReservations, long Actions);

    private sealed record GameDeadlineJob(string Status, int Attempts, DateTimeOffset AvailableAt);

    private sealed class Scene(DuoraWebFactory factory, string connectionString, NpgsqlDataSource source, FakeTimeProvider clock,
        ParticipationService service, ParticipationSeed seed, Guid room) : IAsyncDisposable
    {
        public FakeTimeProvider Clock => clock;

        public ParticipationService Service => service;

        public NpgsqlDataSource Source => source;

        public Guid Room => room;

        public DateTimeOffset Deadline { get; private set; }

        /// <summary>
        /// Bob e depois Alice respondem à única carta pelo serviço, antes do prazo: a sala termina <c>Completed</c>. Devolve a
        /// ação de Alice, a que completou a sala, para o teste de replay.
        /// </summary>
        public async Task<RoomActionRequest> CompleteRoomAsync()
        {
            var bobVersion = (await service.RoomAsync(room, seed.Bob, Ct))!.Version;
            Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Bob, Guid.NewGuid(), bobVersion, "choose-option", "morning", "walk", Ct));
            var completing = ChooseOption((await service.RoomAsync(room, seed.Alice, Ct))!.Version);
            Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Alice, completing.ActionId,
                long.Parse(completing.ExpectedVersion, CultureInfo.InvariantCulture), completing.Type, completing.CardId, completing.OptionId, Ct));
            return completing;
        }

        public Task<Browser> SignInThirdAsync() =>
            new IdentityScenario(connectionString, factory).SignedInBrowserAsync(new(seed.Third, "third@example.com", IdentityScenario.ValidPassword));

        public Task<Browser> SignInAliceAsync() =>
            new IdentityScenario(connectionString, factory).SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));

        /// <summary>Lê o prazo gravado e adia os outros jobs pendentes, para o claim só poder escolher o <c>game-deadline</c> da sala.</summary>
        public async Task IsolateGameDeadlineJobAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using (var query = Command(connection, """
                select j.due_at,s.active_deadline_at from ops.jobs j join experiences.game_sessions s on s.id=j.aggregate_id
                where j.kind='game-deadline' and j.aggregate_id=$1
                """, room))
            await using (var reader = await query.ExecuteReaderAsync(Ct))
            {
                Assert.True(await reader.ReadAsync(Ct));
                Deadline = reader.GetFieldValue<DateTimeOffset>(0);
                Assert.Equal(Deadline, reader.GetFieldValue<DateTimeOffset>(1));
                Assert.False(await reader.ReadAsync(Ct));
            }

            await Execute(connection, """
                update ops.jobs set available_at=$2+interval '10 years'
                where status='Pending' and not (kind='game-deadline' and aggregate_id=$1)
                """, Ct, room, clock.GetUtcNow());
        }

        public async Task AnticipateGameDeadlineJobAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            Assert.Equal(1, await Execute(connection, "update ops.jobs set available_at=$2 where kind='game-deadline' and aggregate_id=$1", Ct, room, clock.GetUtcNow()));
        }

        /// <summary><c>status|tentativas</c>.</summary>
        public async Task<string> GameDeadlineJobAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select status||'|'||attempts from ops.jobs where kind='game-deadline' and aggregate_id=$1", room);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task<GameDeadlineJob> GameDeadlineJobRowAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select status,attempts,available_at from ops.jobs where kind='game-deadline' and aggregate_id=$1", room);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return new(reader.GetString(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2));
        }

        public async Task<RoomFacts> RoomFactsAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, """
                select s.state,s.version,
                  (select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null),
                  (select count(*) from experiences.game_actions a where a.game_session_id=s.id)
                from experiences.game_sessions s where s.id=$1
                """, room);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
