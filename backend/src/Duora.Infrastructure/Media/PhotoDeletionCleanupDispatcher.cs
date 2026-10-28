using Duora.Core.Media;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>The random ownership token also prevents a stale collector from completing a re-leased obligation.</summary>
internal sealed record PhotoDeletionCleanupClaim(Guid PhotoId, ObjectKey Key, Guid Token, int Attempts);

/// <summary>
/// Exclusão física de API-M03: uma chave exata por lease, só de foto com tombstone e ainda <c>Deleted</c>, e só se a
/// chave for o original ou um derivado do asset excluído dessa foto. Todo I/O de storage ocorre depois de soltar a
/// conexão do claim; falha mantém a obrigação com backoff.
/// </summary>
public sealed partial class PhotoDeletionCleanupDispatcher
{
    internal static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private const int MaxBackoffSeconds = 900;
    private const int BaseBackoffSeconds = 15;
    private const int MaxBackoffDoublings = 6;

    private readonly NpgsqlDataSource _source;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<PhotoDeletionCleanupDispatcher> _logger;

    internal PhotoDeletionCleanupDispatcher(
        NpgsqlDataSource source,
        IObjectStore store,
        TimeProvider time,
        bool enabled,
        ILogger<PhotoDeletionCleanupDispatcher> logger)
    {
        _source = source;
        _store = store;
        _time = time;
        Enabled = enabled;
        _logger = logger;
    }

    public bool Enabled { get; }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || await ClaimAsync(cancellationToken) is not { } claim)
        {
            return 0;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DeleteBudget);
        try
        {
            await _store.DeleteAsync(claim.Key, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            LogRetry(_logger, claim.PhotoId, claim.Attempts, exception.GetType().Name);
            await RetryAsync(claim, cancellationToken);
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await CompleteAsync(claim, cancellationToken))
        {
            LogLeaseLost(_logger, claim.PhotoId);
        }

        return 1;
    }

    private async Task<PhotoDeletionCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        await using var command = Command(connection, """
            with picked as materialized (
              select c.object_key from media.photo_deletion_cleanup c
              join media.photo_tombstones t on t.photo_id = c.photo_id
              join media.uploads u on u.id = c.photo_id and u.state = 'Deleted' and u.owner_id = t.owner_id
              where c.available_at <= $1 and (c.lease_until is null or c.lease_until <= $1)
                and (c.object_key = u.quarantine_key or exists (
                  select 1 from media.media_assets a join media.media_variants v on v.media_asset_id = a.id
                  where a.upload_id = u.id and a.state = 'Deleted' and v.object_key = c.object_key))
              order by c.available_at, c.object_key limit 1 for update of c skip locked)
            update media.photo_deletion_cleanup c
            set fencing_token = $2, lease_until = $3,
                attempts = case when attempts < 1000000 then attempts + 1 else attempts end
            from picked where c.object_key = picked.object_key
            returning c.photo_id, c.object_key, c.attempts
            """, now, token, now + LeaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PhotoDeletionCleanupClaim(reader.GetGuid(0), ObjectKey.Parse(reader.GetString(1)), token, reader.GetInt32(2))
            : null;
    }

    private async Task<bool> CompleteAsync(PhotoDeletionCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, """
            delete from media.photo_deletion_cleanup
            where object_key = $1 and photo_id = $2 and fencing_token = $3 and lease_until > $4
            """, claim.Key.Value, claim.PhotoId, claim.Token, _time.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task RetryAsync(PhotoDeletionCleanupClaim claim, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var doublings = Math.Min(claim.Attempts - 1, MaxBackoffDoublings);
        var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoffSeconds, BaseBackoffSeconds * (1 << doublings)));
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, """
            update media.photo_deletion_cleanup set available_at = $4, lease_until = null, fencing_token = null
            where object_key = $1 and photo_id = $2 and fencing_token = $3 and lease_until > $5
            """, claim.Key.Value, claim.PhotoId, claim.Token, now + delay, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            LogLeaseLost(_logger, claim.PhotoId);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 3 };
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        return command;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Physical deletion for photo {PhotoId} failed on attempt {Attempt} ({Cause}); durable retry retained")]
    private static partial void LogRetry(ILogger logger, Guid photoId, int attempt, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Physical deletion for photo {PhotoId} lost its lease; the persisted obligation protects recovery")]
    private static partial void LogLeaseLost(ILogger logger, Guid photoId);
}
