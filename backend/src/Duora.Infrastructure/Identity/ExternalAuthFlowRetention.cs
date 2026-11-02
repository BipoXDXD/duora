using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>Expiry cleanup has no provider credentials or web runtime dependency and runs even with OIDC off.</summary>
public sealed class ExternalAuthFlowRetention(NpgsqlDataSource dataSource, TimeProvider time)
{
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var deleted = 0;
        for (var batch = 0; batch < 20; batch++)
        {
            await using var command = dataSource.CreateCommand("""
                with expired as (
                    select id from identity.external_auth_flows where expires_at <= @now
                    order by expires_at, id limit 500 for update skip locked
                )
                delete from identity.external_auth_flows f using expired e where f.id=e.id
                """);
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            var count = await command.ExecuteNonQueryAsync(ct);
            deleted += count;
            if (count < 500)
            {
                break;
            }
        }
        return deleted;
    }
}
