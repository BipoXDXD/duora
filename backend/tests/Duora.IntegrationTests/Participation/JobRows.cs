using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Leituras de <c>ops.jobs</c> que mais de uma classe de teste de participação precisa.</summary>
internal static class JobRows
{
    /// <summary><c>completed_at</c> do único job do tipo e agregado; nulo enquanto ele não termina.</summary>
    public static async Task<DateTimeOffset?> CompletedAtAsync(NpgsqlDataSource source, string kind, Guid aggregate, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select completed_at from ops.jobs where kind=$1 and aggregate_id=$2", kind, aggregate);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        DateTimeOffset? completedAt = await reader.IsDBNullAsync(0, ct) ? null : reader.GetFieldValue<DateTimeOffset>(0);
        Assert.False(await reader.ReadAsync(ct));
        return completedAt;
    }
}
