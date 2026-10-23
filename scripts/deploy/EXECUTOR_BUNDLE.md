# Candidate executor installation contract — version 1

This is a reviewable contract for the approved database-deployment correction. The installer and guard are implemented; qualification is still required.
A bundle is usable only after exact-source CI, native artifacts, independent review and hashes. This document is not a production handoff,
a production handoff or permission to invoke an actuator. The coordinating chat must release a window after
the implementation, remote proofs and independent review. No new OpenClaw action or second installer is included.

## Immutable source and bundle

The final installation receipt must identify an exact 40-character reviewed source commit, its successful
final-head/base Actions run, the exact downloadable bundle and its SHA-256. Fetch source from that commit;
never resolve a moving branch or trust a comment as executable instructions. A PR candidate can be inspected
before merge, but its partial files are not an installable bundle.

The candidate bundle has this closed inventory:

| Bundle member | Purpose |
| --- | --- |
| `duora_deploy.py` | Existing executor, adapted to preserve qualified database/backup runtime |
| `reconcile_payload.py` | Bounded binary capture and authenticated stopped-image SQL extraction |
| `runtime_guard.py` | Read-only runtime identity, contract and drift checks |
| `install_executor.py` | The single reviewed static installer; no deployment entry point |
| `bundle.json` | Versioned closed inventory, hashes, compatibility and evidence |

`bundle.json` must contain a schema version, source commit, exact relative filenames and SHA-256/modes for
every code member, guard version, qualified Docker/API/Compose versions, legacy-v1 profile, external-v2
profile and exact qualification run/artifact identities. The manifest does not hash itself; the separately
reviewed bundle SHA-256 covers the manifest and every member. Reject extra, missing, duplicate, linked,
traversing or special members and inconsistent commit/hash/mode/qualification. Use only Python's standard
library and the already installed Docker/Compose tools; installation must not install dependencies.

The legacy profile binds the current #228 PostgreSQL native digest, embedded SQL hash and schema ceiling.
It does not accept arbitrary v1 releases. V2 requires external SQL from the authenticated immutable migrator;
missing files never select v1. Rollback keeps the safe executor and cumulative schema/ACL, without Down,
database restore or reapplying weaker legacy SQL.
After current v2, a new v1 release is refused even with an unchanged schema. Only its exact recorded eligible
previous target can be served as application rollback, executing neither legacy SQL nor migrations.
The minimum contract is monotonic, independent of which application is currently selected. Before the first
v2 worker stop/migrations/SQL effects, the executor durably seals `/var/lib/duora/v2-contract-floor.json` (0600).
That floor is not written by preflight or a refused runtime/payload check, is never lowered by an application
rollback, and survives a failed first v2 attempt. Successful recorded v2 history also establishes the minimum.
If the first v2 attempt fails while the qualified current application is still v1, that exact recorded current
target can recover app-only without SQL/migrations when the observed schema is within its qualified range.
An unknown or advanced incompatible partial schema refuses recovery; a new v1 release remains forbidden.
The actual v2 preflight pulls the authenticated migrator digest before extraction, after the runtime/schema
guards and before staging or any worker stop; OCI ingress is not assumed to populate its GHCR RepoDigest.

## Candidate host footprint

All source directories, staging, preimages and receipts are root-owned and inaccessible for unprivileged
writes. Version directories and private state use mode 0700; metadata/sidecars use 0600 and the executor
entry point 0755. Existing unrelated paths and their permissions remain untouched.

| Allowlisted destination | Allowed operation |
| --- | --- |
| `/opt/duora-executor/releases/<source_commit>/` | Stage and verify only the closed bundle above; no overwrite of a divergent existing version |
| `/opt/duora-executor/preimages/<installation_id>/` | Preserve only the previous executor entry point and installer-owned metadata needed for recovery |
| `/usr/local/sbin/duora-deploy` | Atomic final switch to the verified versioned executor; preserve the original file/symlink first |
| `/var/lib/duora/executor-runtime-baseline.json` | Atomic, explicitly reviewed runtime enrollment; never enroll from a deploy or read-only preflight |
| `/var/lib/duora/executor-install-receipt.json` | Atomic installation journal/receipt with exact installed source/helper/manifest/baseline hashes |
| `/var/lib/duora/executor-install-pending.json` | Durable own-attempt pointer, written before baseline or entry changes; unresolved intent blocks another apply and the new executor |
| `/var/lib/duora/executor-recovery-receipt.json` | Record an explicitly recovered own attempt, without asserting product remediation |
| `/var/lib/coolify-release/duora-executor-install/pending.json` | Own private receiver-compatible durable fence; the existing receiver refuses it before imports/configuration/creating a foreign intent |
| `/var/lib/healthchecks-diagnostics/remediation-proof.json` | Informational proof only after actual installation and qualification; schema agreed with the monitor |

Staging and temporary files must remain inside these installer-owned roots. Resolve helpers from the
versioned entry point's real directory, including when the receiver loads the symlink through SourceFileLoader.
Do not depend on the caller's working directory or import a sidecar from an unrelated version.

The Compose, host environment, secrets, Resend, application release pointers, receiver, observer, systemd units,
OpenClaw gateway/catalog, Docker daemon, named volumes, backups, assets and old VPS are outside the write
allowlist. This installer must not call the product deploy function, pull/create/start containers, stop web or
worker, migrate/reconcile SQL, verify/write backups or WAL, restart a unit, or promote a Coolify release.

## Ordered static installation procedure

The CLI defaults to read-only inspection/preflight. Invoke the installer from the exact root-owned verified
bundle with required arguments: `--bundle <bundle-directory> --source-commit <reviewed-40-hex-commit>
--manifest-sha256 <reviewed-manifest-sha256> --artifact-id <native-qualification-artifact-id>`.
Only after review and coordinated handoff, add `--apply --reviewed-inventory-sha256 <reviewed-inventory-sha256>
--expected-executor-sha256 <reviewed-current-executor-sha256>`. There is no deployment action in this CLI.
The installer checks successful exact-source CI, all nine mandatory jobs, native artifact identity/expiry and
all code hashes against immutable public source. The artifact ID is external to avoid a circular archive hash.
The candidate input directory and its ancestry must also be protected and root-owned. Inspection binds both
application current pointers, the current recorded manifest/environment, host environment, Compose, Caddy,
observer pin and pinned release manifest, and receiver code by hash. All are re-read under the locks before
and after activation; this does not disclose their contents or change them.
The Compose path and SHA come from the protected observer pin, including a customized `compose.coolify.json`;
the static installer must not assume the repository's `compose.prod.yml` is the active host configuration.

1. Verify the exact reviewed bundle SHA-256, closed manifest/source/hash/modes and qualification evidence.
   Inspect candidate files statically; reject unsupported contracts or dependencies before writing a host path.
2. Acquire the existing locks, nonblocking, in receiver order:
   `/var/lib/coolify-release/host.lock`, `/var/lib/coolify-release/duora/release.lock`, then
   `/var/lib/duora/.deploy.lock`. Do not create a competing lock namespace. Release all and refuse on contention.
3. Under the same locks, revalidate pending ingress state and the actual Coolify queue/active deployment state,
   current application release, executor preimage hash, Compose/cutoff/Resend preservation and runtime inventory.
   A coordinator lease alone is insufficient. Unknown, active, pending or changed state refuses before a swap.
4. Compare the effective sanitized resources/restart/security/mount/network inventory with the reviewed
   reference and Compose. A matching `com.docker.compose.config-hash` only describes creation configuration;
   it cannot automatically legitimize preexisting `docker update` or network drift. Review differences first.
5. A read-only preflight stops here with a bounded diagnostic result. It does not write enrollment, pull/create
   images, execute SQL writes/reconciliation, stop services or update release/backup state. Read-only schema
   metadata and the legacy SQL hash are checked; external-v2 extraction is separately mandatory before any
   worker stop in the actual deployment. Missing or uncertain evidence is failure.
6. Only in explicitly authorized apply mode, stage and hash-check the entire bundle, preserve installer-owned
   preimages, then atomically enroll the reviewed baseline and switch the single entry point last. All helpers
   must already exist at their versioned paths. No mixed version is allowed to execute.
7. While locks remain held, run the new executor's read-only inventory/schema/contract checks against the unchanged application/runtime,
   confirm exact installed hashes and absence of service/container/state changes, and atomically write the receipt.
   Product promotion requires a later coordinated handoff; installation never triggers it.
8. On installation failure, recover only this installation's own files/preimages, after checking the current
   hash/target still belongs to this attempt. Never overwrite an intervening change, restore an entire snapshot
   or roll back database/application/configuration state. Recovery and partial-failure cases need Actions tests.
   A synced preimage/journal, then the own receiver fence, then the private pending pointer precede every activation write. SIGKILL leaves that intent
   for explicit administrative recovery, never an automatic re-enrollment/retry. From the same qualified bundle,
   use the base verification arguments plus `--recover-attempt <installation-id>
   --recover-attempt-sha256 <reviewed-journal-sha256>` without `--apply`. Recovery acquires the same three locks,
   requires idle ingress/queue and validates all preimage, current baseline, receipt and entry identities before
   restoring either file. A foreign change refuses every replacement. Successful recovery clears only its
   pending marker and leaves the preimages for review. The own receiver fence is always removed last and synced.
   The fence-only crash before private pending is recoverable. Recovery exempts only its exact hash/source/
   manifest-validated own fence; global or any other project's intent and the real global Coolify queue still
   refuse recovery and remain untouched. The existing receiver requires no code or configuration changes.
   SIGKILL at fence/pending/baseline/entry/receipt and during recovery is exercised by real child processes in
   Actions, including an actual receiver attempt after the process dies, invalid fences and foreign intent refusal.
   A new apply never assumes or removes an interrupted fence. No local process termination or Docker is needed.

Enrollment binds host/project, two database/backup containers and volumes, IDs/Created/StartedAt/RestartCount,
image references/IDs, Compose/config hashes, guard/tool versions and hashes of stable Config/HostConfig/Mounts/
Networks. Do not store Env or secrets in cleartext. Running/healthy is checked anew; volatile health logs/PIDs
do not enter the stable fingerprint. Missing baseline or drift refuses before worker shutdown or migrations.
Docker Mounts enumeration order is normalized while retaining every record and field, including duplicates.
Ordering is not a mount identity; changes in sources, targets, permissions, IDs or any other mount field still fail.
Re-enrollment after a legitimate restart/upgrade is a separate explicit administrative operation, never automatic.

## Gates and receipts

Required disposable Actions proofs include real native SQL archives, adversarial ACL reconciliation in both
harnesses, negative contract/runtime guards, resource/network/restart/ID/volume/schema drift, installation
atomicity/recovery and the actual shared-lock/autopublication race. Two application-only deploys must preserve
a persistent SQL session/sentinels, database/backup IDs/timestamps/restarts/mounts/volume and database readiness;
report the application readiness curve separately. Qualify a concrete compatible v2 application rollback
artifact before #226. No proof from the Mac, either VPS or participant data substitutes for this laboratory.

The eventual informational remediation receipt uses scope `duora_database_deployment`; `commit` matches the
APP in `current.json`, `executor_source_commit` identifies executor source, `executor_sha256` and sidecar hashes
identify installed code, and `applied_at` plus exact `lab_run_url` identify real application/evidence. Bind the
reviewed baseline/manifest hashes and tool versions. No provisional positive receipt, `correction.json`, generic
three-project success claim or newly enabled OpenClaw action is part of this contract.

Keep the regional cut at 2026-10-22 22:34:50 UTC and Compose hash
`0a8df3fdc4dc5303d2b5f8b05520ebcdac86639fe184e071583bf6e88fb1b8ad`.
All nine final-head/base checks and triggered native proofs, independent review and the coordinator's explicit
handoff remain required. Bundle commit, native artifact ID and manifest hashes must come from the successful qualification run;
publishing implementation code does not assert qualification or release a production window.
