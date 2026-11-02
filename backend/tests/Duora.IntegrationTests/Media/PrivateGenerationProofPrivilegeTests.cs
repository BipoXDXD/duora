using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed class PrivateGenerationProofPrivilegeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ScratchDatabase? _database;

    public async ValueTask InitializeAsync() =>
        _database = await postgres.CreateMigratedScratchDatabaseAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("insert into media.private_generation_proofs default values")]
    [InlineData("update media.private_generation_proofs set id=id")]
    [InlineData("delete from media.private_generation_proofs")]
    [InlineData("truncate media.private_generation_proofs")]
    [InlineData("select media.record_private_generation_sweep(gen_random_uuid(),gen_random_uuid(),1,gen_random_uuid(),1::bigint,1,'0000000000000000000000000000000000000000000000000000000000000000',gen_random_uuid(),1::bigint)")]
    public async Task TheApplicationCannotForgeOrEraseStorageClosureEvidence(string statement)
    {
        await using var source = NpgsqlDataSource.Create(
            (_database ?? throw new InvalidOperationException("Database not initialized.")).AppConnectionString);
        await using var command = source.CreateCommand(statement);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task AManagedWriterAckNeverAuthorizesTheLegacyTerminalBranch()
    {
        await using var source = NpgsqlDataSource.Create(Database.AppConnectionString);
        var manifest = await SeedAsync(source, bind: true);
        await ExecuteAsync(source, """
            update media.review_candidates set writer_settled_at=statement_timestamp(),
              cleanup_requested_at=statement_timestamp() where generation=$1
            """, manifest.Identity.Generation);
        using var root = new TemporaryDirectory();
        var collector = new ReviewCandidateCleanupDispatcher(source, new FileSystemObjectStore(root.Path),
            TimeProvider.System, true, NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
        var claim = Assert.IsType<ReviewCandidateCleanupClaim>(await collector.ClaimAsync(Ct));
        Assert.True(claim.WriterWasSettled);

        var error = await Assert.ThrowsAsync<PostgresException>(() => collector.CompleteSweepAsync(claim, Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(1L, await ScalarAsync(source,
            "select count(*) from media.review_candidates where generation=$1 and deleted_at is null",
            manifest.Identity.Generation));
    }

    [Fact]
    public async Task AnOldGenerationCannotBeRetrospectivelyEnrolledAsAProtectedWriter()
    {
        await using var source = NpgsqlDataSource.Create(Database.AppConnectionString);
        var manifest = await SeedAsync(source, bind: false);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(source, """
            insert into media.private_generation_bindings(generation,area,domain,epoch,layout,manifest_hash)
            values($1,1,$2,1,1,$3)
            """, manifest.Identity.Generation, manifest.Identity.Domain.Id, manifest.Identity.ManifestHash));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task OnlyTheCurrentCollectorsSweepProofCanCompleteWithoutInventingAWriterAck()
    {
        await using var source = NpgsqlDataSource.Create(Database.AppConnectionString);
        await using var verifier = NpgsqlDataSource.Create(Database.MigratorConnectionString);
        var manifest = await SeedAsync(source, bind: true);
        var generation = manifest.Identity.Generation;
        await ExecuteAsync(source, """
            update media.review_candidates set cleanup_requested_at=statement_timestamp() where generation=$1
            """, generation);
        using var root = new TemporaryDirectory();
        var collector = new ReviewCandidateCleanupDispatcher(source, new FileSystemObjectStore(root.Path),
            TimeProvider.System, true, NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
        var old = Assert.IsType<ReviewCandidateCleanupClaim>(await collector.ClaimAsync(Ct));
        // Trusted verifier fixture isolates SQL ownership rules; it does not attest to filesystem I/O.
        var oldProof = await ProofAsync(verifier, manifest, old.Token);
        await ExecuteAsync(source, """
            update media.review_candidates set cleanup_lease_until=statement_timestamp()-interval '1 second',
              cleanup_available_at=statement_timestamp() where generation=$1
            """, generation);
        var current = Assert.IsType<ReviewCandidateCleanupClaim>(await collector.ClaimAsync(Ct));
        Assert.NotEqual(old.Token, current.Token);
        Assert.False(await collector.CompleteSweepAsync(old with { StorageProofId = oldProof }, Ct));
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            collector.CompleteSweepAsync(current with { StorageProofId = oldProof }, Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        var proof = await ProofAsync(verifier, manifest, current.Token);

        Assert.True(await collector.CompleteSweepAsync(current with { StorageProofId = proof }, Ct));

        Assert.Equal(1L, await ScalarAsync(source, """
            select count(*) from media.review_candidates where generation=$1 and deleted_at is not null
              and writer_settled_at is null and storage_proof_id=$2
            """, generation, proof));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ScratchDatabase Database => _database ?? throw new InvalidOperationException("Database not initialized.");

    private static async Task<PrivateGenerationManifest> SeedAsync(NpgsqlDataSource source, bool bind)
    {
        var owner = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var manifest = PrivateGenerationManifest.Create(new(Guid.NewGuid(), 1, 1),
            PrivateGenerationArea.Candidate, generation, PhotoVariants.All.Select(kind =>
                new PrivateGenerationVariant(kind, 3,
                    "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81")));
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into identity.users(id,status,created_at,email_confirmed,phone_number_confirmed,
              two_factor_enabled,lockout_enabled,access_failed_count,security_stamp)
            values(@owner,'Active',statement_timestamp(),true,false,false,true,0,'synthetic-authority');
            insert into media.uploads(id,owner_id,state,client_key,quarantine_key,declared_content_type,
              size_bytes,content_sha256,created_at,abandon_after,version)
            values(@upload,@owner,'Processing','authority-'||@upload::text,'quarantine/'||md5(@upload::text),
              'image/jpeg',3,sha256('synthetic'::bytea),statement_timestamp(),
              statement_timestamp()+interval '1 hour',2);
            insert into media.review_candidates(generation,upload_id,upload_version,detected_type,width,height,
              created_at,prepare_deadline,cleanup_available_at)
            values(@generation,@upload,2,'image/jpeg',64,32,statement_timestamp(),
              statement_timestamp()+interval '5 minutes',statement_timestamp())
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("upload", upload);
        command.Parameters.AddWithValue("generation", generation);
        await command.ExecuteNonQueryAsync(Ct);
        if (bind)
        {
            await using var binding = new NpgsqlCommand("""
                insert into media.private_generation_bindings(generation,area,domain,epoch,layout,manifest_hash)
                values($1,1,$2,1,1,$3)
                """, connection, transaction);
            binding.Parameters.AddWithValue(generation);
            binding.Parameters.AddWithValue(manifest.Identity.Domain.Id);
            binding.Parameters.AddWithValue(manifest.Identity.ManifestHash);
            await binding.ExecuteNonQueryAsync(Ct);
        }
        await transaction.CommitAsync(Ct);
        return manifest;
    }

    private static async Task<Guid> ProofAsync(NpgsqlDataSource source, PrivateGenerationManifest manifest, Guid token)
    {
        await using var command = source.CreateCommand(
            "select media.record_private_generation_sweep($1,$2,1,$3,1::bigint,1,$4,$5,7::bigint)");
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(manifest.Identity.Generation);
        command.Parameters.AddWithValue(manifest.Identity.Domain.Id);
        command.Parameters.AddWithValue(manifest.Identity.ManifestHash);
        command.Parameters.AddWithValue(token);
        return (Guid)(await command.ExecuteScalarAsync(Ct)
            ?? throw new InvalidOperationException("Missing proof."));
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values)
        { command.Parameters.AddWithValue(value); }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values)
        { command.Parameters.AddWithValue(value); }
        return await command.ExecuteScalarAsync(Ct);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("duora-proof-collector-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
