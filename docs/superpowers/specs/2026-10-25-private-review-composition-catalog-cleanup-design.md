# Private review composition, Approved metadata and unavailable cleanup

Continuation of #173/#69 on qualified source ED. The human authorized these three concrete engineering gaps and a bounded B2 read-only inventory for operator69. Original issue acceptance remains separate: none of these components admits HumanReview, uploads, restore or production operation.

## Internal composition and context ownership

AddMediaUploads registers existing private Safety review services through explicit scoped factories. Their internal constructors and actor/capability checks remain unchanged. No route, capability grant, policy enum, production allowlist or logical reconciliation producer is added. The default UnconfiguredObjectStore and disabled upload policy remain.

The context factory is a singleton borrowing the already configured NpgsqlDataSource. Each CreateDbContext/Async returns a new owned DuoraDbContext; cancellation is observed before allocation. Disposing a context must not dispose the source or create a second pool. Scoped Access/Assignments retain the normal scoped context; multi-phase coordinators continue owning their separate short contexts. No scoped instance is captured by the singleton factory.

## Approved metadata catalog

ApprovedMediaBackupCatalog.CaptureAsync(maxAssets,maxBytes,ct) is internal and SELECT-only. It rejects ambient transactions and owns a read-only REPEATABLE READ transaction. One MVCC snapshot covers roots and manifests; no business row lock or storage call occurs. The operation has a15second cancellation/statement budget. Metadata safety limits are4096assets, exactly3variants per selected asset,16MiB per variant/24MiB per asset and a caller byte budget no greater than4096*24MiB. These are refusal bounds for this new private metadata operation, not qualified production export/capacity limits.

Eligible roots require asset+upload Approved, same owner, approved_at present and asset.deleted_at absent. A root beyond the asset limit, malformed selected binding/manifest or byte limit refuses the whole catalog. No partial-success or directory fallback exists. Variants require exactly card/full/thumb, canonical generated keys, no reused key, dimensions derived from PhotoVariants.Fit and positive bounded sizes.

Legacy roots must not have a human review case. Keys derive from the upload quarantine token. Their ExpectedSha256 is explicitly absent because the original SHA is not a derivative SHA; legacy late-PUT immutability and export integrity are not established by this reader.

Human-reviewed roots bind the actual committed/settled promotion to the same upload, asset dimensions/type, policy human-review-v1 and current case/upload versions. The immutable approval/receipt/audit must match that case version, actor and resulting asset. Promotion manifests bind key/dimensions/size; immutable decision variants bind size/hash. Decision variants do not store dimensions and the query must not invent them. The prior promotion and decision hashes must agree.

The returned immutable private metadata omits personal identities. ToString does not disclose object keys. This is neither authenticated catalog CLI nor a backup-format authority: no export, restic/timer change, whole-directory copy, restore import or tombstone bypass is implemented. An eligible metadata root does not certify current byte availability. Safe export needs separately demonstrated writer immutability or trustworthy file/version snapshot plus the new snapshot/restore format and external tombstones.

## Exact SystemUnavailable cleanup

The new collector is separate from Expired/Approved/abandoned enrollment. It consumes only the exact obligation produced by the initial-review availability coordinator: candidate committed+settled, current case/upload/generation/policy binding, Rejected/PHOTO_REVIEW_UNAVAILABLE and its exact immutable System audit ID, NULL actor, action, target and version. Original retention and marker times must agree; cases with human decisions or appeals are excluded. Status or file age alone cannot enroll a candidate.

Reuse the existing60second lease,15second delete budget and bounded retry backoff. Claim locks only the candidate with SKIP LOCKED. Deletion runs outside SQL locks and names only three canonical private generation keys, including an already missing variant. ACK/retry acquires the candidate row then rereads the current clock, token, lease and complete obligation. Cancellation, partial deletion, lost lease or replaced token cannot acknowledge a successful sweep. The existing cleanup worker consumes this persisted obligation only when storage is configured, independently of upload admission; no availability scanner or expiry scheduler is added.

## B2 metadata inventory for a later operator window

The one-shot collector is not a journal adapter. Only v4 authorize-account, exact-bucket metadata and prefix-scoped version listing are implemented. The operator supplies an existing key and exact non-personal bucket/prefix through an anonymous pipe; no credential in arguments/environment/output/new file. The collector disables its own core dumps, has a60second alarm and no proxy, redirect, retry, arbitrary host or write endpoint.

Bounds: at most5requests,3pages of100versions,1MiB per response,16KiB input and8KiB output. Output is fixed names, booleans/UNKNOWN, bounded counts and hashes only. Hidden/malformed optional retention is not converted into disabled protection. Current retainUntil may have been extended: observed coverage never proves retention since upload. A terminated listing is not a consistent journal barrier while writers run. providerQualified and restoreAdmission always remain false.

The source must pass its own composed hosted checks before an operator's hash-pinned wrapper is eligible. Execution additionally requires the actual H3/#230 acceptance, release/resource binding and exclusive operator69 lease. No key/bucket/grant/retention/lifecycle/billing change or object-body read is authorized by this source. Actual resources, independent custody, writer fencing, journal retention and authenticated restore admission remain unproven inputs.

Protocol references: [authorize-account](https://www.backblaze.com/apidocs/b2-authorize-account), [list-buckets](https://www.backblaze.com/apidocs/b2-list-buckets), [list-file-versions](https://www.backblaze.com/apidocs/b2-list-file-versions), consulted2026-10-25. Native API metadata visibility is capability dependent; a filtered field is UNKNOWN.

## Evidence and remaining acceptance

New synthetic tests cover scoped resolution, context disposal/single pool/cancellation; mixed eligible roots and malformed manifests; snapshot deletion interleaving/no business locks; actual SystemUnavailable producer and exact-key GC/proof mismatches/leases/retry/cancellation; and collector response canaries/scope/host/cursor/budgets/uniform errors. Source review corrected nonexistent decision-variant dimension columns, unvalidated remote boolean output and a historical-retention overclaim before any new CI.

All120 original regression blobs and ED qualification remain intact. The complete private composition requires one new coordinated head and its own normal CI; no test/build/format/Docker runs on the Mac and no partial CI per component. No result is inferred from ED3888PASS.

#173 still requires complete authorized HTTP/UI/reviewer workflows, appeal decisions, profile selection/DELETE, retention/restore and coherent accepted policy M7 before issue closure. #69 also needs independent #174/#86 acceptance, real storage/B2/Approved-only backup+restore/mount/verify/capacity evidence and its operational activation conditions. Accepted ADR decisions are not reopened; actual missing source and real-world evidence are not relabelled as approval requests. Neither issue closes on this partial source.
