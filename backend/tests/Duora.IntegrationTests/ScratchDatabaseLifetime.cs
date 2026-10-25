using System.Collections.Concurrent;
using Npgsql;

namespace Duora.IntegrationTests;

/// <summary>Ownership and scoped pools of one generated scratch database; never a shared-server cleanup.</summary>
internal sealed class ScratchDatabaseLifetime : IDisposable
{
    private static readonly ConcurrentDictionary<(string Host, int Port, string Database), ScratchDatabaseLifetime> Scopes = new();
    private readonly ScratchDatabase _database;
    private readonly Func<CancellationToken, Task> _verify;
    private readonly Func<CancellationToken, Task> _drop;
    private readonly (string Host, int Port, string Database) _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly HashSet<string> _pools = new(StringComparer.Ordinal);
    private bool _closing;
    private bool _released;

    internal ScratchDatabaseLifetime(ScratchDatabase database, Func<CancellationToken, Task> verify,
        Func<CancellationToken, Task> drop)
    {
        _database = database;
        _verify = verify;
        _drop = drop;
        _key = Key(database.AppConnectionString);
        _pools.Add(database.AppConnectionString);
        if (!Scopes.TryAdd(_key, this))
        {
            throw new InvalidOperationException("A scratch database identity is already registered.");
        }
    }

    internal static void TrackPool(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString) || !Scopes.TryGetValue(Key(connectionString), out var scope))
        {
            return;
        }
        lock (scope._state)
        {
            if (scope._closing || scope._released)
            {
                throw new InvalidOperationException("The owning scratch database is closing.");
            }
            scope._pools.Add(connectionString);
        }
    }

    internal async ValueTask ReleaseAsync(ScratchDatabase requester)
    {
        // A record clone is not the generated and registered handle, even if its fields look identical.
        if (!ReferenceEquals(requester, _database))
        {
            throw new InvalidOperationException("Only the registered scratch database handle may release it.");
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _gate.WaitAsync(cleanup.Token);
        try
        {
            lock (_state)
            {
                if (_released)
                {
                    return;
                }
                _closing = true;
            }
            await _verify(cleanup.Token);
            string[] pools;
            lock (_state)
            {
                pools = [.. _pools];
            }
            foreach (var connectionString in pools)
            {
                using var connection = new NpgsqlConnection(connectionString);
                // Closes only this exact pool's idle connections; busy leases remain valid until returned.
                NpgsqlConnection.ClearPool(connection);
            }
            // Normal DROP refuses any still-connected consumer. Never FORCE or terminate a backend.
            await _drop(cleanup.Token);
            lock (_state)
            {
                _released = true;
                _pools.Clear();
            }
            RemoveScope();
        }
        finally
        {
            lock (_state)
            {
                _closing = false;
            }
            _gate.Release();
        }
    }

    private static (string Host, int Port, string Database) Key(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return (builder.Host ?? "", builder.Port, builder.Database ?? "");
    }

    private void RemoveScope() => ((ICollection<KeyValuePair<(string Host, int Port, string Database), ScratchDatabaseLifetime>>)Scopes)
        .Remove(new(_key, this));

    public void Dispose()
    {
        RemoveScope();
        _gate.Dispose();
    }
}
