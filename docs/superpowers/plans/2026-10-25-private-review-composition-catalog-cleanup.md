# Private review composition, approved catalog and unavailable cleanup

> **For agentic workers:** Use superpowers:executing-plans inline. No implementation delegation or local SDK/test/build/Docker. The human authorized these existing-contract gaps; no generic approval gate is reopened.

**Goal:** Complete the internal composition, read-only Approved catalog and exact SystemUnavailable cleanup obligations without admitting uploads, changing backup writers or claiming provider/restore readiness.

**Architecture:** Keep the qualified ED reference immutable. Register private review services through Media composition using contexts owned by each operation and the existing shared NpgsqlDataSource/pool. Catalog selection uses one bounded read-only repeatable-read snapshot and validates every selected manifest. A separate unavailable collector authenticates the exact committed System fact and generation before storage deletion; expiration remains a different enrollment authority.

**Tech Stack:** Existing .NET/PostgreSQL/EF/Npgsql only; SDK Docker402 unchanged. Execution and gates belong to a new coordinated hosted source, never the Mac.

**Spec:** [Private composition and catalog design](../specs/2026-10-25-private-review-composition-catalog-cleanup-design.md); ADR0035; docs/api/media-review.md; existing candidate-unavailable and expired-candidate-cleanup designs. The broader private M6d design remains source in feat/173-approved-media-backup-20261024; this stage does not implement its export/restore/CLI producer.

## Global Constraints

- Preserve all120 original test blobs and ED head/tree/qualification. No changes to CF, MFA, collectors, mainH, host, grants, production SQL, release executor or backup/timers.
- Policy remains disabled by default; TechnicalOnly remains forbidden in Production/Staging. No new HumanReview allowlist, route, CLI, real grant or hosted producer.
- Reuse the existing data source and pool budget; do not create a second pool per review operation. Dispose owned contexts without disposing the borrowed data source.
- Catalog is private metadata only. No storage I/O, DML, business locks, partial-success snapshot, or claim of derivative immutability/export/restore qualification. Missing or malformed selected manifests refuse the whole catalog.
- Unavailable GC consumes only the exact audited initial-review SystemUnavailable obligation; no enrollment by age/status alone, no human/appeal/expired/approved obligation reuse. Delete only its three canonical candidate keys, with existing lease60s/delete15s/retry bounds.
- New tests are defined before implementation; no assertion relaxation. The human specifically requires a complete composed source and one necessary normal coordinated qualification, without partial source/CI per piece. Record only results actually observed; static preparation never claims RED/GREEN. Existing ED CI is not repeated or borrowed for the new tree.
- global.json402 patch remains outside the qualified tree until the next coordinated head. No automatic monitor or duplicate/manual CI.

## Review Focus

1. Private DI must resolve all context-owning services without scope capture or a second connection pool. Tests hold the only pooled connection, cancel an owned-context open, then prove source reuse after context disposal.
2. A mixed catalog must exclude non-Approved/Deleted and uncommitted copies, while refusing incomplete, duplicate, foreign or unbound manifests among selected Approved assets. Legacy hashes are absent, never confused with the original SHA.
3. Concurrent changes between catalog reads must not mix snapshots, and no storage may run under SQL locks. Read-only/repeatable-read and bounded overflow tests pin this contract.
4. Unavailable cleanup must reject foreign audit action/actor/target/version/generation and preserve history, original/final bytes and a competing generation. Real PostgreSQL/FS tests exercise both producer and consumer.
5. Lease recovery, token replacement, time moving while a row lock is held, delete failure and cancellation must preserve durable retry obligations. No historical audit is rewritten by cleanup.

## Task 1 — executable criteria map and private DI

Create private registration/factory under Infrastructure/Safety and call registration from AddMediaUploads only. Register existing Access/Assignments/Evidence/Rejections/Approvals/Appeals/AppealAssignments/AppealEvidence/Expirations/CandidateAvailability plus new catalog; explicit factories are required because constructors are internal. IDbContextFactory creates independent DuoraDbContext instances using the existing NpgsqlDataSource, never disposing it.

- [x] Add integration tests resolving the existing types through actual Media+Infrastructure registration; independent/disposed contexts, single pool, scoped lifetime and absence of a new hosted service/route/admission.
- [x] Implement registration and owned factory; keep production configuration and authorization logic unchanged.
- [ ] Record hosted results for this source separately from ED.

## Task 2 — bounded private Approved catalog

Create ApprovedMediaBackupCatalog and private result types. CaptureAsync requires caller-supplied positive asset/byte budgets under fixed metadata safety caps, rejects ambient transactions, owns a read-only REPEATABLE READ context/snapshot and returns a complete immutable catalog or refuses. Selected rows require asset+upload Approved, same owner, approved_at present and deleted_at absent. Exactly thumb/card/full, canonical keys, positive dimensions, existing16MiB/variant24MiB/asset envelopes, no reused keys. Human promoted assets additionally bind their committed/settled generation, case/upload versions, policy, immutable approval and variant hashes; legacy uses upload quarantine-derived keys and explicitly has no prior derivative hash.

- [x] Add PostgreSQL tests to the existing approval fixture via a new partial file: mixed selection, no-promotion legacy, current promoted approval, missing/foreign/duplicate/hash mismatch, byte/asset budget and cancellation/snapshot behavior. Never edit original120 cases.
- [x] Implement SELECT-only snapshot and manifest validation. Keep export, restic, restore and catalog authentication CLI absent until their real prerequisites are satisfied.
- [x] Register private service only; no public metadata/logging surface.

## Task 3 — exact unavailable obligation collector

Create UnavailableCandidateCleanupDispatcher with the existing collector lease/delete/retry mechanics and a distinct enrollment predicate. Require matching candidate/case/upload/policy, committed+settled binding, Rejected/PHOTO_REVIEW_UNAVAILABLE, uploadVersion=candidateVersion+2, original retention and cleanup marker, exact System audit ID/action/NULL actor/target/version. Recheck token/lease/clock and exact obligation after obtaining candidate lock for ACK/retry. No SQL lock is held during storage.

- [x] Add new PostgreSQL/FS cases to existing approval fixture: actual availability producer after lost/corrupt variant, positive exact-key cleanup, negative forged proofs/context, simultaneous claims, expired token/ACK/retry after waits and interrupted deletion.
- [x] Implement dispatcher and register it with enabled only when storage is configured. Connect it to the existing private cleanup consumer without changing any other enrollment predicate.
- [ ] Prove existing expiration collector still refuses unavailable obligations and vice versa.

## Task 4 — bounded read-only provider inventory

- [x] Implement scripts/deploy/ledger_provider_inventory.py: pipe-only credential input, fixed v4 read-only API operations, exact bucket/prefix,3x100version pages,1MiB response/8KiB output,60second alarm, no redirects/proxy/retry or object bodies.
- [x] Add8 offline Python test methods for sanitized output, unknown metadata, scope/host refusal, pagination, transport allowlist and CLI secrecy. No local execution or provider claim.
- [x] Normalize optional remote fields; observed coverage is current metadata, never proof of retention since upload. Always refuse provider/restore admission.
- [ ] Qualify composed source in its one coordinated hosted run, then bind collection source/input/authority by hash for operator69 afterH3/#230.

## Task 5 — review, source handoff and operational acceptance

- [ ] Verify only intended source/test/docs change, preserve120 blobs and ED refs, no policy/privilege/resource expansion.
- [ ] Obtain final independent review of the composed private candidate. Prepare Git tree/bundle/blob manifest before any coordinated publication.
- [ ] Qualify new source through its own normal CI/gates, no rerun/dispatch/cancel. If coordination is pending, hand off the exact candidate and test-stage status without claiming execution.
- [ ] Produce a hashed checklist mapping every #69/#173 criterion to evidence or a concrete owner/input. Include provider resource/prefix/retention/version listing, custody independent of restore, writer fencing/barrier/pin, Approved-only export/restore, mount/verify/capacity/reviewer coverage and operator69 lease after H3/#230.
- [ ] Close an issue only when every criterion is demonstrated. Code catalogs/private DI or synthetic provider doubles do not meet operational acceptance.

## Execution rulings

- Existing checkout work/duora-acl226 is dedicated to this task; private branch isolation preserves the ED branch/ref and avoids a new workspace's possible setup scripts. No dependency setup or baseline suite is rerun: ED's immutable hosted receipt is the baseline.
- No new provider resource/auth/credential/grant is inferred from historical B2 backup evidence. ProviderUnset remains a concrete external dependency for deletion/restore activation.

## Preparation outcome

All three source components and their new tests are implemented privately; no partial publication or CI was started. Independent static reviews of DI/catalog, GC/B2 and test preconditions completed with no remainingP1/P2 after correcting nonexistent decision-manifest columns, remote boolean normalization and historical-retention wording. A concurrent variant deletion plus a direct transaction_read_only probe strengthen the snapshot test. New source has38C#cases and8Python methods by static count only; none is reported as executed. The complete composed candidate now goes toM2; its own normal hosted qualification and the real operator69 input remain external dependencies. Neither issue is complete.
