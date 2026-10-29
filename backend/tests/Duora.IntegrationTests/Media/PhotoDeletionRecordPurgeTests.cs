using System.Diagnostics.Metrics;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// P3 da #173 (ADR-0035, decisões de 29/10/2026): tombstones e recibos de exclusão de foto saem 1 ano depois de
/// <c>deleted_at</c>/<c>created_at</c>, pela função <c>media.purge_photo_deletion_records</c>. Banco próprio, porque o
/// expurgo varre todas as contas. O relógio falso fica no passado: a função limita o instante pedido pelo relógio do
/// banco, então "agora + 1 ano" de um relógio adiantado não apagaria nada.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class PhotoDeletionRecordPurgeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset DeletedAt = new(2025, 3, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OneYearLater = DeletedAt.AddYears(1);

    private static readonly string PurgeCategory =
        typeof(PhotoDeletionRecordPurge).FullName ?? throw new InvalidOperationException("Tipo sem nome completo.");

    private readonly FakeTimeProvider _time = new(DeletedAt);
    private readonly CapturingLoggerProvider _logs = new();
    private ServiceProvider? _meters;
    private NpgsqlDataSource? _source;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NpgsqlDataSource Source => _source ?? throw new InvalidOperationException("Teste não inicializado.");

    private IMeterFactory Meters =>
        _meters?.GetRequiredService<IMeterFactory>() ?? throw new InvalidOperationException("Teste não inicializado.");

    public async ValueTask InitializeAsync()
    {
        _source = NpgsqlDataSource.Create(await postgres.CreateMigratedDatabaseAsync(Ct));
        _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        if (_meters is not null)
        {
            await _meters.DisposeAsync();
        }
    }

    [Fact]
    public async Task ATombstoneAndReceiptOneTickShortOfAYearAreKept()
    {
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        _time.SetUtcNow(OneYearLater - TimeSpan.FromTicks(1));

        var result = await Purge().PurgeOnceAsync(Ct);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(0, 0), result);
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", photo));
    }

    [Fact]
    public async Task ATombstoneAndReceiptExactlyAYearOldArePurgedAndThePhotoStaysDeleted()
    {
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        _time.SetUtcNow(OneYearLater);

        var result = await Purge().PurgeOnceAsync(Ct);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 1), result);
        Assert.Equal(0, await CountAsync("select count(*) from media.photo_tombstones"));
        Assert.Equal(0, await CountAsync("select count(*) from media.photo_deletion_commands"));
        // O upload não é tocado: a foto continua Deleted, e é esse estado que barra leitura e restore de mídia.
        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id = $1 and state = 'Deleted'", photo));
    }

    [Fact]
    public async Task AReceiptOfALaterRepeatKeepsItsOwnYearAfterTheTombstoneLeaves()
    {
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        await SeedReceiptAsync(photo, DeletedAt.AddDays(30));
        _time.SetUtcNow(OneYearLater);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 1), await Purge().PurgeOnceAsync(Ct));
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", photo));

        _time.SetUtcNow(OneYearLater.AddDays(30));
        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 0), await Purge().PurgeOnceAsync(Ct));
        Assert.Equal(0, await CountAsync("select count(*) from media.photo_deletion_commands"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(364)]
    public async Task ATombstoneWhoseDeletionIsStillInsideTheBackupWindowNeverLeavesWhateverTheCallerClockSays(int daysAgo)
    {
        // A janela de backup (~65 dias: 14 de recuperação + 21 de lock + 30 do lifecycle) conta no relógio do banco.
        // Mesmo um chamador com relógio séculos à frente, ou comprometido, não torna elegível uma exclusão recente.
        var databaseNow = await DatabaseNowAsync();
        var photo = (await SeedDeletionsAsync(1, databaseNow.AddDays(-daysAgo))).Single();
        _time.SetUtcNow(new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await Purge().PurgeOnceAsync(Ct);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(0, 0), result);
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", photo));
    }

    [Fact]
    public async Task ATombstoneWhosePhysicalDeletionIsStillPendingNeverLeaves()
    {
        // Enquanto os bytes não saem do disco, cada novo backup de mídia ainda os copia: a janela nem começou.
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        await ExecuteAsync("""
            insert into media.photo_deletion_cleanup (object_key, photo_id, created_at, available_at)
            select quarantine_key, id, $2, $2 from media.uploads where id = $1
            """, photo, DeletedAt);
        _time.SetUtcNow(DeletedAt.AddYears(3));

        var result = await Purge().PurgeOnceAsync(Ct);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 0), result);
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));
    }

    [Fact]
    public async Task ATombstoneThatStillExplainsAPendingCandidateCleanupNeverLeaves()
    {
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        await ExecuteAsync("""
            insert into media.review_candidates (generation, upload_id, upload_version, detected_type, width, height,
              created_at, prepare_deadline, writer_settled_at, cleanup_requested_at, cleanup_available_at)
            values (gen_random_uuid(), $1, 1, 'image/jpeg', 64, 32, $2 - interval '1 hour', $2,
              $2 - interval '1 hour', $2, $2)
            """, photo, DeletedAt);
        _time.SetUtcNow(DeletedAt.AddYears(3));

        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 0), await Purge().PurgeOnceAsync(Ct));
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));

        await ExecuteAsync("update media.review_candidates set deleted_at = $2, last_cleanup_at = $2 where upload_id = $1",
            photo, DeletedAt.AddMinutes(5));
        Assert.Equal(new PhotoDeletionRecordPurgeResult(0, 1), await Purge().PurgeOnceAsync(Ct));
    }

    [Fact]
    public async Task OneCallDeletesAtMostOneBatchPerTableAndTheNextCallTakesTheRest()
    {
        await SeedDeletionsAsync(PhotoDeletionRecordPurge.BatchSize + 1, DeletedAt);
        _time.SetUtcNow(OneYearLater);
        var purge = Purge();

        Assert.Equal(new PhotoDeletionRecordPurgeResult(PhotoDeletionRecordPurge.BatchSize, PhotoDeletionRecordPurge.BatchSize),
            await purge.PurgeOnceAsync(Ct));
        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 1), await purge.PurgeOnceAsync(Ct));
        Assert.Equal(new PhotoDeletionRecordPurgeResult(0, 0), await purge.PurgeOnceAsync(Ct));
    }

    [Fact]
    public async Task TwoInstancesRunningTogetherDeleteEachRecordOnce()
    {
        const int Eligible = 50;
        const int FirstBatch = 30;
        await SeedDeletionsAsync(Eligible, DeletedAt);
        _time.SetUtcNow(OneYearLater);

        // A primeira instância apaga um lote e ainda não confirmou: as linhas dela ficam travadas.
        await using var first = await Source.OpenConnectionAsync(Ct);
        await using var transaction = await first.BeginTransactionAsync(Ct);
        await using (var held = new NpgsqlCommand(
            "select receipts, tombstones from media.purge_photo_deletion_records($1, $2)", first, transaction))
        {
            held.Parameters.AddWithValue(OneYearLater);
            held.Parameters.AddWithValue(FirstBatch);
            await using var reader = await held.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal((FirstBatch, FirstBatch), (reader.GetInt32(0), reader.GetInt32(1)));
        }

        // A segunda pula o que está travado e leva só o resto, sem esperar nem repetir efeito.
        using var counter = new PurgedCounter(Meters);
        var second = await Purge().PurgeOnceAsync(Ct);
        await transaction.CommitAsync(Ct);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(Eligible - FirstBatch, Eligible - FirstBatch), second);
        Assert.Equal(2 * (Eligible - FirstBatch), counter.Total);
        Assert.Equal(0, await CountAsync("select count(*) from media.photo_tombstones"));
        Assert.Equal(0, await CountAsync("select count(*) from media.photo_deletion_commands"));
    }

    [Fact]
    public async Task ConcurrentPurgesAddUpToTheEligibleRecordsExactly()
    {
        const int Eligible = 120;
        await SeedDeletionsAsync(Eligible, DeletedAt);
        await SeedDeletionsAsync(3, OneYearLater.AddDays(-1));
        _time.SetUtcNow(OneYearLater);
        using var counter = new PurgedCounter(Meters);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Purge().PurgeOnceAsync(Ct)));

        Assert.Equal(Eligible, results.Sum(r => r.Tombstones));
        Assert.Equal(Eligible, results.Sum(r => r.Receipts));
        Assert.Equal(2 * Eligible, counter.Total);
        Assert.Equal(3, await CountAsync("select count(*) from media.photo_tombstones"));
        Assert.Equal(3, await CountAsync("select count(*) from media.photo_deletion_commands"));
    }

    [Fact]
    public async Task APurgeRecordsTheMetricPerTableAndLogsTheTotal()
    {
        await SeedDeletionsAsync(2, DeletedAt);
        await SeedReceiptAsync((await SeedDeletionsAsync(1, OneYearLater.AddDays(-1))).Single(), DeletedAt);
        _time.SetUtcNow(OneYearLater);
        using var counter = new PurgedCounter(Meters);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(3, 2), await Purge().PurgeOnceAsync(Ct));

        Assert.Equal(3, counter.Of("photo_deletion_commands"));
        Assert.Equal(2, counter.Of("photo_tombstones"));
        var entry = Assert.Single(_logs.Entries, e => e.Category == PurgeCategory);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("5 linhas (3 recibos, 2 tombstones)", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyPurgeRecordsNothingAndStaysSilent()
    {
        await SeedDeletionsAsync(1, DeletedAt);
        using var counter = new PurgedCounter(Meters);

        Assert.Equal(new PhotoDeletionRecordPurgeResult(0, 0), await Purge().PurgeOnceAsync(Ct));

        Assert.Equal(0, counter.Total);
        Assert.Empty(_logs.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task TheFunctionRefusesABatchOutsideItsBounds(int limit)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(OneYearLater, limit));

        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, exception.SqlState);
    }

    [Fact]
    public async Task TheFunctionAcceptsTheLargestBatch() =>
        Assert.Equal((0, 0), await CallFunctionAsync(OneYearLater, 1000));

    [Fact]
    public async Task TheFunctionRefusesAnInfiniteInstant()
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select * from media.purge_photo_deletion_records('infinity'::timestamptz, 10)", connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, exception.SqlState);
    }

    [Fact]
    public async Task TheFunctionIsASecurityDefinerWithAPinnedSearchPathAndUtcClock()
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select p.prosecdef, array_to_string(p.proconfig, ';'), pg_get_userbyid(p.proowner)
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'media' and p.proname = 'purge_photo_deletion_records'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);

        Assert.True(await reader.ReadAsync(Ct));
        Assert.True(reader.GetBoolean(0));
        var settings = reader.GetString(1);
        Assert.Contains("search_path=pg_catalog, pg_temp", settings, StringComparison.Ordinal);
        Assert.Contains("TimeZone=UTC", settings, StringComparison.Ordinal);
        Assert.Equal("duora_migrator", reader.GetString(2));
        Assert.False(await reader.ReadAsync(Ct));
    }

    private PhotoDeletionRecordPurge Purge() =>
        new(Source, _time, new PhotoDeletionRecordPurgeMetrics(Meters), _logs.CreateLogger(PurgeCategory));

    private async Task<(int Receipts, int Tombstones)> CallFunctionAsync(DateTimeOffset asOf, int limit)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select receipts, tombstones from media.purge_photo_deletion_records($1, $2)", connection);
        command.Parameters.AddWithValue(asOf);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Uma conta sintética com <paramref name="count"/> fotos já excluídas: upload <c>Deleted</c>, tombstone e recibo da
    /// exclusão, todos no mesmo instante, como grava <see cref="PhotoDeletions"/>. Sem obrigação física pendente.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SeedDeletionsAsync(int count, DateTimeOffset deletedAt)
    {
        var owner = Guid.NewGuid();
        await ExecuteAsync("""
            insert into identity.users (id, status, created_at, email_confirmed, phone_number_confirmed,
              two_factor_enabled, lockout_enabled, access_failed_count, security_stamp)
            values ($1, 'Active', $2, true, false, false, true, 0, 'synthetic-purge-stamp')
            """, owner, deletedAt.AddDays(-1));
        await ExecuteAsync("""
            insert into media.uploads (id, owner_id, state, client_key, quarantine_key, declared_content_type,
              size_bytes, content_sha256, created_at, abandon_after, rejection_code, version)
            select gen_random_uuid(), $1, 'Deleted', 'purge-' || g, 'quarantine/' || md5($1::text || g), 'image/jpeg',
              2048, null, $2 - interval '1 hour', $2, null, 3
            from generate_series(1, $3) g
            """, owner, deletedAt, count);
        await ExecuteAsync("""
            insert into media.photo_tombstones (photo_id, owner_id, operation_key, intent_sha256, deleted_version, deleted_at)
            select id, owner_id, encode(sha256(convert_to('op:' || id, 'UTF8')), 'hex'),
              sha256(convert_to('intent:' || id, 'UTF8')), 3, $2
            from media.uploads where owner_id = $1
            """, owner, deletedAt);
        await ExecuteAsync("""
            insert into media.photo_deletion_commands (operation_key, owner_id, key_id, photo_id, intent_sha256,
              snapshot_sha256, expected_version, result_version, created_at)
            select operation_key, owner_id, 'v1', photo_id, intent_sha256, sha256(convert_to('snap:' || photo_id, 'UTF8')),
              2, 3, $2
            from media.photo_tombstones where owner_id = $1
            """, owner, deletedAt);
        return await QueryIdsAsync("select id from media.uploads where owner_id = $1", owner);
    }

    /// <summary>Recibo de um DELETE repetido com chave nova sobre a foto já excluída (sem novo tombstone).</summary>
    private Task SeedReceiptAsync(Guid photo, DateTimeOffset createdAt) => ExecuteAsync("""
        insert into media.photo_deletion_commands (operation_key, owner_id, key_id, photo_id, intent_sha256,
          snapshot_sha256, expected_version, result_version, created_at)
        select encode(sha256(convert_to('repeat:' || id || $2::text, 'UTF8')), 'hex'), owner_id, 'v1', id,
          sha256(convert_to('repeat-intent:' || id, 'UTF8')), sha256(convert_to('repeat-snap:' || id, 'UTF8')), 3, 3, $2
        from media.uploads where id = $1
        """, photo, createdAt);

    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("select statement_timestamp()", connection);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(Ct) ?? throw new InvalidOperationException()));
    }

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountAsync(string sql, params object[] values)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        return (long)(await command.ExecuteScalarAsync(Ct) ?? 0L);
    }

    private async Task<IReadOnlyList<Guid>> QueryIdsAsync(string sql, Guid owner)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(owner);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Soma as medições do contador de expurgo criadas por esta fábrica (outras classes de teste não interferem).</summary>
    private sealed class PurgedCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _byTable = [];
        private readonly Lock _gate = new();

        public PurgedCounter(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory)
                    && instrument.Meter.Name == PhotoDeletionRecordPurgeMetrics.MeterName
                    && instrument.Name == PhotoDeletionRecordPurgeMetrics.PurgedInstrument)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var table = "";
                foreach (var tag in tags)
                {
                    if (tag.Key == "table")
                    {
                        table = tag.Value as string ?? "";
                    }
                }

                lock (_gate)
                {
                    _byTable[table] = _byTable.GetValueOrDefault(table) + value;
                }
            });
            _listener.Start();
        }

        public long Total
        {
            get
            {
                lock (_gate)
                {
                    return _byTable.Values.Sum();
                }
            }
        }

        public long Of(string table)
        {
            lock (_gate)
            {
                return _byTable.GetValueOrDefault(table);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
