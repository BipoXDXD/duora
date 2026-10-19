using System.Text.Json;
using Duora.Core.Experiences.Constellation;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed record ParticipationSeed(Guid Alice, Guid Bob, Guid Third, Guid ContentId, IReadOnlyList<Guid> Events, IReadOnlyList<Guid> Rounds);

/// <summary>Synthetic fixtures only: this helper is compiled into tests, never deployed with Web or Worker.</summary>
public static class BrowserScenarioSeed
{
    private static readonly string[] Names = ["alice", "bob", "third"];

    public static async Task<ParticipationSeed> InitializeAsync(string connectionString, DateTimeOffset now, CancellationToken ct,
        string emailSuffix = "", bool declarePreferences = false)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        Guid[] users = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var password = new PasswordHasher<object>().HashPassword(new object(), "frase longa o bastante");
        foreach (var (user, name) in users.Zip(Names))
        {
            var email = name + emailSuffix + "@example.com";
            await Execute(connection, """
                insert into identity.users(id,status,created_at,user_name,normalized_user_name,email,normalized_email,email_confirmed,
                  password_hash,security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count)
                values($1,'Active',$2,$3,upper($3),$3,upper($3),true,$4,$5,$6,false,false,true,0)
                """, ct, user, now, email, password, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            await Execute(connection, "insert into identity.user_roles(user_id,role_id) select $1,id from identity.roles where normalized_name='PARTICIPANT'", ct, user);
            await Execute(connection, """
                insert into identity.selfie_verifications(id,user_id,state,policy_version,requested_at,expires_at,completed_at,version)
                values($1,$2,'Passed','test',$3,$3,$3,1)
                """, ct, Guid.NewGuid(), user, now);
            if (declarePreferences)
            {
                await Execute(connection, "insert into profiles.preferences(user_id,allow_different_display_locales,version,updated_at) values($1,true,1,$2)", ct, user, now);
                await Execute(connection, "insert into profiles.communication_languages(user_id,language_code) values($1,'en')", ct, user);
            }
        }
        var en = new ConstellationContent(1, "Shared choices", "Explore choices together.", [new("morning", "What makes a pleasant morning?", [new("tea", "Tea"), new("walk", "A walk")])]);
        var pt = new ConstellationContent(1, "Escolhas em comum", "Explore escolhas em dupla.", [new("morning", "O que torna a manhã agradável?", [new("tea", "Chá"), new("walk", "Uma caminhada")])]);
        _ = new LocalizedConstellationContent(en, pt);
        var contentId = Guid.NewGuid();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await Execute(connection, """
            insert into experiences.experience_versions(id,content_version,rules_version,schema_version,editorial_state,content_en,content_pt,published_at)
            values($1,1,1,1,'Published',$2::jsonb,$3::jsonb,$4)
            """, ct, contentId, JsonSerializer.Serialize(en, json), JsonSerializer.Serialize(pt, json), now);
        List<Guid> events = [];
        List<Guid> rounds = [];
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            var round = Guid.NewGuid();
            var starts = now.AddMinutes(5 * (i + 1));
            events.Add(id);
            rounds.Add(round);
            await Execute(connection, "insert into events.events(id,title,state,starts_at,capacity,experience_version_id) values($1,$2,'Published',$3,100,$4)", ct, id, $"Synthetic event {i + 1}", starts, contentId);
            await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,1,'Scheduled',$3,$4,42)", ct, round, id, starts, starts.AddMinutes(12));
            await Execute(connection, "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,'build-round',$2,$3,$3)", ct, Guid.NewGuid(), round, starts);
            await Execute(connection, "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,'close-round',$2,$3,$3)", ct, Guid.NewGuid(), round, starts.AddMinutes(12));
        }
        return new(users[0], users[1], users[2], contentId, events, rounds);
    }
}
