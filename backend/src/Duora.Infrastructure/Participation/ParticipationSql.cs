using Npgsql;

namespace Duora.Infrastructure.Participation;

internal static class ParticipationSql
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        return command;
    }

    public static async Task<int> Execute(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = Command(connection, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> Eligible(NpgsqlConnection connection, Guid userId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            select exists(select 1 from identity.users u where u.id=$1 and u.status='Active'
              and u.email_confirmed
              and exists(select 1 from identity.selfie_verifications v where v.user_id=u.id and v.state='Passed'))
            """, userId);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public static async Task Guard(NpgsqlConnection connection, IEnumerable<Guid> users, CancellationToken ct)
    {
        var ids = users.Distinct().Order().ToArray();
        await Execute(connection, "set local lock_timeout='2s'", ct);
        await Execute(connection, """
            insert into safety.user_guards(user_id) select id from unnest($1::uuid[]) id order by id on conflict do nothing
            """, ct, ids);
        await Execute(connection, "select 1 from safety.user_guards where user_id=any($1) order by user_id for update", ct, ids);
        // Presence/account writers serialize on users, not user_guards. Hold their rows through
        // eligibility revalidation and reservation too; an account transition cannot slip past that read.
        await Execute(connection, "select 1 from identity.users where id=any($1) order by id for share", ct, ids);
    }
}
