using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Explicit Linux acceptance entry, called by scripts/testing/storage-authority/RuntimeChecks.csproj.
/// It executes the actual standalone host and real preparers/collector against disposable PostgreSQL.
/// Kept outside automatic macOS test selection; no test is marked skipped or silently passed.
/// </summary>
[SupportedOSPlatform("linux")]
public static partial class StorageAuthorityRuntimeChecks
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        Assert.True(OperatingSystem.IsLinux());
        Assert.Equal(0U, PrivateGenerationLinux.CurrentUid);
        await using var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        await using var database = await postgres.CreateMigratedScratchDatabaseAsync(cancellationToken);
        await using var referenceDatabase = await postgres.CreateEmptyDatabaseAsync(cancellationToken);
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var custodian = NpgsqlDataSource.Create(referenceDatabase.MigratorConnectionString);
        await using var admin = NpgsqlDataSource.Create(postgres.ScratchSuperuserConnectionString(database));
        var suffix = Guid.NewGuid().ToString("N");
        var businessRole = "authority_business_" + suffix;
        var referenceRole = "authority_reference_" + suffix;
        var businessPassword = Guid.NewGuid().ToString("N");
        var referencePassword = Guid.NewGuid().ToString("N");
        // All administration below belongs to this fixture's freshly created container/databases.
        await ExecuteAsync(admin, $"""
            create role {businessRole} login password '{businessPassword}';
            create role {referenceRole} login password '{referencePassword}';
            grant connect on database {database.Name} to {businessRole};
            grant connect on database {referenceDatabase.Name} to {referenceRole};
            grant usage on schema media to {businessRole};
            grant select on media.private_generation_bindings,media.review_candidates,media.review_candidate_variants,
              media.review_promotions,media.review_promotion_variants,media.uploads,media.photo_deletion_cleanup
              to {businessRole};
            grant execute on function media.record_private_generation_sweep
              (uuid,uuid,integer,uuid,bigint,integer,text,uuid,bigint) to {businessRole};
            """, cancellationToken);
        await ExecuteAsync(custodian, await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "scripts/storage-authority/reference.sql"), cancellationToken),
            cancellationToken);
        await ExecuteAsync(custodian, $"""
            grant usage on schema storage_control to {referenceRole};
            grant execute on function storage_control.attach(uuid,bigint,integer,uuid,uuid),
              storage_control.transition(uuid,bigint,integer,uuid,uuid,integer,uuid,text,text)
              to {referenceRole};
            """, cancellationToken);
        var domain = new PrivateGenerationDomain(Guid.NewGuid(), 1, 1);
        var incarnation = Guid.NewGuid();
        await ExecuteAsync(custodian, """
            insert into storage_control.epochs(domain,epoch,layout,incarnation,active)
            values($1,1,1,$2,true)
            """, cancellationToken, domain.Id, incarnation);
        var businessConnection = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        {
            Username = businessRole,
            Password = businessPassword,
            MaxPoolSize = 8,
            Timeout = 3,
            CommandTimeout = 3,
        }.ConnectionString;
        var referenceConnection = new NpgsqlConnectionStringBuilder(referenceDatabase.AppConnectionString)
        {
            Username = referenceRole,
            Password = referencePassword,
            MaxPoolSize = 8,
            Timeout = 3,
            CommandTimeout = 3,
        }.ConnectionString;
        var root = "/duora-authority-" + suffix;
        Directory.CreateDirectory(root);
        AuthorityProcess? authority = null;
        try
        {
            Provision(root, domain, incarnation, businessConnection, referenceConnection);
            authority = await AuthorityProcess.StartAsync(root, expectReady: true, cancellationToken);
            WriteClientParameters(root, domain, database.AppConnectionString);
            await RunClientProcessAsync(root, "pipeline", cancellationToken);
            var outcome = JsonSerializer.Deserialize<Outcome>(await File.ReadAllTextAsync(
                Path.Combine(root, "client-out/outcome.json"), cancellationToken))
                ?? throw new InvalidDataException("Missing client outcome.");
            var candidate = outcome.Candidate;
            var promotion = outcome.Promotion;
            var rollbackTarget = outcome.OpenClosedCandidate;
            foreach (var kind in PhotoVariants.All)
            {
                Assert.False(File.Exists(Path.Combine(root, "data", ObjectKey.ForPromotedDerivative(promotion, kind).Value)));
                Assert.True(File.Exists(Path.Combine(root, "data", ObjectKey.ForReviewCandidate(candidate, kind).Value)));
            }
            Console.WriteLine("PASS physical observation agrees with child pipeline and SQL proof");

            await AssertKernelDeniedAsync(root, cancellationToken);
            Console.WriteLine("PASS legacy UID and client UID cannot replace controls or publish private bytes");

            // Controlled rollback of local controls/business state. Independent revocation is preserved.
            var journalBeforeRollback = await File.ReadAllBytesAsync(Path.Combine(root, "control/domain.journal"), cancellationToken);
            // Restore a genuinely Open generation, so Seal cannot mask a missing external revocation check.
            var text = System.Text.Encoding.UTF8.GetString(journalBeforeRollback);
            var lines = text.Split('\n');
            var cut = Array.FindIndex(lines, line => line.Contains('\t')
                && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0]))
                    .Contains("\"state\":\"Closing\"", StringComparison.Ordinal)
                && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0]))
                    .Contains(rollbackTarget.ToString("D"), StringComparison.Ordinal));
            Assert.True(cut >= 3);
            await File.WriteAllTextAsync(Path.Combine(root, "control/domain.journal"),
                string.Join('\n', lines[..cut]) + "\n", cancellationToken);
            await ExecuteAsync(app, """
                update media.review_candidates set deleted_at=null,storage_proof_id=null,writer_settled_at=null,
                  cleanup_requested_at=null,created_at=statement_timestamp(),prepare_deadline=statement_timestamp()+interval '5 minutes'
                  where generation=$1
                """, cancellationToken, rollbackTarget);
            await RunClientProcessAsync(root, "replay-denied", cancellationToken);
            Assert.False(File.Exists(Path.Combine(root, "data", ObjectKey.ForReviewCandidate(rollbackTarget, PhotoVariant.Thumb).Value)));
            Console.WriteLine("PASS local Open rollback cannot erase the separately persisted revocation");

            await authority.StopAsync(cancellationToken);
            authority = null;
            await using (var denied = await AuthorityProcess.StartAsync(root, expectReady: false, cancellationToken))
            {
                Assert.Equal(2, denied.Process.ExitCode);
            }
            Console.WriteLine("PASS restart without custodian epoch readmission is denied");
            // Explicit fixture custodian step, only AFTER observing actual old process exit.
            await ExecuteAsync(custodian, """
                update storage_control.epochs set active=false where domain=$1 and epoch=1;
                insert into storage_control.epochs(domain,epoch,layout,incarnation,active) values($1,2,1,$2,true)
                """, cancellationToken, domain.Id, incarnation);
            domain = domain with { Epoch = 2 };
            WriteEnrollment(root, domain, incarnation);
            authority = await AuthorityProcess.StartAsync(root, expectReady: true, cancellationToken);
            WriteClientParameters(root, domain, database.AppConnectionString);
            await ExecuteAsync(app, """
                update media.review_candidates set cleanup_requested_at=statement_timestamp(),
                  cleanup_available_at=statement_timestamp() where generation=$1
                """, cancellationToken, rollbackTarget);
            await RunClientProcessAsync(root, "recover-close", cancellationToken);
            Console.WriteLine("PASS admitted new epoch recovers old closure without readmitting the old writer");

            await RunClientProcessAsync(root, "legacy-denied", cancellationToken);
            Console.WriteLine("PASS unbound private legacy reads/deletes fail closed with original files intact");
            await VerifyControlAndReferenceFailuresAsync(root, referenceDatabase.Name, admin, cancellationToken);
        }
        finally
        {
            if (authority is not null)
            {
                await authority.StopAsync(CancellationToken.None);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task RunClientAsync(string path, string mode, CancellationToken cancellationToken)
    {
        Assert.Equal(17020U, PrivateGenerationLinux.CurrentUid);
        Assert.Equal(17020U, PrivateGenerationLinux.CurrentGid);
        var parameters = JsonSerializer.Deserialize<ClientParameters>(await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidDataException("Missing client parameters.");
        var root = parameters.Root;
        var domain = parameters.Domain;
        Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(Path.Combine(root, "business.json")));
        Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(Path.Combine(root, "reference.json")));
        await using var app = NpgsqlDataSource.Create(parameters.ConnectionString);
        if (mode == "pipeline")
        {
            using var services = Services(root, domain).BuildServiceProvider();
            var store = services.GetRequiredService<IObjectStore>();
            var managed = Assert.IsAssignableFrom<IPrivateGenerationStore>(store);
            var upload = await SeedUploadAsync(app, cancellationToken);
            await using (var db = PostgresFixture.CreateDbContext(parameters.ConnectionString))
            {
                var sanitized = new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 64, 32,
                    [.. PhotoVariants.All.Select(kind => new SanitizedVariant(kind, TestImages.WebP(64, 32), 64, 32))]);
                var staged = await new PhotoReviewCandidates(db, store, TimeProvider.System, new MediaProcessingOptions())
                    .StageAsync(upload, 2, DateTimeOffset.UtcNow.AddSeconds(30), sanitized, cancellationToken);
                Assert.IsType<ReviewCandidateStageResult.Staged>(staged);
            }
            Guid candidate;
            Guid caseId;
            long caseVersion;
            string policy;
            await using (var command = app.CreateCommand("""
                select c.generation,r.id,r.version,r.policy_version
                from media.review_candidates c join safety.photo_review_cases r on r.id=c.case_id where c.upload_id=$1
                """))
            {
                command.Parameters.AddWithValue(upload);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                candidate = reader.GetGuid(0);
                caseId = reader.GetGuid(1);
                caseVersion = reader.GetInt64(2);
                policy = reader.GetString(3);
            }
            Guid promotion;
            await using (var db = PostgresFixture.CreateDbContext(parameters.ConnectionString))
            {
                var result = await new PhotoReviewPromotions(db, store, TimeProvider.System).PrepareAsync(
                    new(caseId, caseVersion, 3, candidate, policy), cancellationToken);
                promotion = Assert.IsType<ReviewPromotionResult.Prepared>(result).Generation;
            }
            Assert.Equal(2L, await ScalarAsync(app,
                "select count(*) from media.private_generation_bindings where generation in ($1,$2)",
                cancellationToken, candidate, promotion));

            var binding = Assert.IsType<PrivateGenerationBinding>(await PrivateGenerationMetadata.FindAsync(
                app, PrivateGenerationArea.Promotion, promotion, cancellationToken));
            var identity = binding.Identity(PrivateGenerationArea.Promotion, promotion);
            await Assert.ThrowsAsync<IOException>(() => managed.SweepAsync(new(identity, 1), Guid.NewGuid(),
                cancellationToken));
            await ExecuteAsync(app, """
                update media.review_promotions set writer_settled_at=null,cleanup_requested_at=statement_timestamp(),
                  cleanup_available_at=statement_timestamp() where generation=$1
                """, cancellationToken, promotion);
            var collector = new ReviewPromotionCleanupDispatcher(app, store, TimeProvider.System, true,
                NullLogger<ReviewPromotionCleanupDispatcher>.Instance);
            Assert.Equal(1, await collector.DispatchOnceAsync(cancellationToken));
            foreach (var kind in PhotoVariants.All)
            {
                await using var removed = await store.OpenReadAsync(ObjectKey.ForPromotedDerivative(promotion, kind), cancellationToken);
                Assert.Null(removed);
                await using var present = await store.OpenReadAsync(ObjectKey.ForReviewCandidate(candidate, kind), cancellationToken);
                Assert.NotNull(present);
            }
            Assert.Equal(1L, await ScalarAsync(app, """
                select count(*) from media.review_promotions where generation=$1 and deleted_at is not null
                  and writer_settled_at is null and storage_proof_id is not null
                """, cancellationToken, promotion));
            Console.WriteLine("PASS client UID17020 real candidate/promotion/collector with application credentials only");
            var openClosed = await VerifyPausedAndMaximumBodiesAsync(parameters, app, store, managed, cancellationToken);
            await File.WriteAllTextAsync(parameters.OutcomePath,
                JsonSerializer.Serialize(new Outcome(candidate, promotion, openClosed)), cancellationToken);
            return;
        }
        using var container = Services(root, domain).BuildServiceProvider();
        var objectStore = container.GetRequiredService<IObjectStore>();
        var privateStore = Assert.IsAssignableFrom<IPrivateGenerationStore>(objectStore);
        if (mode is "create-open" or "write-denied" or "read-denied" or "seal-only" or "finish-open")
        {
            await RunControlFaultClientAsync(parameters, app, objectStore, privateStore, mode, cancellationToken);
            return;
        }
        var outcome = JsonSerializer.Deserialize<Outcome>(await File.ReadAllTextAsync(parameters.OutcomePath, cancellationToken))
            ?? throw new InvalidDataException("Missing client outcome.");
        var record = Assert.IsType<PrivateGenerationStorageRecord>(await new PrivateGenerationAuthorityMetadata(app)
            .ReadAsync(PrivateGenerationArea.Candidate, outcome.OpenClosedCandidate, cancellationToken));
        if (mode == "replay-denied")
        {
            await Assert.ThrowsAsync<IOException>(() => privateStore.RegisterAsync(record.Manifest, cancellationToken));
            using var content = new MemoryStream(Enumerable.Repeat((byte)42,
                checked((int)record.Manifest.Variants.Single(v => v.Kind == PhotoVariant.Thumb).Length)).ToArray(), writable: false);
            await Assert.ThrowsAsync<IOException>(() => privateStore.PutVariantAsync(record.Manifest,
                PhotoVariant.Thumb, content, cancellationToken));
        }
        else if (mode == "recover-close")
        {
            var closed = await privateStore.CloseAsync(record.Manifest.Identity, cancellationToken);
            Assert.True(closed.Revision > 0);
            await CollectAsync(app, objectStore, record.Manifest.Identity.Generation, cancellationToken);
            using var oldContainer = Services(root, domain with { Epoch = 1 }).BuildServiceProvider();
            var oldStore = Assert.IsAssignableFrom<IPrivateGenerationStore>(oldContainer.GetRequiredService<IObjectStore>());
            await Assert.ThrowsAsync<IOException>(() => oldStore.RegisterAsync(record.Manifest, cancellationToken));
        }
        else if (mode == "legacy-denied")
        {
            await LegacyPrivateReadIsUnavailableAsync(root, objectStore, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Unknown synthetic client operation.");
        }
        Console.WriteLine("PASS UID17020 client " + mode);
    }

    private static void WriteClientParameters(string root, PrivateGenerationDomain domain, string connectionString)
    {
        var path = Path.Combine(root, "client.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ClientParameters(root, domain, connectionString,
            Path.Combine(root, "client-out/outcome.json"))));
        Mode(path, 0, 17020, 288);
    }

    private static async Task RunClientProcessAsync(string root, string mode, CancellationToken cancellationToken)
    {
        var dll = Path.Combine(RepositoryRoot.Find(), "scripts/testing/storage-authority/bin/Debug/net10.0/RuntimeChecks.dll");
        using var process = Process.Start(new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-c", "import os,sys; os.setgroups([]); os.setgid(17020); os.setuid(17020); os.execvp('dotnet',['dotnet',sys.argv[1],'--client',sys.argv[2],sys.argv[3]])",
                dll, Path.Combine(root, "client.json"), mode,
            },
        }) ?? throw new InvalidOperationException("Client process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var text = await output;
        var errorText = await errors;
        Assert.True(process.ExitCode == 0, "Client failed: " + errorText);
        Console.Write(text);
    }

    private sealed record ClientParameters(string Root, PrivateGenerationDomain Domain, string ConnectionString,
        string OutcomePath);
    private sealed record Outcome(Guid Candidate, Guid Promotion, Guid OpenClosedCandidate);

    private static IServiceCollection Services(string root, PrivateGenerationDomain domain) =>
        new ServiceCollection().AddLogging().AddMediaUploads(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Media:Storage:Provider"] = "FileSystem",
                ["Media:Storage:FileSystemRoot"] = Path.Combine(root, "legacy"),
                ["Media:PrivateGenerations:Enabled"] = "true",
                ["Media:PrivateGenerations:SocketPath"] = Path.Combine(root, "rpc/authority.sock"),
                ["Media:PrivateGenerations:AuthorityUid"] = "17010",
                ["Media:PrivateGenerations:DomainId"] = domain.Id.ToString("D"),
                ["Media:PrivateGenerations:Epoch"] = domain.Epoch.ToString(CultureInfo.InvariantCulture),
            }).Build(), "Testing");

    private static void Provision(string root, PrivateGenerationDomain domain, Guid incarnation,
        string businessConnection, string referenceConnection)
    {
        Mode(root, 0, 0, 493);
        foreach (var name in new[] { "control", "data", "rpc", "legacy" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
        }
        Mode(Path.Combine(root, "control"), 0, 17010, 488);
        Mode(Path.Combine(root, "data"), 0, 0, 493);
        Mode(Path.Combine(root, "rpc"), 0, 17010, 505);
        Mode(Path.Combine(root, "legacy"), 17020, 17020, 448);
        Directory.CreateDirectory(Path.Combine(root, "client-out"));
        Mode(Path.Combine(root, "client-out"), 17020, 17020, 448);
        foreach (var name in new[] { "review-candidates", "derivatives" })
        {
            var directory = Path.Combine(root, "data", name);
            Directory.CreateDirectory(directory);
            Mode(directory, 17010, 17010, 448);
        }
        File.WriteAllText(Path.Combine(root, "control/domain.lock"), "");
        File.WriteAllText(Path.Combine(root, "control/domain.journal"),
            $"duora-private-journal-v1\n{domain.Id:N}\n{incarnation:N}\n");
        foreach (var name in new[] { "domain.lock", "domain.journal" })
        {
            Mode(Path.Combine(root, "control", name), 0, 17010, 432);
        }
        WriteProtected(Path.Combine(root, "business.json"), new { connectionString = businessConnection });
        WriteProtected(Path.Combine(root, "reference.json"), new { connectionString = referenceConnection });
        WriteProtected(Path.Combine(root, "host.json"), new
        {
            environment = "Testing",
            root,
            businessConnectionFile = Path.Combine(root, "business.json"),
            referenceConnectionFile = Path.Combine(root, "reference.json"),
        });
        WriteEnrollment(root, domain, incarnation);
    }

    private static void WriteEnrollment(string root, PrivateGenerationDomain domain, Guid incarnation)
    {
        using var rootHandle = PrivateGenerationLinux.OpenDirectory(root);
        using var control = PrivateGenerationLinux.OpenChild(rootHandle, "control", directory: true);
        using var data = PrivateGenerationLinux.OpenChild(rootHandle, "data", directory: true);
        var files = new Dictionary<string, PrivateGenerationNode>();
        var areas = new Dictionary<string, PrivateGenerationNode>();
        foreach (var name in new[] { "domain.lock", "domain.journal" })
        {
            using var handle = PrivateGenerationLinux.OpenChild(control, name, directory: false);
            files.Add(name, Node(PrivateGenerationLinux.Stat(handle)));
        }
        foreach (var name in new[] { "review-candidates", "derivatives" })
        {
            using var handle = PrivateGenerationLinux.OpenChild(data, name, directory: true);
            areas.Add(name, Node(PrivateGenerationLinux.Stat(handle)));
        }
        WriteProtected(Path.Combine(root, "enrollment.json"), new PrivateGenerationEnrollment(domain, incarnation,
            17010, [17020], Node(PrivateGenerationLinux.Stat(rootHandle)),
            Node(PrivateGenerationLinux.Stat(control)), files, areas));
    }

    private static PrivateGenerationNode Node(PrivateGenerationLinux.FileStatus info) =>
        new(info.Inode, info.DeviceMajor, info.DeviceMinor);

    private static void WriteProtected(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, value.GetType(), PrivateGenerationWire.Json));
        Mode(path, 0, 17010, 288);
    }

    private static void Mode(string path, int uid, int gid, int mode)
    {
        using var process = Process.Start(new ProcessStartInfo("chown")
        {
            UseShellExecute = false,
            ArgumentList = { $"{uid}:{gid}", path },
        }) ?? throw new InvalidOperationException("Fixture chown did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private static async Task AssertKernelDeniedAsync(string root, CancellationToken cancellationToken)
    {
        foreach (var uid in new[] { 1654, 17020, 17010 })
        {
            var script = """
                import os,sys,errno
                uid=int(sys.argv[1]); root=sys.argv[2]
                os.setgroups([]); os.setgid(uid); os.setuid(uid)
                checks=[lambda:os.unlink(root+'/control/domain.lock'),
                        lambda:os.rename(root+'/control/domain.lock',root+'/control/replaced'),
                        lambda:os.chmod(root+'/control/domain.lock',0o666)]
                if uid != 17010:
                    checks.append(lambda:open(root+'/data/derivatives/review-'+'1'*32+'-thumb','wb'))
                for action in checks:
                    try: action()
                    except OSError as e:
                        assert e.errno in (errno.EACCES,errno.EPERM,errno.EROFS)
                    else: raise AssertionError('unexpected private capability')
                """;
            using var process = Process.Start(new ProcessStartInfo("python3")
            {
                UseShellExecute = false,
                ArgumentList = { "-c", script, uid.ToString(CultureInfo.InvariantCulture), root },
            }) ?? throw new InvalidOperationException("Fixture peer did not start.");
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
    }

    private static async Task LegacyPrivateReadIsUnavailableAsync(string root, IObjectStore store,
        CancellationToken cancellationToken)
    {
        var key = ObjectKey.ForPromotedDerivative(Guid.NewGuid(), PhotoVariant.Full);
        var legacy = new FileSystemObjectStore(Path.Combine(root, "legacy"));
        using var bytes = new MemoryStream([1, 2, 3]);
        await legacy.PutAsync(key, bytes, 3, "image/webp", cancellationToken);
        await Assert.ThrowsAsync<IOException>(() => store.OpenReadAsync(key, cancellationToken));
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(key, cancellationToken));
        Assert.True(File.Exists(Path.Combine(root, "legacy", key.Value)));
    }

    private static async Task<Guid> SeedUploadAsync(NpgsqlDataSource source, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid();
        var upload = Guid.NewGuid();
        await ExecuteAsync(source, """
            insert into identity.users(id,status,created_at,email_confirmed,phone_number_confirmed,
              two_factor_enabled,lockout_enabled,access_failed_count,security_stamp)
            values($1,'Active',statement_timestamp(),true,false,false,true,0,'synthetic-authority-host');
            insert into media.uploads(id,owner_id,state,client_key,quarantine_key,declared_content_type,size_bytes,
              content_sha256,created_at,abandon_after,version)
            values($2,$1,'Processing','authority-'||$2::text,'quarantine/'||md5($2::text),'image/jpeg',
              2048,sha256('synthetic'::bytea),statement_timestamp(),statement_timestamp()+interval '1 hour',2)
            """, cancellationToken, owner, upload);
        return upload;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, CancellationToken cancellationToken,
        params object[] values)
    {
        for (var index = values.Length; index > 0; index--)
        {
            sql = sql.Replace("$" + index.ToString(CultureInfo.InvariantCulture),
                "@v" + index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        await using var command = source.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++)
        {
            command.Parameters.AddWithValue("v" + (index + 1).ToString(CultureInfo.InvariantCulture), values[index]);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql,
        CancellationToken cancellationToken, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values)
        { command.Parameters.AddWithValue(value); }
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed class AuthorityProcess(Process process) : IAsyncDisposable
    {
        private int _stopped;
        internal Process Process => process;
        internal static async Task<AuthorityProcess> StartAsync(string root, bool expectReady,
            CancellationToken cancellationToken)
        {
            var dll = Path.Combine(RepositoryRoot.Find(), "backend/src/Duora.StorageAuthority/bin/Debug/net10.0/Duora.StorageAuthority.dll");
            var info = new ProcessStartInfo("python3")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-c", "import os,sys; os.setgroups([]); os.setgid(17010); os.setuid(17010); os.execvp('dotnet',['dotnet',sys.argv[1],'--configuration',sys.argv[2]])",
                    dll, Path.Combine(root, "host.json"),
                },
            };
            var child = new AuthorityProcess(System.Diagnostics.Process.Start(info)
                ?? throw new InvalidOperationException("Authority did not start."));
            if (expectReady)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(20));
                var first = await child.Process.StandardOutput.ReadLineAsync(budget.Token);
                if (first != "STORAGE_AUTHORITY_READY")
                {
                    var error = await child.Process.StandardError.ReadToEndAsync(budget.Token);
                    await child.StopAsync(CancellationToken.None);
                    throw new InvalidOperationException("Authority did not admit its fixture: " + error);
                }
                _ = child.Process.StandardOutput.ReadToEndAsync(cancellationToken);
                _ = child.Process.StandardError.ReadToEndAsync(cancellationToken);
            }
            else
            {
                var first = await child.Process.StandardOutput.ReadLineAsync(cancellationToken);
                if (first == "STORAGE_AUTHORITY_READY")
                {
                    await child.StopAsync(CancellationToken.None);
                    Assert.Fail("Unqualified storage configuration was admitted.");
                }
                await child.Process.WaitForExitAsync(cancellationToken);
                Assert.Contains("STORAGE_AUTHORITY_STARTUP_DENIED", await child.Process.StandardError.ReadToEndAsync(cancellationToken),
                    StringComparison.Ordinal);
            }
            return child;
        }

        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            process.Dispose();
        }

        public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
    }
}
