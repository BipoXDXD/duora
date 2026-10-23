# PostgreSQL component qualification

Application releases use the reviewed native digests in
`infra/images/postgres-component-lock.json`. They do not rebuild or republish
PostgreSQL. Both PostgreSQL and the pgBackRest repository host use this runtime;
changing its digest can recreate both services even when application changes are
limited to documentation or tests.

The selector fingerprints the closed PostgreSQL Dockerfile/input tree, executable
bits and build-context policy. It refuses mutable bases, unsupported build args,
external COPY inputs, symbolic links, unknown metadata and unexpected platforms.
The composite action verifies the successful source run and its exact retained
native artifact through authenticated GitHub APIs. A sparse checkout of the
source commit must produce the same fingerprint as the current inputs. Registry
pull, native platform inspection and OCI transport must retain the locked digest.
AMD64 additionally exercises the existing receiver import and sender selection;
the production receiver remains AMD64-only.

Current scanners, SBOM/license checks, privilege tests and restore/PITR drills run
on the selected runtime in each native image job. Reuse does not waive those
checks. A fixed vulnerability can block the selected image; update and qualify
the component explicitly rather than suppressing the result.

## Initial baseline and updates

The initial lock is anchored to publication39885198914, commit
`ebad1ec9c272d3631fed4207f65e5edf3ceb86a0` (#228). Its AMD64 digest is
`d32f8c4408e804bf8b57583c6fdfb8935380e6326bcc7ccb7397b1f386ada06c`.
The hub observed that digest on the current VPS. This records the source of the
candidate pin; it is not authorization to change a running service. A fresh
runtime/configuration preflight must still establish that the pin matches the
actual database and backup at rollout time. Never recreate them to return to an
older diagnostic baseline.

To update a component, qualify its exact native image digests and authenticated
source inputs first, then review a lock update with the applicable DB/pgBackRest
compatibility, backup/WAL/restore evidence and coordinated maintenance decision.
Changing runtime inputs makes application publication fail before transport;
there is no fallback build. A cache hit is not component identity.

## Retained proof expiry

Native publication artifacts have 90-day retention. The selector refuses missing,
expired, duplicate or incomplete source proof even if GHCR still holds the image.
This keeps production unchanged but blocks a new publication. Review the source
artifact expiry before planned releases and qualify renewal at least 14 days
before expiry. Renewal must again bind source commit, runtime fingerprint, native
digest/platform, scans, SBOM/licenses and restore/PITR evidence. It does not permit
an unreviewed digest change or an automatic runtime replacement. Record renewed
source/run/artifact identities in the reviewed lock; do not bypass expiry or
silently select the latest successful workflow.

## Limits and pending executor work

This component change does not by itself prove an interruption-free application
deployment. The current executor still pauses the worker and replaces application
containers. The separate reviewed executor/payload work must prevent DB/backup
mutations, deliver the current reconciliation SQL independently of the frozen
runtime, and qualify runtime continuity, failure/retry and a concrete compatible
application rollback artifact in disposable Actions infrastructure. #226 stays
held until those prerequisites, the installed executor rollout and the explicit
coordinator handoff have passed. No local Docker, production installation,
OpenClaw action, data restore or old-VPS removal is part of this PR.
