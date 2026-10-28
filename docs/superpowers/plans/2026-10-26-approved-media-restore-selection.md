# Approved media restore selection — implementation plan

**Goal:** Join the existing read-only Approved catalog to a private authenticated backup manifest and complete deletion ledger, producing only an exact private selection or refusal.

**Spec:** ../specs/2026-10-26-approved-media-restore-selection-design.md

**Execution:** Author implements in this isolated worktree; existing independent agents review design, tests and final source. Human authorized autonomous implementation and agent review. M2 coordinates any new PR vehicle and exclusively merges. No change to qualified263 branch.

## Global constraints

- Same .NET10/EF/PostgreSQL dependency graph; no new dependency, schema, grant, public API or runtime flag.
- No SDK/build/test/Docker on Mac. Normal PR CI is the executable verification; no new lane/manualdispatch.
- Source review and operational admission stay distinct. No host/provider/realdata operation.
- Maximum4096assets,24MiB/asset,16MiB/variant, three canonical variants; exceptions/cancellation never return partial output.
- Preserve legacy behavior and catalog selection. Eligible legacy NULL hashes refuse restore selection until a separate consistency proof exists.

## Task1 — contracts and regression tests first

Files:
- Modify backend/src/Duora.Infrastructure/Media/ApprovedMediaBackupCatalog.cs: add private projection properties PhotoId/PromotionGeneration; do not change SQL selection predicates.
- Create backend/src/Duora.Infrastructure/Media/ApprovedMediaRestoreSelection.cs: private V1 manifest/expectation/authentication/result contracts and selector initially refusing.
- Create backend/tests/Duora.IntegrationTests/Media/ApprovedMediaRestoreSelectionTests.cs: pure selector cases, no database replacement.
- Create backend/tests/Duora.IntegrationTests/Safety/ApprovedMediaRestoreCatalogTests.cs: existing real PostgreSQL fixture and read-only catalog→selection path.

Interfaces to pin:

```csharp
internal sealed record ApprovedMediaRestoreManifest(int Schema, Guid EnvironmentId, Guid SnapshotId,
    string SourceDigest, DateTimeOffset CapturedAt, string DatabaseSnapshot,
    IReadOnlyList<ApprovedMediaCatalogAsset> Assets, long TotalBytes, string KeyId, byte[] Signature);
internal sealed record ApprovedMediaRestoreExpectation(Guid EnvironmentId, Guid SnapshotId,
    string SourceDigest, int MaximumAssets, long MaximumBytes);
internal interface IApprovedMediaRestoreManifestAuthenticator
{
    bool Verify(ApprovedMediaRestoreManifest manifest);
}
```

- [ ] Write tests against initially refused contract: positive exact3objects; tombstoned PhotoId removed across versions/generations while another remains; nonApproved catalog absence never restores; eligible legacy refused.
- [ ] Pin refused mismatches: source/snapshot/environment/signature/format; duplicate PhotoId/AssetId/key, missing kind, hash/size/dimensions/version/gen, limits/overflow; full ledger incomplete/prepared/wrongscope refuses through existing validator.
- [ ] Pin cancellation, authenticator/entry enumeration exceptions, mutation of original manifest during authentication and no partial selection.
- [ ] RealPG test: initial human-approved asset and legacy resolve actual uploadIDs and generations; catalog uses one read-only snapshot; passing promoted catalog to selector preserves exact approved identity. No SQLite/InMemory.
- [ ] Independent design/test review before remote source mutation. With M2 vehicle available, one test-first normal CI provides genuine RED, followed by implementation. If coordinator requires offline preparation while CI unavailable, preserve tests-first commit/bytes and explicitly record RED unobserved; never fabricate a run.

## Task2 — minimal selector and catalog binding

- [ ] SQL projection adds u.id as PhotoId; return PhotoId and p.generation in private catalog record; IDs nonempty and logical PhotoIds unique. Do not change variants/review authorization/transaction behavior.
- [ ] Copy manifest/catalog into bounded private read-only lists; copy signature and independent ledger checkpoint/barrier arrays before invoking callbacks. Validate closed fields, hashes, counts/totals/keys/dimensions and duplicates, including entries later omitted.
- [ ] Authenticate the frozen manifest against independent snapshot/environment/source expectation. Reject absent/false/throwing verifier.
- [ ] Invoke DeletionLedgerValidation.Validate on the independent expectation/barrier/full entries; observe cancellation before/after and while enumeration. Reject Refused; never accept externally fabricated Verified.
- [ ] Filter every tombstoned logicalPhotoId and every asset absent from currentlyApproved catalog. Matching logicalPhotoId requires exact AssetId/Version/Generation and threevariant metadata+persistedhash equality. Eligible legacy without persisted hashes refuses the whole selection.
- [ ] Return private read-only selection with snapshot/environment/restoreEpoch and exact asset/variant list/total; ToString redacts keys/IDs. No copy/file/DBwrite/restore-admission effects.
- [ ] Independent final source review and mutation coverage check. Apply only proven findings without weakening gates.

## Task3 — qualification and queue delivery

- [ ] Document tests, synthetic auth boundary, explicit legacy refusal and all remaining operational/engineering gaps; do not claim issues69/173 resolved.
- [ ] git diff --check and exact tree/blob preservation; no local runtime claim. Normal CI ninechecks and isolation gates qualify final head/tree/base. Reuse receipts when bytes/base unchanged.
- [ ] Deliver exact head/tree/base, changedpaths/hashes/reviews/CI to M2 and ROOT. Merge only under M2 serial integration and69window after actual prerequisite publication; no concurrent branch updates after handoff.

## Review focus

1. Authentication must bind the frozen complete manifest, not a caller-owned mutable object or a synthetic Verified ledger.
2. PhotoId tombstone must dominate generation/version changes; matching another generation must never resurrect the same logical photo.
3. Missing/corrupt metadata and overflow must refuse before releasing any plan, even for entries later filtered.
4. Legacy export hash must not be misrepresented as persisted hash/version immutability; keep eligible legacy refused.
5. External adapter/host/provider unavailability must not become permissive fallback; this selector alone never admits restore.
