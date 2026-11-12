using System.Net;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-E05 sem vaga: quem não tem inscrição confirmada (lista de espera, saída, evento já concluído para ele ou sem
/// inscrição) recebe 404 no check-in, e nem a inscrição dele nem o evento nem a inscrição de quem tem a vaga mudam.
/// </summary>
public sealed class CheckInWithoutSeatTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Waitlisted")]
    [InlineData("Withdrawn")]
    [InlineData("Completed")]
    [InlineData("Unregistered")]
    public async Task CheckInWithoutAConfirmedSeatIsRefusedAndChangesNothing(string situation)
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        var eventId = scene.Seed.Events[0];
        await scene.ExecuteAsync("update events.events set capacity=1 where id=$1", eventId);
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(eventId, scene.Seed.Alice, false, Ct));
        if (situation != "Unregistered")
        {
            Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(eventId, scene.Seed.Bob, false, Ct));
        }

        if (situation is "Withdrawn" or "Completed")
        {
            await scene.ExecuteAsync("update events.registrations set state=$3,waitlist_position=null where event_id=$1 and user_id=$2", eventId, scene.Seed.Bob, situation);
        }

        var before = await SnapshotAsync(scene, eventId);
        Assert.Contains(situation == "Unregistered" ? "\"state\": \"Confirmed\"" : $"\"state\": \"{situation}\"", before, StringComparison.Ordinal);
        using var bob = await scene.SignInAsync(scene.Seed.Bob);

        using var refused = await bob.PostJsonAsync($"/api/v1/events/{eventId}/check-in", null);

        await ProblemAssert.IsProblemAsync(refused, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(before, await SnapshotAsync(scene, eventId));

        using var alice = await scene.SignInAsync(scene.Seed.Alice);
        using var seated = await alice.PostJsonAsync($"/api/v1/events/{eventId}/check-in", null);
        Assert.Equal(HttpStatusCode.NoContent, seated.StatusCode);
        Assert.Contains("\"state\": \"CheckedIn\"", await SnapshotAsync(scene, eventId), StringComparison.Ordinal);
    }

    /// <summary>Linha inteira do evento e de todas as inscrições dele, em JSON: qualquer coluna que mude derruba a igualdade.</summary>
    private static async Task<string> SnapshotAsync(ParticipationScene scene, Guid eventId)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, """
            select (select to_jsonb(e)::text from events.events e where e.id=$1)
              || (select coalesce(jsonb_agg(to_jsonb(r) order by r.user_id)::text,'[]') from events.registrations r where r.event_id=$1)
            """, eventId);
        return (string)(await query.ExecuteScalarAsync(Ct))!;
    }
}
