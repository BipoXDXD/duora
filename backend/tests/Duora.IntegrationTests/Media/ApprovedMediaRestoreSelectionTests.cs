using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duora.Core.Media;
using Duora.Infrastructure.Media;

namespace Duora.IntegrationTests.Media;

public sealed class ApprovedMediaRestoreSelectionTests
{
    private static readonly Guid EnvironmentId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid WriterEpoch = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid RestoreEpoch = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid SnapshotId = Guid.Parse("10000000-0000-4000-8000-000000000004");
    private static readonly Guid PhotoA = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid PhotoB = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid AssetA = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid AssetB = Guid.Parse("30000000-0000-4000-8000-000000000002");
    private static readonly Guid GenerationA = Guid.Parse("40000000-0000-4000-8000-000000000001");
    private static readonly Guid GenerationB = Guid.Parse("40000000-0000-4000-8000-000000000002");
    private static readonly byte[] TestKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string SourceDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string VariantHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AnAuthenticatedManifestSelectsOnlyTheExactThreeVariantsPerApprovedAsset()
    {
        var fixture = Create();

        var selected = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(Select(fixture));

        Assert.Equal(EnvironmentId, selected.EnvironmentId);
        Assert.Equal(SnapshotId, selected.SnapshotId);
        Assert.Equal(RestoreEpoch, selected.RestoreEpoch);
        Assert.Equal(new[] { AssetA, AssetB }, selected.Assets.Select(a => a.AssetId));
        Assert.Equal(18, selected.TotalBytes);
        Assert.All(selected.Assets, a => Assert.Equal(3, a.Variants.Count));
        Assert.Equal("derivatives/review-40000000000040008000000000000001-thumb", selected.Assets[0].Variants.Single(v => v.Kind == PhotoVariant.Thumb).Key.Value);
        Assert.DoesNotContain(PhotoA.ToString("D"), selected.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("derivatives/", selected.ToString(), StringComparison.Ordinal);
        Assert.Equal("ApprovedMediaRestoreManifest", fixture.Manifest.ToString());
    }

    [Fact]
    public void ALaterLogicalTombstoneExcludesAnEarlierVersionAndGenerationWithoutTouchingAnotherPhoto()
    {
        var fixture = Create(deleted: true);

        var selected = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(Select(fixture));

        Assert.Equal(PhotoB, Assert.Single(selected.Assets).PhotoId);
        Assert.Equal(AssetB, Assert.Single(selected.Assets).AssetId);
        Assert.Equal(9, selected.TotalBytes);
        Assert.Equal(2, fixture.Manifest.Assets.Count);
        Assert.Equal(18, fixture.Catalog.TotalBytes);
    }

    [Fact]
    public void AManifestEntryAbsentFromTheCurrentlyApprovedCatalogCannotBeRestored()
    {
        var fixture = Create();
        fixture = fixture with { Catalog = fixture.Catalog with { Assets = [fixture.Catalog.Assets[1]], TotalBytes = 9 } };

        var selected = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(Select(fixture));

        Assert.Equal(PhotoB, Assert.Single(selected.Assets).PhotoId);
        Assert.Equal(9, selected.TotalBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEligibleLegacyAssetNeedsASeparateImmutableSnapshotProofButATombstonedOneNeverReturns(bool deleted)
    {
        var fixture = Create(deleted);
        var legacyKey = ObjectKey.Parse("quarantine/0123456789abcdef0123456789abcdef");
        var variants = fixture.Catalog.Assets[0].Variants.Select(v => v with { Key = ObjectKey.ForDerivative(legacyKey, v.Kind), ExpectedSha256 = null }).ToArray();
        var legacy = fixture.Catalog.Assets[0] with { PromotionGeneration = null, Variants = variants };
        var stored = legacy with { Variants = variants.Select(v => v with { ExpectedSha256 = VariantHash }).ToArray() };
        fixture = fixture with
        {
            Catalog = fixture.Catalog with { Assets = [legacy, fixture.Catalog.Assets[1]] },
            Manifest = Sign(fixture.Manifest with { Assets = [stored, fixture.Manifest.Assets[1]] }),
        };

        if (deleted)
        {
            var selected = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(Select(fixture));
            Assert.Equal(PhotoB, Assert.Single(selected.Assets).PhotoId);
        }
        else
        {
            Refused(fixture);
        }
    }

    [Theory]
    [InlineData("photo")]
    [InlineData("asset")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("key")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("dimensions")]
    [InlineData("missing-variant")]
    [InlineData("duplicate-kind")]
    [InlineData("duplicate-photo")]
    [InlineData("duplicate-asset")]
    [InlineData("duplicate-key")]
    public void AnAuthenticatedButInconsistentManifestRefusesTheWholeSelection(string mismatch)
    {
        var fixture = Create();
        var assets = fixture.Manifest.Assets.ToArray();
        var asset = assets[0];
        var variants = asset.Variants.ToArray();
        switch (mismatch)
        {
            case "photo":
                asset = asset with { PhotoId = Guid.Empty };
                break;
            case "asset":
                asset = asset with { AssetId = Guid.NewGuid() };
                break;
            case "version":
                asset = asset with { Version = 2 };
                break;
            case "generation":
                asset = asset with { PromotionGeneration = GenerationB };
                break;
            case "key":
                variants[0] = variants[0] with { Key = ObjectKey.NewQuarantine() };
                break;
            case "hash":
                variants[0] = variants[0] with { ExpectedSha256 = new string('b', 64) };
                break;
            case "size":
                variants[0] = variants[0] with { SizeBytes = 4 };
                break;
            case "dimensions":
                variants[0] = variants[0] with { Width = 63 };
                break;
            case "missing-variant":
                variants = variants[..2];
                break;
            case "duplicate-kind":
                variants[0] = variants[1];
                break;
            case "duplicate-photo":
                asset = asset with { PhotoId = PhotoB };
                break;
            case "duplicate-asset":
                asset = asset with { AssetId = AssetB };
                break;
            default:
                variants[0] = assets[1].Variants[0];
                break;
        }
        assets[0] = asset with { Variants = variants };
        fixture = fixture with { Manifest = Sign(fixture.Manifest with { Assets = assets }) };

        Refused(fixture);
    }

    [Theory]
    [InlineData("tombstoned")]
    [InlineData("absent")]
    public void AMalformedAuthenticatedEntryMustBeValidatedEvenWhenItWouldBeFiltered(string excluded)
    {
        var fixture = Create(deleted: excluded == "tombstoned");
        var assets = fixture.Manifest.Assets.ToArray();
        var variants = assets[0].Variants.ToArray();
        variants[0] = variants[0] with { ExpectedSha256 = null };
        assets[0] = assets[0] with { Variants = variants };
        fixture = fixture with { Manifest = Sign(fixture.Manifest with { Assets = assets }) };
        if (excluded == "absent")
        {
            fixture = fixture with { Catalog = fixture.Catalog with { Assets = [fixture.Catalog.Assets[1]], TotalBytes = 9 } };
        }

        Refused(fixture);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("environment")]
    [InlineData("snapshot")]
    [InlineData("source")]
    [InlineData("signature")]
    [InlineData("total")]
    [InlineData("no-key")]
    public void TheManifestCannotChooseItsOwnIdentityOrClaimAuthentication(string mismatch)
    {
        var fixture = Create();
        var manifest = mismatch switch
        {
            "schema" => fixture.Manifest with { Schema = 2 },
            "environment" => fixture.Manifest with { EnvironmentId = Guid.NewGuid() },
            "snapshot" => fixture.Manifest with { SnapshotId = Guid.NewGuid() },
            "source" => fixture.Manifest with { SourceDigest = "sha256:" + new string('b', 64) },
            "total" => fixture.Manifest with { TotalBytes = 1 },
            "no-key" => fixture.Manifest with { KeyId = "" },
            _ => fixture.Manifest,
        };
        manifest = Sign(manifest);
        if (mismatch == "signature")
        {
            manifest = manifest with { Signature = new byte[32] };
        }
        Refused(fixture with { Manifest = manifest });
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("ledger")]
    [InlineData("throw")]
    public void MissingOrThrowingAuthenticationNeverReleasesAPlan(string failure)
    {
        var fixture = Create();
        IApprovedMediaRestoreManifestAuthenticator? manifest = failure == "manifest" ? null : new Authenticator(failure == "throw");
        IDeletionLedgerBarrierAuthenticator? ledger = failure == "ledger" ? null : new Authenticator();

        Assert.IsType<ApprovedMediaRestoreSelectionResult.Refused>(Select(fixture, manifest, ledger));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("prepared")]
    [InlineData("wrong-environment")]
    [InlineData("unsigned")]
    public void TheSelectorValidatesTheCompleteLedgerInsteadOfTrustingAPrecomputedDeletedSet(string failure)
    {
        var fixture = Create(deleted: true);
        fixture = failure switch
        {
            "truncated" => fixture with { Entries = fixture.Entries.Take(2).ToArray() },
            "prepared" => fixture with { Entries = fixture.Entries.Take(1).ToArray(), Barrier = Sign(fixture.Barrier with { MaximumSequence = 1, ChainRoot = fixture.Entries[0].Hash }) },
            "wrong-environment" => fixture with { LedgerExpected = fixture.LedgerExpected with { EnvironmentId = Guid.NewGuid() } },
            _ => fixture with { Barrier = fixture.Barrier with { Signature = new byte[32] } },
        };

        Refused(fixture);
    }

    [Fact]
    public void AnAuthenticatedLedgerKeyIdentifierMustRespectTheSelectorMetadataBudget()
    {
        var fixture = Create();
        fixture = fixture with { Barrier = Sign(fixture.Barrier with { KeyId = new string('a', 129) }) };

        Refused(fixture);
    }

    [Theory]
    [InlineData("assets")]
    [InlineData("bytes")]
    [InlineData("unbounded-assets")]
    [InlineData("unbounded-ledger")]
    [InlineData("overflow")]
    public void LimitsAndOverflowRefuseTheWholeSelection(string limit)
    {
        var fixture = Create();
        fixture = limit switch
        {
            "assets" => fixture with { Expected = fixture.Expected with { MaximumAssets = 1 } },
            "bytes" => fixture with { Expected = fixture.Expected with { MaximumBytes = 17 } },
            "unbounded-assets" => fixture with { Expected = fixture.Expected with { MaximumAssets = int.MaxValue } },
            "unbounded-ledger" => fixture with { LedgerExpected = fixture.LedgerExpected with { MaximumEntries = int.MaxValue } },
            _ => fixture with { Manifest = Sign(fixture.Manifest with { TotalBytes = long.MaxValue }) },
        };

        Refused(fixture);
    }

    [Fact]
    public void OriginalInputsCannotChangeTheAuthenticatedSelectionDuringVerification()
    {
        var fixture = Create();
        var authenticator = new Authenticator(onManifest: () =>
        {
            ((ApprovedMediaCatalogAsset[])fixture.Manifest.Assets)[0] = fixture.Manifest.Assets[1];
            ((ApprovedMediaCatalogAsset[])fixture.Catalog.Assets)[0] = fixture.Catalog.Assets[1];
            fixture.LedgerExpected.MinimumChainRoot[0] = 255;
            fixture.Barrier.ChainRoot[0] = 255;
        });

        var selected = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(Select(fixture, authenticator, new Authenticator()));

        Assert.Equal(new[] { AssetA, AssetB }, selected.Assets.Select(a => a.AssetId));
        Assert.Equal(18, selected.TotalBytes);
    }

    [Fact]
    public void CancellationDuringLedgerAuthenticationCannotBeSwallowedAsAUsablePlan()
    {
        var fixture = Create();
        using var cancellation = new CancellationTokenSource();
        var ledger = new Authenticator(onLedger: cancellation.Cancel);

        Assert.ThrowsAny<OperationCanceledException>(() => Select(fixture, new Authenticator(), ledger, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringFailedManifestAuthenticationMustPropagate(bool throws)
    {
        var fixture = Create();
        using var cancellation = new CancellationTokenSource();
        if (!throws)
        {
            fixture = fixture with { Manifest = fixture.Manifest with { Signature = new byte[32] } };
        }
        var authenticator = new Authenticator(onManifest: () =>
        {
            cancellation.Cancel();
            if (throws)
            {
                throw new IOException("synthetic authentication interrupted");
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => Select(fixture, authenticator, new Authenticator(), cancellation.Token));
    }

    [Fact]
    public void AThrowingLedgerEnumeratorAfterAValidPrefixCannotReleaseAPartialSelection()
    {
        var fixture = Create(deleted: true);
        Assert.IsType<ApprovedMediaRestoreSelectionResult.Refused>(ApprovedMediaRestoreSelection.Select(
            fixture.Catalog, fixture.Manifest, fixture.Expected, fixture.LedgerExpected, fixture.Barrier,
            ThrowingEntries(fixture.Entries), new Authenticator(), new Authenticator(), Ct));
    }

    private static IEnumerable<DeletionLedgerEntry> ThrowingEntries(IReadOnlyList<DeletionLedgerEntry> entries)
    {
        yield return entries[0];
        throw new IOException("synthetic unavailable journal");
    }

    private static void Refused(Fixture fixture) => Assert.IsType<ApprovedMediaRestoreSelectionResult.Refused>(Select(fixture));

    internal static ApprovedMediaRestoreSelectionResult Select(Fixture fixture) => Select(fixture, new Authenticator(), new Authenticator());

    internal static Fixture FromCatalog(ApprovedMediaCatalog catalog, Guid? deletedPhotoId = null)
    {
        var fixture = Create(deletedPhotoId is not null, deletedPhotoId);
        return fixture with
        {
            Catalog = catalog,
            Manifest = Sign(fixture.Manifest with
            {
                Assets = catalog.Assets.ToArray(),
                TotalBytes = catalog.TotalBytes,
                DatabaseSnapshot = catalog.Snapshot,
            }),
        };
    }

    private static ApprovedMediaRestoreSelectionResult Select(Fixture fixture,
        IApprovedMediaRestoreManifestAuthenticator? manifest, IDeletionLedgerBarrierAuthenticator? ledger,
        CancellationToken? cancellation = null) => ApprovedMediaRestoreSelection.Select(fixture.Catalog,
        fixture.Manifest, fixture.Expected, fixture.LedgerExpected, fixture.Barrier, fixture.Entries,
        manifest, ledger, cancellation ?? Ct);

    private static Fixture Create(bool deleted = false, Guid? deletedPhotoId = null)
    {
        var assets = new[] { Asset(PhotoA, AssetA, GenerationA), Asset(PhotoB, AssetB, GenerationB) };
        var catalog = new ApprovedMediaCatalog(CapturedAt, "3:4:", assets, 18);
        var manifest = Sign(new ApprovedMediaRestoreManifest(1, EnvironmentId, SnapshotId, SourceDigest,
            CapturedAt, "1:2:", assets.ToArray(), 18, "synthetic-test-key", []));
        var entries = new List<DeletionLedgerEntry>();
        var previous = new byte[32];
        if (deleted)
        {
            var operation = Guid.Parse("50000000-0000-4000-8000-000000000001");
            var deletedPhoto = deletedPhotoId ?? PhotoA;
            foreach (var kind in new[] { DeletionLedgerEntryKind.Prepared, DeletionLedgerEntryKind.SqlCommitted, DeletionLedgerEntryKind.Committed })
            {
                var entry = new DeletionLedgerEntry(entries.Count + 1, EnvironmentId, WriterEpoch, 7,
                    operation, deletedPhoto, SHA256.HashData(Encoding.ASCII.GetBytes("synthetic-delete")), kind, previous, []);
                var text = string.Join('\n', new[]
                {
                    "DUORA-DELETE-LEDGER-V1", entry.Sequence.ToString(CultureInfo.InvariantCulture),
                    EnvironmentId.ToString("D"), WriterEpoch.ToString("D"), "7", operation.ToString("D"),
                    deletedPhoto.ToString("D"), Convert.ToHexString(entry.IntentSha256),
                    ((int)kind).ToString(CultureInfo.InvariantCulture), Convert.ToHexString(previous),
                }) + "\n";
                previous = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                entries.Add(entry with { Hash = previous });
            }
        }
        var barrier = Sign(new DeletionLedgerBarrier(EnvironmentId, WriterEpoch, RestoreEpoch, 7,
            entries.Count, previous, "synthetic-test-key", []));
        return new(catalog, manifest, new(EnvironmentId, SnapshotId, SourceDigest, 10, 1_000_000),
            new(EnvironmentId, WriterEpoch, RestoreEpoch, 7, 0, new byte[32], 100), barrier, entries);
    }

    private static ApprovedMediaCatalogAsset Asset(Guid photo, Guid asset, Guid generation) => new(asset, 1,
        new[] { PhotoVariant.Thumb, PhotoVariant.Card, PhotoVariant.Full }.Select(kind =>
            new ApprovedMediaCatalogVariant(kind, ObjectKey.ForPromotedDerivative(generation, kind), 64, 32, 3, VariantHash)).ToArray())
    {
        PhotoId = photo,
        PromotionGeneration = generation,
    };

    private static ApprovedMediaRestoreManifest Sign(ApprovedMediaRestoreManifest manifest) => manifest with
    {
        Signature = HMACSHA256.HashData(TestKey, JsonSerializer.SerializeToUtf8Bytes(manifest with { Signature = [] })),
    };

    private static DeletionLedgerBarrier Sign(DeletionLedgerBarrier barrier) => barrier with
    {
        Signature = HMACSHA256.HashData(TestKey, JsonSerializer.SerializeToUtf8Bytes(barrier with { Signature = [] })),
    };

    private sealed class Authenticator(bool throws = false, Action? onManifest = null, Action? onLedger = null)
        : IApprovedMediaRestoreManifestAuthenticator, IDeletionLedgerBarrierAuthenticator
    {
        public bool Verify(ApprovedMediaRestoreManifest manifest)
        {
            if (throws)
            {
                throw new IOException("synthetic unavailable authenticator");
            }
            var valid = CryptographicOperations.FixedTimeEquals(Sign(manifest).Signature, manifest.Signature);
            onManifest?.Invoke();
            return valid;
        }

        public bool Verify(DeletionLedgerBarrier barrier)
        {
            var valid = CryptographicOperations.FixedTimeEquals(Sign(barrier).Signature, barrier.Signature);
            onLedger?.Invoke();
            return valid;
        }
    }

    internal sealed record Fixture(ApprovedMediaCatalog Catalog, ApprovedMediaRestoreManifest Manifest,
        ApprovedMediaRestoreExpectation Expected, DeletionLedgerExpectation LedgerExpected,
        DeletionLedgerBarrier Barrier, IReadOnlyList<DeletionLedgerEntry> Entries);
}
