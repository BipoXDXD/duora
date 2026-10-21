using Duora.Contracts.Participation;
using Duora.Core.Experiences.UnlikelyRoutes;
using Duora.Infrastructure.Participation;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Additional synthetic Routes event, published through the real service with local fixture audio.</summary>
public static class RoutesBrowserScenarioSeed
{
    public static async Task<Guid> InitializeAsync(string connectionString, ParticipationService service,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var administrator = Guid.NewGuid();
        await Execute(connection, """
            insert into identity.users(id,status,created_at,user_name,normalized_user_name,email,normalized_email,email_confirmed,
              security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count)
            values($1,'Active',$2,$3,upper($3),$3,upper($3),true,$4,$5,false,true,true,0)
            """, ct, administrator, now, $"routes-admin-{administrator}@example.com", Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await Execute(connection, "insert into identity.user_roles(user_id,role_id) select $1,id from identity.roles where normalized_name='PRODUCT_ADMIN'", ct, administrator);
        var request = RoutesCatalogPublicationTests.Request();
        var content = await service.PublishRoutesCatalogAsync(administrator,
            Content(request.EnUS), Content(request.PtBR),
            request.Audio.Select(cue => new CatalogAudioUpload(cue.CueId, Variant(cue.EnUS), Variant(cue.PtBR))).ToArray(), true, ct)
            ?? throw new InvalidOperationException("Synthetic Routes catalog was rejected.");
        var eventId = Guid.NewGuid();
        var round = Guid.NewGuid();
        var starts = now.AddMinutes(5);
        await Execute(connection, "insert into events.events(id,title,state,starts_at,capacity,experience_version_id) values($1,'Synthetic Routes event','Published',$2,100,$3)", ct, eventId, starts, content);
        await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,1,'Scheduled',$3,$4,42)", ct, round, eventId, starts, starts.AddMinutes(12));
        await Execute(connection, "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,'build-round',$2,$3,$3)", ct, Guid.NewGuid(), round, starts);
        await Execute(connection, "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,'close-round',$2,$3,$3)", ct, Guid.NewGuid(), round, starts.AddMinutes(12));
        return eventId;
    }

    private static CatalogAudioUploadVariant Variant(CatalogAudioVariantRequest input) =>
        new(input.AssetId, input.Transcript, input.AccessibleDescription, input.Provenance, input.Sha256, input.Bytes);

    private static UnlikelyRoutesContent Content(RoutesCatalogContentRequest input) => new(1, input.Title, input.Objective, input.Example,
        input.Stops.Concat(input.Stops.Select(stop => stop with { StepId = stop.StepId + "-second" })).Select(stop => new RouteStop(stop.StepId, stop.Prompt, stop.Options.Select(option => new RouteOption(option.OptionId, option.Label)).ToArray())).ToArray(),
        input.ClueSets.Select(set => (IReadOnlyList<Clue>)set.Select(clue => new Clue(clue.Text)).ToArray()).ToArray());
}
