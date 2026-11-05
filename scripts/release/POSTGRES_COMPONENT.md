# PostgreSQL component qualification

Application releases use the reviewed native digests in
`infra/images/postgres-component-lock.json`. They do not rebuild or republish
PostgreSQL. Both PostgreSQL and the pgBackRest repository host use this runtime.
A release never recreates them: until the running digest changes, the executor
refuses a release that carries another digest, even when application changes are
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
The running digest changes only through the administrative swap in
`docs/operations/deploy.md` ("Troca do digest do PostgreSQL"), for the same major,
catalog, pgBackRest and collation. Production and the lock must change in the same
window, because each side refuses the other's digest.
Changing runtime inputs makes application publication fail before transport;
there is no fallback build. A cache hit is not component identity.

## Qualification workflow

`.github/workflows/qualify-postgres.yml` is the only workflow that builds this
component. It accepts only `workflow_dispatch` from `main`, with the full SHA in
`candidate_commit` and `mode` set to `qualify` or `renew`. Workflow code, scripts
and the compose file come from `main`. The candidate contributes a credential-free
sparse checkout of `/.dockerignore` and `/infra/images/postgres/`. Candidate code
runs in two ways only: its Dockerfile inside `docker buildx build`, and the
resulting image, which the gates start as containers.

- `qualify` validates the candidate recipe, builds with `--provenance=false
  --sbom=false`, because an attestation would produce an index that the transport
  refuses, and pushes one native manifest under a `qualify-*` tag. The digest is
  referenced through the lock only; failed runs can leave an unusable tag behind.
- `renew` never builds. It requires `candidate_commit` to equal the lock's
  `source_commit`, then pulls and verifies the locked digest.
- Both modes run `pgbackrest version`, `DatabaseObserverImageTests`, the restore
  drill, Trivy and the SBOM, then retain `release-<arch>-<attempt>` for 90 days.
  `candidate-recipe.json` holds the fingerprint for the lock update.

In `qualify`, Trivy runs without `--ignorefile`, so a `.trivyignore` exception on
`main` cannot hide a finding in a new image. In `renew`, Trivy reads the reviewed
`.trivyignore` of the `main` checkout, never a file from the candidate, so a
running digest can be renewed under a valid exception, as a release would accept
it. There is no separate license evaluation of the image SBOM in this workflow.

`AppRolePrivilegesTests`, `MigratorPayloadImageTests` and
`RuntimeContinuityLabTests` need application images, so they do not run here. The
`images` job of the lock pull request runs all three, with different reach:
`AppRolePrivilegesTests` is the only one that exercises the locked digest,
`MigratorPayloadImageTests` inspects only the migrator image, and the continuity
lab builds its v1 phase on the frozen digests of `runtime_guard.py`, then ends by
swapping to the reviewed target in `scripts/deploy/fixtures/postgres-swap-target.json`
through the real executor command. That fixture is the target because the lock
still names the running digest when the executor change lands. A qualification
run alone proves nothing to a release.

The lock binds this path differently from a release publication. The run's
`head_sha` is the `main` commit, so `validate_origin` requires `head_branch ==
main` and `workflow_dispatch`, and binds through `publication.commit`. The
publication holds only `postgres`, has no `ciRun`, and `select-proof` receives
`--run-json` to compare the artifact with the authenticated run. On this path
only, the composite action also asks GitHub whether the run's `head_sha` is in the
history of `refs/heads/main`, because `head_branch` alone cannot tell a tag named
`main` from the branch, and whether `source_commit` is in the history of the
checkout, so the lock never depends on a commit that can be collected. On AMD64 the
transport still exercises the receiver import, but `send.requested` needs five
roles and stays out: the evidence records
`receiver_import_and_sender_selection_verified: false` with `sender_scope`.

Merge the lock PR with a merge commit, never by squash or rebase. The candidate
commit lives only on the PR branch, so either of those drops it from the history
of `main`. The PR stays green, because its merge ref still contains the commit,
and every `images` job on `main` then fails closed. `main-protegida` allows all
three methods, so this depends on the person merging.

Re-run all jobs, never only the failed ones. The lock names one attempt, so a
partial re-run leaves one architecture without its artifact and fails closed.

## Retained proof expiry

Native publication artifacts have 90-day retention. The selector refuses missing,
expired, duplicate or incomplete source proof even if GHCR still holds the image.
This keeps production unchanged but blocks a new publication. Review the source
artifact expiry before planned releases and qualify renewal at least 14 days
before expiry. Renewal uses `mode: renew` above and must again bind source commit,
runtime fingerprint, native digest/platform, scans, SBOM and restore/PITR evidence.
It does not permit an unreviewed digest change or an automatic runtime replacement.
Record renewed source/run/artifact identities in the reviewed lock; do not bypass
expiry or silently select the latest successful workflow.

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
