using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Coletor de promoções privadas abandonadas registrado no Worker (#297): o mesmo padrão de log e retry dos
/// coletores de candidato. O log leva só a tentativa e o tipo da causa, nunca a geração nem a chave. Banco próprio,
/// com uma promoção sintética, sem o Worker isolado (que só roda no runner Linux).
/// </summary>
public sealed class ReviewPromotionCollectorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string Category =
        typeof(ReviewPromotionCleanupDispatcher).FullName ?? throw new InvalidOperationException("Tipo sem nome completo.");

    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-promotion-collector-");
    private ScratchDatabase? _database;
    private NpgsqlDataSource? _source;
    private ILoggerFactory? _loggers;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NpgsqlDataSource Source => _source ?? throw new InvalidOperationException("Teste não inicializado.");

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        _source = NpgsqlDataSource.Create(_database.AppConnectionString);
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
    }

    public async ValueTask DisposeAsync()
    {
        _loggers?.Dispose();
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task ASweepRemovesTheThreeKeysRecordsTheRemovalAndStaysSilent()
    {
        var generation = await SeedAbandonedPromotionAsync();
        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var kind in PhotoVariants.All)
        {
            await PutAsync(store, ObjectKey.ForPromotedDerivative(generation, kind));
        }

        Assert.Equal(1, await Collector(store).DispatchOnceAsync(Ct));

        foreach (var kind in PhotoVariants.All)
        {
            await using var removed = await store.OpenReadAsync(ObjectKey.ForPromotedDerivative(generation, kind), Ct);
            Assert.Null(removed);
        }

        Assert.Equal(1, await CountAsync(
            "select count(*) from media.review_promotions where generation = $1 and deleted_at is not null", generation));
        Assert.DoesNotContain(_logs.Entries, entry => entry.Category == Category);
    }

    [Fact]
    public async Task AFailedDeletionLogsOnlyTheAttemptAndItsCauseAndSchedulesADurableRetry()
    {
        var generation = await SeedAbandonedPromotionAsync();

        Assert.Equal(1, await Collector(new FailingStore()).DispatchOnceAsync(Ct));

        var warning = Assert.Single(_logs.Entries, entry => entry.Category == Category);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("attempt 1", warning.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), warning.Text, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(generation.ToString("N"), warning.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("derivatives/", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(generation.ToString("D"), warning.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_promotions where generation = $1 and deleted_at is null
              and cleanup_token is null and cleanup_available_at > $2 and cleanup_attempts = 1
            """, generation, _time.GetUtcNow()));
    }

    [Fact]
    public async Task ASweepThatOutlivesItsLeaseLogsTheLossAndKeepsThePersistedClaim()
    {
        var generation = await SeedAbandonedPromotionAsync();
        var store = new LeaseExpiringStore(_time);

        Assert.Equal(1, await Collector(store).DispatchOnceAsync(Ct));

        var warning = Assert.Single(_logs.Entries, entry => entry.Category == Category);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("lease", warning.Text, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(
            "select count(*) from media.review_promotions where generation = $1 and deleted_at is null and cleanup_token is not null",
            generation));
    }

    private ReviewPromotionCleanupDispatcher Collector(IObjectStore store) =>
        new(Source, store, _time, enabled: true,
            (_loggers ?? throw new InvalidOperationException("Teste não inicializado.")).CreateLogger<ReviewPromotionCleanupDispatcher>());

    private static async Task PutAsync(FileSystemObjectStore store, ObjectKey key)
    {
        var bytes = new byte[] { 1, 2, 3 };
        await using var content = new MemoryStream(bytes);
        await store.PutAsync(key, content, bytes.Length, "image/webp", Ct);
    }

    /// <summary>
    /// Conta, upload, candidato removido, caso editorial e uma promoção com escritor assentado cujo prazo de preparo
    /// passou: o coletor pode reivindicá-la já.
    /// </summary>
    private async Task<Guid> SeedAbandonedPromotionAsync()
    {
        var owner = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        var reviewCase = Guid.NewGuid();
        var promotion = Guid.NewGuid();
        var created = _time.GetUtcNow().AddHours(-1);
        await ExecuteAsync("""
            insert into identity.users (id, status, created_at, email_confirmed, phone_number_confirmed,
              two_factor_enabled, lockout_enabled, access_failed_count, security_stamp)
            values ($1, 'Active', $2, true, false, false, true, 0, 'synthetic-promotion-stamp')
            """, owner, created.AddDays(-1));
        await ExecuteAsync("""
            insert into media.uploads (id, owner_id, state, client_key, quarantine_key, declared_content_type,
              size_bytes, content_sha256, created_at, abandon_after, rejection_code, version)
            values ($1, $2, 'AwaitingReview', 'promotion-collector', 'quarantine/' || md5($1::text), 'image/jpeg',
              2048, sha256(convert_to($1::text, 'UTF8')), $3, $3 + interval '1 hour', null, 3)
            """, upload, owner, created);
        await ExecuteAsync("""
            insert into media.review_candidates (generation, upload_id, upload_version, detected_type, width, height,
              created_at, prepare_deadline, writer_settled_at, cleanup_requested_at, cleanup_available_at,
              last_cleanup_at, deleted_at)
            values ($1, $2, 1, 'image/jpeg', 64, 32, $3, $3 + interval '10 seconds', $3, $3, $3, $3, $3)
            """, candidate, upload, created);
        await ExecuteAsync("""
            insert into safety.photo_review_cases (id, upload_id, candidate_generation, policy_version,
              awaiting_review_at, review_deadline, version)
            values ($1, $2, $3, 'human-review-v1', $4, $4 + interval '168 hours', 1)
            """, reviewCase, upload, candidate, created);
        await ExecuteAsync("""
            insert into media.review_promotions (generation, upload_id, case_id, case_version, upload_version,
              candidate_generation, policy_version, detected_type, width, height, created_at, prepare_deadline,
              writer_settled_at, cleanup_available_at)
            values ($1, $2, $3, 1, 1, $4, 'human-review-v1', 'image/jpeg', 64, 32, $5, $5 + interval '10 seconds', $5, $5)
            """, promotion, upload, reviewCase, candidate, created);
        return promotion;
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

    private sealed class FailingStore : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

        /// <summary>A mensagem carrega a chave (com a geração) como canário: o log não pode repeti-la.</summary>
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new IOException($"Synthetic promotion deletion failure for {key.Value}.");
    }

    /// <summary>Cada remoção leva mais que o lease inteiro: quando a varredura termina, o lease já venceu.</summary>
    private sealed class LeaseExpiringStore(FakeTimeProvider time) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            time.Advance(ReviewPromotionCleanupDispatcher.LeaseDuration);
            return Task.CompletedTask;
        }
    }
}
