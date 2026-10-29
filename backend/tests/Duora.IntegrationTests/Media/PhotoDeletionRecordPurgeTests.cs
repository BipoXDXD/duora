using System.Diagnostics.Metrics;
using Duora.Infrastructure.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    private ScratchDatabase? _database;
    private NpgsqlDataSource? _source;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NpgsqlDataSource Source => _source ?? throw new InvalidOperationException("Teste não inicializado.");

    private IMeterFactory Meters =>
        _meters?.GetRequiredService<IMeterFactory>() ?? throw new InvalidOperationException("Teste não inicializado.");

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        _source = NpgsqlDataSource.Create(_database.AppConnectionString);
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

        if (_database is not null)
        {
            await _database.DisposeAsync();
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
    public async Task ATombstoneWhoseOriginalCleanupIsStillPendingNeverLeaves()
    {
        // Com obrigação própria em original_cleanup, a exclusão não grava o original em photo_deletion_cleanup.
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        await ExecuteAsync("""
            insert into media.original_cleanup (upload_id, object_key, created_at, available_at)
            select id, quarantine_key, $2, $2 from media.uploads where id = $1
            """, photo, DeletedAt.AddMinutes(-5));
        _time.SetUtcNow(DeletedAt.AddYears(3));

        Assert.Equal(new PhotoDeletionRecordPurgeResult(1, 0), await Purge().PurgeOnceAsync(Ct));
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));

        await ExecuteAsync("delete from media.original_cleanup where upload_id = $1", photo);
        Assert.Equal(new PhotoDeletionRecordPurgeResult(0, 1), await Purge().PurgeOnceAsync(Ct));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(64, false)]
    [InlineData(65, true)]
    [InlineData(66, true)]
    public async Task ATombstoneLeavesOnlyWhenItsCandidateWasDeletedAtLeastTheBackupWindowAgo(int daysAgo, bool purged)
    {
        // A janela de backup conta da remoção física, no relógio do banco, não de deleted_at.
        var databaseNow = await DatabaseNowAsync();
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        var removedAt = databaseNow.AddDays(-daysAgo);
        await ExecuteAsync("""
            insert into media.review_candidates (generation, upload_id, upload_version, detected_type, width, height,
              created_at, prepare_deadline, writer_settled_at, cleanup_requested_at, cleanup_available_at,
              last_cleanup_at, deleted_at)
            values (gen_random_uuid(), $1, 1, 'image/jpeg', 64, 32, $2 - interval '1 hour', $2,
              $2 - interval '1 hour', $2, $2, $3, $3)
            """, photo, DeletedAt, removedAt);
        _time.SetUtcNow(new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await Purge().PurgeOnceAsync(Ct);

        Assert.Equal(purged ? 1 : 0, result.Tombstones);
        Assert.Equal(purged ? 0 : 1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(64, false)]
    [InlineData(65, true)]
    [InlineData(66, true)]
    public async Task ATombstoneLeavesOnlyWhenItsReviewPromotionWasRemovedAtLeastTheBackupWindowAgo(int? daysAgo, bool purged)
    {
        // Uma promoção não confirmada guarda bytes privados em review_promotion_variants até o coletor removê-los;
        // como no candidato, a janela de backup conta da remoção física, no relógio do banco. O candidato da foto já
        // saiu há muito tempo, então só a promoção segura o tombstone.
        var databaseNow = await DatabaseNowAsync();
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        await SeedReviewPromotionAsync(photo, daysAgo is { } days ? databaseNow.AddDays(-days) : null);
        _time.SetUtcNow(new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await Purge().PurgeOnceAsync(Ct);

        Assert.Equal(purged ? 1 : 0, result.Tombstones);
        Assert.Equal(purged ? 0 : 1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ATombstoneExactlyOneYearOldOnTheDatabaseClockLeavesEvenWithTheCallerAhead(
        int microsecondsYounger, bool purged)
    {
        // A idade e o expurgo saem do mesmo comando do cliente, então leem o mesmo statement_timestamp(): o registro
        // tem exatamente 1 ano (365 dias quando o intervalo não cruza um 29 de fevereiro) no relógio do banco, sem
        // que o relógio ande entre a leitura e a chamada. O chamador no ano 9999 deixa o relógio do banco como teto.
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();

        await PurgeWithRecordsAgedOnTheSameStatementAsync(microsecondsYounger);

        Assert.Equal(purged ? 0 : 1, await CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photo));
        Assert.Equal(purged ? 0 : 1, await CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", photo));
    }

    [Fact]
    public async Task TheFunctionRunsWithExactlyItsPinnedSettings()
    {
        // Sem lock_timeout, um lock preso seguraria o lote até o statement timeout do cliente.
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select p.proconfig from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'media' and p.proname = 'purge_photo_deletion_records'
            """, connection);

        var settings = (string[])(await command.ExecuteScalarAsync(Ct) ?? Array.Empty<string>());

        Assert.Equal(["TimeZone=UTC", "lock_timeout=500ms", "search_path=pg_catalog, pg_temp"],
            settings.Order(StringComparer.Ordinal));
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

    [Fact]
    public async Task RollingBackThePromotionRuleRestoresThePreviousFunctionWithItsOwnerAndGrant()
    {
        // O Down da 20261029180000 devolve o corpo da 20261029150000: a promoção pendente deixa de segurar o
        // tombstone, e o dono e o EXECUTE de duora_app continuam os mesmos.
        var photo = (await SeedDeletionsAsync(1, DeletedAt)).Single();
        await SeedReviewPromotionAsync(photo, removedAt: null);
        await using (var db = PostgresFixture.CreateDbContext(
            _database?.MigratorConnectionString ?? throw new InvalidOperationException("Teste não inicializado.")))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261029150000_PhotoDeletionRecordRetention", cancellationToken: Ct);
        }

        _time.SetUtcNow(new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, (await Purge().PurgeOnceAsync(Ct)).Tombstones);
        Assert.Equal(1, await CountAsync("""
            select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'media' and p.proname = 'purge_photo_deletion_records' and p.prosecdef
              and pg_get_userbyid(p.proowner) = 'duora_migrator'
              and has_function_privilege('duora_app', p.oid, 'EXECUTE')
              and not has_function_privilege('public', p.oid, 'EXECUTE')
            """));
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

    /// <summary>
    /// Candidato removido há 400 dias, caso editorial e promoção não confirmada com o derivado <c>full</c> em
    /// <c>review_promotion_variants</c>. Com <paramref name="removedAt"/>, o coletor já apagou os bytes nesse instante.
    /// </summary>
    private async Task SeedReviewPromotionAsync(Guid photo, DateTimeOffset? removedAt)
    {
        var candidate = Guid.NewGuid();
        var reviewCase = Guid.NewGuid();
        var promotion = Guid.NewGuid();
        var candidateRemovedAt = (await DatabaseNowAsync()).AddDays(-400);
        await ExecuteAsync("""
            insert into media.review_candidates (generation, upload_id, upload_version, detected_type, width, height,
              created_at, prepare_deadline, writer_settled_at, cleanup_requested_at, cleanup_available_at,
              last_cleanup_at, deleted_at)
            values ($1, $2, 1, 'image/jpeg', 64, 32, $3 - interval '1 hour', $3, $3 - interval '1 hour', $3, $3, $4, $4)
            """, candidate, photo, DeletedAt, candidateRemovedAt);
        await ExecuteAsync("""
            insert into safety.photo_review_cases (id, upload_id, candidate_generation, policy_version,
              awaiting_review_at, review_deadline, version)
            values ($1, $2, $3, 'human-review-v1', $4 - interval '1 hour', $4 - interval '1 hour' + interval '168 hours', 1)
            """, reviewCase, photo, candidate, DeletedAt);
        await ExecuteAsync("""
            insert into media.review_promotions (generation, upload_id, case_id, case_version, upload_version,
              candidate_generation, policy_version, detected_type, width, height, created_at, prepare_deadline,
              writer_settled_at, cleanup_available_at)
            values ($1, $2, $3, 1, 1, $4, 'human-review-v1', 'image/jpeg', 64, 32, $5 - interval '30 minutes',
              $5 - interval '30 minutes' + interval '10 seconds', $5 - interval '30 minutes', $5)
            """, promotion, photo, reviewCase, candidate, DeletedAt);
        await ExecuteAsync("""
            insert into media.review_promotion_variants (generation, kind, object_key, width, height, size_bytes, sha256)
            values ($1, 'full', 'derivatives/review-' || replace($1::text, '-', '') || '-full', 64, 32, 2048,
              sha256(convert_to('full:' || $1::text, 'UTF8')))
            """, promotion);
        if (removedAt is { } at)
        {
            await ExecuteAsync("""
                update media.review_promotions set cleanup_requested_at = $2, last_cleanup_at = $3, deleted_at = $3
                where generation = $1
                """, promotion, DeletedAt, at);
        }
    }

    /// <summary>
    /// Num único comando do cliente (bloco <c>DO</c>, como <c>duora_migrator</c>, dono das tabelas), envelhece
    /// tombstones e recibos para <c>statement_timestamp() - 1 year</c> mais <paramref name="microsecondsYounger"/> e chama o expurgo com o chamador no
    /// ano 9999. Dentro do mesmo comando, <c>statement_timestamp()</c> não muda.
    /// </summary>
    private async Task PurgeWithRecordsAgedOnTheSameStatementAsync(int microsecondsYounger)
    {
        var migrator = _database?.MigratorConnectionString ?? throw new InvalidOperationException("Teste não inicializado.");
        await using var connection = new NpgsqlConnection(migrator);
        await connection.OpenAsync(Ct);
        await using (var setting = new NpgsqlCommand("select set_config('duora_test.younger', $1, false)", connection))
        {
            setting.Parameters.AddWithValue(
                microsecondsYounger.ToString(System.Globalization.CultureInfo.InvariantCulture) + " microseconds");
            await setting.ExecuteScalarAsync(Ct);
        }

        await using var purge = new NpgsqlCommand("""
            do $$
            declare
              v_aged timestamptz;
            begin
              perform set_config('TimeZone', 'UTC', true);
              v_aged := statement_timestamp() - interval '1 year' + current_setting('duora_test.younger')::interval;
              update media.photo_tombstones set deleted_at = v_aged;
              update media.photo_deletion_commands set created_at = v_aged;
              perform media.purge_photo_deletion_records('9999-01-01 00:00:00+00', 1000);
            end
            $$
            """, connection);
        await purge.ExecuteNonQueryAsync(Ct);
    }

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
