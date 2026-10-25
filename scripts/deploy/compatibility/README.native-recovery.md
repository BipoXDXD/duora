# Source proposal v3 only: composed native current-v2 recovery

This directory contains candidate source, not a native execution receipt. Nothing here has been imported or initialized as a native harness on the Mac. No Docker, build, SDK install, SSH, browser, network or GitHub mutation was performed for this proposal. Executed validation consists of AST parsing, the new pure stdlib receipt tests, and the explicitly requested bounded existing-runtime Pyright check. The native harness itself was never imported or executed.

The parent remains the sole product/shared-source writer. Copy the three proposed compatibility files into the actual reviewed PR260 checkout only after independent source review. The two `.integration.patch` files are proposals against the functional lab and the existing same-job workflow. They were not applied to product source. No PR, issue, automation or additional job is created by this proposal.

## Interface

`current_recovery_lab.py` requires all of:

```
--cohort PATH
--old-source PATH
--workflow-source PATH
--functional-receipt RUNNER_TEMP/.../receipt.json
--handoff RUNNER_TEMP/.../recovery-handoff.json
--output RUNNER_TEMP/.../recovery-receipt.json
```

It first refuses anything other than root, native Linux x86_64, Python3.12, GitHub-hosted Actions, repository `BipoXDXD/duora`, pull request260. The event's actual PR head must match the clean workflow source checkout; cohort1a9/tree8e8 and old3d99/tree5adb must match the existing pins. Every git subprocess disables lazy fetch. There is no fetch/pull/build/install SDK path in this driver. Native Compose may acquire the digest-pinned MinIO fixture image; all five actual old images must already be cached by the functional lab.

The historical synthetic135 control-tail source was executor `ac5510f6b47ffac1a07ee1cb79af386cb931249140c32aae9d704265929ec1ae`. This is **historical metadata**, not a stale acceptance pin. The driver binds the actual clean reviewed PR260 checkout, records its exact executor hash plus all four sidecar hashes, copies them unchanged to a root-owned versioned directory, and rechecks those hashes before every original `main` call. The three unchanged sidecars remain explicitly pinned. Parent-only source typing postconditions can therefore be composed without weakening the four-file identity binding.

The root-owned fixture launcher calls the original executor `main` with actual arguments. The same administrative `main` owns the real host, project and executor flocks. Observer's minimal `SAFE_ENV` is retained; the launcher restores only the saved public Actions identity to let its native-only gate execute again. No token, credential, user-provided command, Docker endpoint or production path override is passed through that context.

## Sequential single-build handoff

1. The proposed functional patch additionally caches the old worker; that makes five old image acquisitions total. Its functional behavior and receipt scope stay `old_web_schema48_function_only_not_executor_recovery`.
2. Only after `lab.execute()` completes does the functional lab tag the exact single built immutable cohort migrator with a random owned keeper tag. Its original containers, volumes, networks, temporary secrets and original image tag are then removed by the existing cleanup.
3. Only after successful cleanup and functional receipt validation does it emit the private handoff JSON. The handoff binds run/attempt, actual workflow head, exact build config ID, unique keeper tag, and the exact functional receipt hash. It transfers only the built object and public evidence. All other required material is in the three already-independent source checkouts; the recovery driver copies unchanged executor/receiver bytes into its separate fresh root, creates new synthetic secrets, and never reuses a live functional-lab container, volume, network or secret.
4. The new recovery step runs in that same job, after functional cleanup. It validates the existing functional receipt with its original parser, checks the handoff and actual cached image IDs, and refuses existing or foreign root paths/resources, conflicting exact container/project/network names, occupied loopback port8000, and overlapping fixed backup subnets.
5. After the native evidence is captured, it removes only its own labeled runtime resources and exact observer resources, then the transferred keeper tag as its last image cleanup. It never runs Compose `down`, `restart`, Docker prune or a database restore. The proposed workflow also includes an `always()` fallback that can remove only the exact transferred tag if an earlier stage refuses. Both paths verify handoff identity and actual config ID before removal.

There is no second cohort build, old application rebuild, database/backup recreation, restart, down, PITR or repeated old restore/security matrix. Native cleanup removes the disposable database only after the protected interval and receipt evidence have been captured.

## Real native path

- Render the actual product+backup-drill Compose into an owned root fixture named `duora`, preserving image interpolation. Own PG volume is exactly `duora_pgdata`; actual runtime guard collection, audit and preservation checks are used.
- Generate actual synthetic database/TLS secrets with the existing secret generator. Use the frozen public PostgreSQL18.6 image for PostgreSQL and backup, a real MinIO HTTPS bucket with versioning and COMPLIANCE ObjectLock, real stanza-create/full, and unchanged `duora_backup.py verify` (bucket protection, pgBackRest info/check and archiver). The wrapper uses the existing local drill `--skip-egress-check`, explicitly excluding native B2 API and egress firewall claims.
- Initialize the exact ordered35 catalog using the old3d99 migrator plus oldSQLd79. Start actual old3d99 web/worker/edge and require real readiness. Bootstrap files are explicitly synthetic administrative fixture records, never fabricated production deploy success. Collect the real runtime inventory and explicitly enroll its baseline. Use original CLI read-only control enrollment projection and its exact reviewed public hash for explicit fixture enrollment.
- Keep one actual `duora_app` pgbench backend alive with `default_transaction_read_only=on`, a sentinel script that checks readonly state each transaction, a stable backend PID/start time, and sentinel data. No persistent `psql` is used because actual quiescence rejects it. The background monitor executes only `pg_isready`; SQL identity observations are bounded, waited checkpoints.
- Run source `arm_current_v2_deploy` under actual locks, then source `_run_current_v2_operation` with the original exact enum names `worker-stop` and `migration` for an actual worker stop and actual cohort DDL35→48 migrator job, each positively acknowledged by its waited real subprocess result. The deterministic fault boundary is after acknowledged migrator exit0 and before the next producer. No unknown/nonzero producer is converted to recovery permission.
- Invoke original administrative CLI `--recover-current-v2` and `--complete-current-v2-recovery-control`. SQL48 is copied from the stopped immutable inherited migrator with the same real `_copy_stopped_sql`; SQL, ACL witness, WAL verify, Compose app recovery and all guard observations execute normally.
- Install byte-exact qualified receiver1ccd as its allowed protected versioned link. Its unchanged HTTP client talks to a strict allowlisted HTTP server bound to127.0.0.1:8000. Query all three synthetic application queues with that real client. API env GETs intentionally hide shown-once values; PATCH can change only the observer image and preserve existing flags/other rows. Start returns one positive job UUID; the server records exact PATCH/start/finish counts.
- At actual start, independently check that host/project locks remain held while the executor lock is free. The job calls actual `observer.normalize_observer`, `validate_observer` and `deploy_and_start`. That executes the actual child normal deploy CLI steady branch, then starts the actual unprivileged sleeping observer. Its effective image, provenance labels, env, readonly root, no capabilities, pids16/memory16MiB, network membership and readiness are independently witnessed by original executor code.
- The fixture edge mounts an own loopback-only HTTP Caddyfile serving the actual old image `/srv` assets and health port8081. The original cohort Caddy source hash is retained separately. There is no request to production DNS/HTTPS/www. The worker uses its existing pickup transport in own tmpfs; its queue is empty and its egress network is internal.
- Confirm real PG/backup/volume profiles, PG ID/image/StartedAt/RestartCount/mount hash/postmaster start, exact35→48 ordered history, stable readonly backend/sentinel, current/previous/manifest/env/floor/runtime/compose/hostenv bytes, assets markers, schema DDL and actual recovered apps. Schema digest excludes ownership/ACL and only pg_dump's random restrict transport nonce; actual ACLs are separately witnessed by original source. This avoids mistaking intentional SQL privilege repair for DDL drift.
- Check exact archived raw intent preimage hashes, app/control outcomes, compatibility pointer, active intent absence and fence absence. Repeat the completed original CLI and require no SQL writes, WAL/backup invocation, Compose up or HTTP writes. A sanitized subprocess classification trace distinguishes SQL reads from SQL producers and contains no command arguments, environment, SQL, HTTP payload or credentials.

The driver has one acknowledged expansion/recovery cycle. It does not inject native archive/fence crash faults; known tail interruption cases remain the existing frozen135 synthetic evidence. Its native repeated completion checks real archives/fences and no replay. It never claims that synthetic tail interruptions became native proof.

## Exactly one origin adapter

Expanded recovery's original CLI internally calls `payload.extract_sql`. A disposable root entry wrapper replaces only that one function for the exact declared incoming migrator reference/revision/architecture. The incoming reference is marked as a synthetic envelope identity, never inserted into Docker RepoDigests and never authenticated as GHCR. The adapter inspects the actual inherited build ID, requires Linux/amd64, source URL, migrator title, exact cohort revision and no declared volumes, then calls the unchanged `_copy_stopped_sql(buildID, buildID)` and requires canonical SQL5d66. Every other extraction delegates to the unchanged original extractor. Nothing rewrites Docker results, root reads, locks, runtime, SQL, WAL, application identity or the receiver client.

The incoming fixture retains actual old application refs (never started as incoming); only its migrator source is the inherited cohort build. The real worker-stop/DDL operation uses a separate owned env file mapping only the migrator image to that actual immutable build ID. This is the same local origin mapping, not a second Docker/Compose transport seam. Full normal incoming deploy `main` is explicitly unexercised. The normal recovered-current child `main` is exercised through the actual observer.

Actual public incoming GHCR authentication/extraction **before worker shutdown** remains mandatory for actual259 publication and unchanged by this candidate. Existing helper proof is reused; this native composition cannot substitute for it.

## Receipt and artifacts

The independent new parser requires `payload_origin=local_cohort_build`, `incoming_ghcr_exercised=false`, `product_forward_main_exercised=false`, `control_plane=CoolifyHTTP_fixture`, and `real_Coolify_hosting_exercised=false`. It requires real recovery/complete CLI, real locks/handoff/runtime/WAL/client/apps/observer flags. It also requires false claims for observer main proxy checks, B2 API, egress firewall, native tail interruptions, production HTTPS/www, production acceptance and publication authorization by receipt.

Only these public artifacts are proposed for upload:

- Existing unchanged-scope functional `receipt.json`.
- Separate `recovery-receipt.json`.
- `recovery-command-trace.ndjson` (producer classes/pid/time only; snapshot through completed repeat).
- `recovery-public-records.json` (original source public application/control/ready/compatibility records).

Private handoff, synthetic secrets, complete env files, raw Docker inspect environment and server token are not uploaded. Receipt output is at most128KiB. Runtime/error receipts contain failure type and stage only; they do not echo arbitrary exception messages.

## Runtime budget and verification status

Steady fixture memory limits total3456MiB plus observer16MiB. The incoming migrator is768MiB and the worker is stopped first, so the protected expansion peak is3840MiB (4224MiB if all service limits are conservatively added). The inactive restore profile is not started. This fits a6GiB minimum RAM admission and stays sequential after functional cleanup; actual native behavior/time remains unexecuted.

Local verification is AST-only for all proposed Python plus the new v3 pure parser delta test. Those tests validate scope separation, all required/forbidden flags, exact catalog/preservation, empty-baseline refusal, frozen sidecar/receiver identity, integer exactly-once API counts, no replay, continuity and duplicate/nonfinite/oversized JSON refusal. No existing test suite was repeated. Static validity is not a native success claim. Final source review and the single authorized final-head CI run remain the parent's next steps.


## Retained fixes and the current v3 barrier contract

The original d035 candidate and original manifest remain unchanged in the parent directory, as does v2. Exact v3 diffs to v2 and to the original candidate are included.

- Set755 explicitly on only the newly owned synthetic certificate directory after mkdir. This defeats inherited umask077 for MinIO UID1001 traversal without changing synthetic secrets700, files400, any production path, or certificate validation.
- Replace the two invalid journal names with the exact original product enum values `worker-stop` and `migration`. No product enum or guard changes.
- Replace optimistic event-stream liveness with a real consumed barrier protocol. Subscribe with the exact native ownership label/nonce and a `--since` time from before subscription. Create only an owned never-started container from the already cached old edge as the start marker, verify its real CID/image ID/labels/created state/zero mounts/network none, and wait for its unique actual `container/create` event to be parsed before admitting protected operations. After all final protected observations, create a different owned end-marker container and wait for its actual event to be consumed while the stream is still live before stopping the stream. Marker containers are re-inspected and removed without force by the same nonce-owned cleanup.
- Bound each JSON event to64KiB and the complete stream to2MiB. Refuse duplicate fields, malformed framing, nonfinite values, unknown timestamps/actor IDs, missing ownership nonce, early EOF, reader failure, timeout, duplicate marker records, missing end acknowledgement or unknown reader shutdown. No missing/unknown stream condition means all-clear.
- The separate receipt parser requires both consumed markers with distinct real64hex IDs, exact container/create type/action, strictly increasing record indices, ordered daemon `timeNano` and host consumption timestamps that bracket the entire protected interval, actual endConsumed and readerComplete assertions, and stream liveness at both marker consumptions. Existing continuous readiness, stable backend/session/sentinel/runtime identity and forbidden PG/backup events (including pause/unpause) remain required. Forbidden events are checked only inside the acknowledged start/end record interval; pre-bootstrap history is not relabeled as protected coverage.
- Keep original worker pickup settings, empty synthetic mail queue and internal fixture egress. No email is sent and no production environment or application code is changed.
- Retain the prior pure parser cases and add only the v3 delta case rejecting network markers even when a labels field is supplied, and refusing other event types/actions. AST checks and tiny parser tests do not run the native harness.

Static typing uses the unchanged standard/error settings and absolute extraPaths, the existing Node with a256MiB heap and45s timeout, existing GoblinShift Python path, and the explicit bundled fallback typeshed. Initial output and the precise command are retained. Only source typing postconditions and an HTTP override signature are repaired, with no ignores or changed checking policy. The local Python3.14 templatelib diagnostics are external to these three candidate files; actual CI remains Python3.12 and is still required.


## v3 minimal barrier correction

Hub found that Moby network/create events expose only name/type in Actor.Attributes, so an events label filter cannot deliver those v2 markers. V3 changes only the marker primitive, its ownership/cleanup qualification and the parser/test event type. Existing consumed-event ordering, timeout, bounded JSON, stream liveness and UNKNOWN refusal remain unchanged, as do TLS permissions and the exact worker-stop/migration enums.

Each barrier now executes only `docker container create --pull never` using the already cached immutable old edge reference, with its actual cached Docker image ID, own nonce/phase labels, network none, readonly filesystem, no capabilities, no new privileges and entrypoint `/bin/true`. The marker is never started. The image is inspected before creation, bound to the exact pinned reference/RepoDigest/source/title/revision/platform, and refused if it declares volumes, preventing anonymous mounts. Its actual inspect.Id is retained unchanged across both markers and cleanup; this marker binding does not assume that a Docker object ID equals the OCI config digest. The actual created container must have its exact CID/name, old edge image ID/reference, nonce/phase labels, entrypoint, created/not-running/not-paused/not-restarting/PID0 state, RestartCount0, zero mounts/binds/volumes-from/devices and network none before marker admission. Marker events must be actual container/create records with matching CID and phase. Cleanup repeats the same actual ownership/image/unstarted/isolation checks before a non-force removal; a running, altered or foreign marker is refused.

No build, pull, start, mount, product checkout, product enum, CI job or other boundary is added. The proposal still has no native execution evidence. Local verification executes only the new v3 parser delta case, AST parsing, and justified bounded source typing checks for the new ownership helper and the subsequent parent-directed actual-Docker-ID marker binding. Both commands and results are retained. Prior tests are retained in source without rerunning their test methods.


The parent separately owns the newly identified old-image Docker-ID versus OCI-config identity correction and local-build field renames. V3 deliberately does not mix that broader identity delta into the barrier review. The parent will apply and qualify it after this source proposal; the current v3 proposal is not independently a claim that those other identity checks have already been corrected.


## Exact Docker ID and OCI identities

The five historical raw manifest fixtures are reused from already qualified public OCI evidence.
Each exact reference leaf equals SHA256(raw fixture); its config descriptor must equal the pinned OCI config digest.
The live Docker image ID is recorded separately as `docker_image_id` and must be exactly that leaf or that config,
with the immutable RepoDigest, Linux/AMD64 and source/role/revision metadata still required.
The config digest is explicitly derived from the frozen manifest, never described as observed through Docker.
Application and observer identities compare actual Docker IDs.
The one new local migrator build and its same-job keeper handoff record only `docker_image_id`,
without inventing an OCI config identity, registry provenance, another build or publication.


## Disposable hosted Actions /opt prerequisite

The Ubuntu runner image intentionally grants mode0777 to /opt. Before creating any fixed fixture path,
the native root wrapper observes that exact directory by NOFOLLOW FD and requires root:root, canonical
path/inode identity and only the known0755/0777 modes. On0777 it changes ONLY that top inode to0755,
retains the FD/preimage and restores0777 in the main cleanup after rechecking the same root inode/mode.
No recursion, content/SDK changes, chown, other parent repair, production host or Mac operation is permitted.
An existing0755 inode is observed without chmod. Unknown state or unconfirmed restoration fails the receipt.
The structured original/fixture/restored mode and identity evidence is required by the native parser.
Source: https://github.com/actions/runner-images/blob/main/images/ubuntu/scripts/build/configure-system.sh

## Rendering the disposable runtime model

The native driver enables the migrate and restore profiles only on its Compose config rendering command,
so the complete JSON model contains both otherwise inactive service definitions before budgeting it.
This config command starts no service. Subsequent runtime calls keep their existing explicit service lists
and migration-only profile; the restore profile is not started. A closed required-service inventory is recorded
before any runtime start, and missing definitions fail before provisioning. Bootstrap stage labels separate
rendering, inventory, MinIO, database and WAL failures without exposing credentials or command output.

Failure diagnostics record at most four explicit exception causes with at most eight source locations each.
Each location contains only one of five fixed public source basenames and its integer line number. Exception
kinds also use a fixed allowlist; messages, arbitrary paths, function arguments, locals, SQL and subprocess
output are never included. Enrollment stages distinguish manifest validation, real runtime inventory/recheck,
control bootstrap, receiver-idle checks, read-only projection, binding and explicit admission.
