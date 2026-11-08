using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public static partial class StorageAuthorityRuntimeChecks
{
    private static async Task VerifyControlAndReferenceFailuresAsync(string root, string referenceDatabase,
        NpgsqlDataSource admin, CancellationToken cancellationToken)
    {
        var journal = Path.Combine(root, "control/domain.journal");
        var beforeRegistration = await File.ReadAllBytesAsync(journal, cancellationToken);
        await RunClientProcessAsync(root, "create-open", cancellationToken);
        var goodOpen = await File.ReadAllBytesAsync(journal, cancellationToken);
        var savedJournal = Path.Combine(root, "control/domain.journal.saved");
        File.Move(journal, savedJournal);
        await RunClientProcessAsync(root, "write-denied", cancellationToken);
        await RunClientProcessAsync(root, "read-denied", cancellationToken);
        File.Move(savedJournal, journal);
        await File.WriteAllBytesAsync(journal, goodOpen[..^1], cancellationToken);
        await RunClientProcessAsync(root, "write-denied", cancellationToken);
        // RED for journal-content bypass: same inode/mode, valid existing object, no bytes may be served.
        await RunClientProcessAsync(root, "read-denied", cancellationToken);
        await File.WriteAllBytesAsync(journal, beforeRegistration, cancellationToken);
        await RunClientProcessAsync(root, "read-denied", cancellationToken);
        await File.WriteAllBytesAsync(journal, goodOpen, cancellationToken);
        await RunClientProcessAsync(root, "seal-only", cancellationToken);
        var goodSealed = await File.ReadAllBytesAsync(journal, cancellationToken);
        await File.WriteAllBytesAsync(journal, goodOpen, cancellationToken);
        await RunClientProcessAsync(root, "read-denied", cancellationToken);
        await File.WriteAllBytesAsync(journal, goodSealed, cancellationToken);
        var domainLock = Path.Combine(root, "control/domain.lock");
        var savedLock = Path.Combine(root, "control/domain.lock.saved");
        File.Move(domainLock, savedLock);
        File.WriteAllText(domainLock, "");
        Mode(domainLock, 0, 17010, 432);
        await RunClientProcessAsync(root, "read-denied", cancellationToken);
        File.Delete(domainLock);
        File.Move(savedLock, domainLock);
        Console.WriteLine("PASS missing/torn/regressed journal, missing local identity and substituted regular lock inode deny reads/bytes");
        // Reference failure must be independent of a valid journal and of the available business DB.
        await RunClientProcessAsync(root, "create-open", cancellationToken);
        await ExecuteAsync(admin, $"alter database {referenceDatabase} allow_connections false", cancellationToken);
        await ExecuteAsync(admin, "select pg_terminate_backend(pid) from pg_stat_activity where datname=$1",
            cancellationToken, referenceDatabase);
        await RunClientProcessAsync(root, "write-denied", cancellationToken);
        await RunClientProcessAsync(root, "read-denied", cancellationToken);
        await ExecuteAsync(admin, $"alter database {referenceDatabase} allow_connections true", cancellationToken);
        await RunClientProcessAsync(root, "finish-open", cancellationToken);
        Console.WriteLine("PASS independent reference outage denies reads and writes; preserved state recovers");
    }

    /// <summary>
    /// The journal chain has no secret, so a well-chained entry without identity reaches the journal reader and
    /// throws outside the denial list. No production seam: the actual host, peer check and PostgreSQL are in play.
    /// </summary>
    private static async Task VerifyUnexpectedFailureIsRecordedAsync(string root, AuthorityProcess authority,
        CancellationToken cancellationToken)
    {
        var journal = Path.Combine(root, "control/domain.journal");
        await RunClientProcessAsync(root, "create-open", cancellationToken);
        var intact = await File.ReadAllBytesAsync(journal, cancellationToken);
        await File.WriteAllBytesAsync(journal, WithChainedEntryWithoutIdentity(intact), cancellationToken);
        await RunClientProcessAsync(root, "read-unexpected-failure", cancellationToken);
        var line = await authority.UnexpectedFailure.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.StartsWith("STORAGE_OPERATION_FAILED NullReferenceException ", line, StringComparison.Ordinal);
        Assert.Contains("PrivateGenerationJournal.Read", line, StringComparison.Ordinal);
        await File.WriteAllBytesAsync(journal, intact, cancellationToken);
        await RunClientProcessAsync(root, "finish-open", cancellationToken);
        Console.WriteLine("PASS failure outside the denial list is recorded and denied; the host keeps serving");
    }

    private static byte[] WithChainedEntryWithoutIdentity(byte[] journal)
    {
        var text = Encoding.UTF8.GetString(journal);
        var previous = text.Split('\n')[^2].Split('\t')[1];
        var payload = Convert.ToBase64String("""{"identity":null,"state":"Open","revision":1}"""u8);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + payload)));
        return Encoding.UTF8.GetBytes($"{text}{payload}\t{hash}\n");
    }

    private static async Task RunControlFaultClientAsync(ClientParameters parameters, NpgsqlDataSource app,
        IObjectStore store, IPrivateGenerationStore managed, string mode, CancellationToken cancellationToken)
    {
        var path = Path.Combine(parameters.Root, "client-out/fault-identity.json");
        if (mode == "create-open")
        {
            var (manifest, contents) = await OpenManifestAsync(parameters, store, app, [3, 3, 3], cancellationToken);
            await managed.RegisterAsync(manifest, cancellationToken);
            using var bytes = new MemoryStream(contents[PhotoVariant.Thumb], writable: false);
            await managed.PutVariantAsync(manifest, PhotoVariant.Thumb, bytes, cancellationToken);
            await using var present = await store.OpenReadAsync(manifest.Key(PhotoVariant.Thumb), cancellationToken);
            Assert.NotNull(present);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest.Identity), cancellationToken);
            return;
        }
        var identity = JsonSerializer.Deserialize<PrivateGenerationIdentity>(
            await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidDataException("Missing control fault identity.");
        var record = Assert.IsType<PrivateGenerationStorageRecord>(
            await new PrivateGenerationAuthorityMetadata(app).ReadAsync(identity.Area, identity.Generation, cancellationToken));
        if (mode == "write-denied")
        {
            using var bytes = new MemoryStream([42, 42, 42]);
            await Assert.ThrowsAsync<IOException>(() => managed.PutVariantAsync(
                record.Manifest, PhotoVariant.Thumb, bytes, cancellationToken));
            Assert.Equal(0L, await ScalarAsync(app,
                "select count(*) from media.private_generation_proofs where generation=$1", cancellationToken,
                identity.Generation));
            return;
        }
        if (mode == "read-unexpected-failure")
        {
            var key = record.Manifest.Key(PhotoVariant.Thumb);
            Assert.Equal("denied", await RawRequestAsync(parameters, new(1, "read", ObjectKey: key.Value), null,
                cancellationToken));
            await Assert.ThrowsAsync<IOException>(() => store.OpenReadAsync(key, cancellationToken));
            return;
        }
        if (mode == "read-denied")
        {
            await Assert.ThrowsAsync<IOException>(() => store.OpenReadAsync(
                record.Manifest.Key(PhotoVariant.Thumb), cancellationToken));
            return;
        }
        foreach (var kind in PhotoVariants.All)
        {
            using var bytes = new MemoryStream([42, 42, 42]);
            await managed.PutVariantAsync(record.Manifest, kind, bytes, cancellationToken);
        }
        await managed.SealAsync(record.Manifest, cancellationToken);
        if (mode == "finish-open")
        {
            await CollectAsync(app, store, identity.Generation, cancellationToken);
        }
    }

    private static async Task<Guid> VerifyPausedAndMaximumBodiesAsync(ClientParameters parameters, NpgsqlDataSource app,
        IObjectStore store, IPrivateGenerationStore managed, CancellationToken cancellationToken)
    {
        var (pausedManifest, pausedBytes) = await OpenManifestAsync(parameters, store, app,
            [300_000, 300_000, 300_000], cancellationToken);
        await managed.RegisterAsync(pausedManifest, cancellationToken);
        using (var paused = new PausedEofStream(pausedBytes[PhotoVariant.Thumb]))
        {
            var writing = managed.PutVariantAsync(pausedManifest, PhotoVariant.Thumb, paused, cancellationToken);
            await paused.AtEof.WaitAsync(cancellationToken);
            await ExecuteAsync(app, """
                update media.review_candidates set cleanup_requested_at=statement_timestamp(),
                  cleanup_available_at=statement_timestamp() where generation=$1
                """, cancellationToken, pausedManifest.Identity.Generation);
            Assert.Equal("busy", await RawRequestAsync(parameters,
                new(1, "close", pausedManifest.Identity), null, cancellationToken));
            var collector = new ReviewCandidateCleanupDispatcher(app, store, TimeProvider.System, true,
                NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
            Assert.Equal(1, await collector.DispatchOnceAsync(cancellationToken));
            Assert.Equal(0L, await ScalarAsync(app,
                "select count(*) from media.private_generation_proofs where generation=$1",
                cancellationToken, pausedManifest.Identity.Generation));
            Assert.Equal(1L, await ScalarAsync(app, """
                select count(*) from media.review_candidates where generation=$1
                  and writer_settled_at is null and deleted_at is null
                """, cancellationToken, pausedManifest.Identity.Generation));
            paused.Release();
            await writing;
        }
        await CollectAsync(app, store, pausedManifest.Identity.Generation, cancellationToken);
        await Assert.ThrowsAsync<IOException>(() => managed.RegisterAsync(pausedManifest, cancellationToken));
        using (var late = new MemoryStream(pausedBytes[PhotoVariant.Thumb], writable: false))
        {
            await Assert.ThrowsAsync<IOException>(() => managed.PutVariantAsync(
                pausedManifest, PhotoVariant.Thumb, late, cancellationToken));
        }
        var firstReceipt = await managed.CloseAsync(pausedManifest.Identity, cancellationToken);
        var secondReceipt = await managed.CloseAsync(pausedManifest.Identity, cancellationToken);
        Assert.Equal(firstReceipt, secondReceipt);
        Console.WriteLine("PASS admitted PUT paused before EOF keeps Close busy and SQL nonterminal; replay denied after durable sweep");

        var (large, contents) = await OpenManifestAsync(parameters, store, app,
            [16_777_216, 4_194_304, 4_194_304], cancellationToken);
        await managed.RegisterAsync(large, cancellationToken);
        foreach (var kind in PhotoVariants.All)
        {
            using var bytes = new MemoryStream(contents[kind], writable: false);
            await managed.PutVariantAsync(large, kind, bytes, cancellationToken);
        }
        await managed.SealAsync(large, cancellationToken);
        await CollectAsync(app, store, large.Identity.Generation, cancellationToken);
        Console.WriteLine("PASS real RPC accepts 16MiB variant and 24MiB aggregate, seals and durably sweeps");

        var (malformed, _) = await OpenManifestAsync(parameters, store, app, [3, 3, 3], cancellationToken);
        await managed.RegisterAsync(malformed, cancellationToken);
        var request = new PrivateGenerationRequest(1, "put", malformed.Identity, malformed, PhotoVariant.Thumb, 3);
        Assert.Equal("denied", await RawRequestAsync(parameters, request, [42, 42, 42, 42], cancellationToken));
        Assert.Equal("denied", await RawRequestAsync(parameters, request, [42, 42], cancellationToken));
        await using (var absent = await store.OpenReadAsync(malformed.Key(PhotoVariant.Thumb), cancellationToken))
        {
            Assert.Null(absent);
        }
        await CollectAsync(app, store, malformed.Identity.Generation, cancellationToken);
        Console.WriteLine("PASS excess and truncated bodies cannot publish a private object");
        return pausedManifest.Identity.Generation;
    }

    private static async Task CollectAsync(NpgsqlDataSource app, IObjectStore store, Guid generation,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(app, """
            update media.review_candidates set cleanup_requested_at=coalesce(cleanup_requested_at,statement_timestamp()),
              cleanup_available_at=statement_timestamp() where generation=$1
            """, cancellationToken, generation);
        var collector = new ReviewCandidateCleanupDispatcher(app, store, TimeProvider.System, true,
            NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
        Assert.Equal(1, await collector.DispatchOnceAsync(cancellationToken));
        Assert.Equal(1L, await ScalarAsync(app, """
            select count(*) from media.review_candidates where generation=$1 and deleted_at is not null
              and writer_settled_at is null and storage_proof_id is not null
            """, cancellationToken, generation));
    }

    private static async Task<(PrivateGenerationManifest Manifest, Dictionary<PhotoVariant, byte[]> Bytes)> OpenManifestAsync(
        ClientParameters parameters, IObjectStore store, NpgsqlDataSource app, int[] sizes,
        CancellationToken cancellationToken)
    {
        var upload = await SeedUploadAsync(app, cancellationToken);
        var generation = Guid.NewGuid();
        var contents = PhotoVariants.All.ToDictionary(kind => kind,
            kind => Enumerable.Repeat((byte)42, sizes[(int)kind]).ToArray());
        var variants = contents.Select(pair => new PrivateGenerationVariant(pair.Key, pair.Value.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(pair.Value)))).ToArray();
        var manifest = PrivateGenerationManifest.Create(parameters.Domain, PrivateGenerationArea.Candidate, generation, variants);
        await using var db = PostgresFixture.CreateDbContext(parameters.ConnectionString);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into media.review_candidates(generation,upload_id,upload_version,detected_type,width,height,
              created_at,prepare_deadline,cleanup_available_at)
            values({generation},{upload},2,'image/jpeg',64,32,statement_timestamp(),
              statement_timestamp()+interval '5 minutes',statement_timestamp())
            """, cancellationToken);
        foreach (var variant in variants)
        {
            var key = manifest.Key(variant.Kind);
            var hash = Convert.FromHexString(variant.Sha256);
            await db.Database.ExecuteSqlAsync($"""
                insert into media.review_candidate_variants(generation,kind,object_key,width,height,size_bytes,sha256)
                values({generation},{PhotoVariants.Name(variant.Kind)},{key.Value},64,32,{variant.Length},{hash})
                """, cancellationToken);
        }
        await PrivateGenerationMetadata.BindAsync(db, store, PrivateGenerationArea.Candidate, generation, variants,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (manifest, contents);
    }

    private static async Task<string> RawRequestAsync(ClientParameters parameters, PrivateGenerationRequest request,
        byte[]? bytes, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(parameters.Root, "rpc/authority.sock")),
            cancellationToken);
        Assert.Equal(17010, PrivateGenerationLinux.Peer(socket).Uid);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await PrivateGenerationWire.WriteAsync(stream, request, cancellationToken);
        if (bytes is not null)
        {
            var admission = await PrivateGenerationWire.ReadAsync<PrivateGenerationReply>(stream, cancellationToken);
            Assert.Equal("admitted", admission.Status);
            await stream.WriteAsync(bytes, cancellationToken);
        }
        socket.Shutdown(SocketShutdown.Send);
        var reply = await PrivateGenerationWire.ReadAsync<PrivateGenerationReply>(stream, cancellationToken);
        return reply.Status;
    }

    private sealed class PausedEofStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        private readonly TaskCompletionSource _atEof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task AtEof => _atEof.Task;
        internal void Release() => _release.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_inner.Position == _inner.Length)
            {
                _atEof.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                return 0;
            }
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            { Release(); _inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
