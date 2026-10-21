# Retention and security notices implementation plan

Goal: implement approved #46/#68 policies with atomic PostgreSQL effects and regression evidence.
Architecture: reuse ops.jobs with an isolated retention kind and stable recurring identity; consume recovery codes and enqueue their notice in the existing account transaction. No production access or deployment.
Spec: issues #46/#68, explicit approval recorded 2026-10-21; ADR-0021, INV-06/08/12, API-A04/A14/A16.

## Constraints and review focus

- Seven days from completion for Delivered/Expired; thirty from processing for terminal webhook receipts.
- Failed requires explicit review and completion older than thirty days; unreviewed failures alert, never expire automatically.
- Preserve open rows, null terminal dates, multiple consumers and recurring jobs; batches <=1000.
- Fence stale workers, bound transactions, recover after crash without replaying missed calendar days.
- Recovery notices contain no secrets, commit exactly with consumption, snapshot holder locale/time zone; enrollment start remains audit-only.
- No new dependency, no protected workflow edits; all CI gates required before merge.

## Tasks

- [x] #68: add real API tests for successful recovery use, replay/invalid code, rollback, enrollment audit-only and A14/A16 account quota. Run RED, add enum/catalog/outbox/service notification, run focused GREEN and pickup dispatch tests.
- [x] #46: add PostgreSQL tests for terminal age boundaries, batch limit, multiple consumers, explicit review and open-row preservation. Add additive migration and retention service; prove RED/GREEN.
- [x] #46: test recurrence, stale fencing, rollback, parallel claims and consumer isolation. Add worker wiring, bounded retry and aggregate metrics; test real host registration.
- [x] Document approved retention and security policies, operations/review procedure and rollback. Validate migration against disposable PostgreSQL.
- [ ] Run backend suite/build/format and frontend gates as relevant; independent whole-branch review, fixes, push PR(s), wait all required CI checks, merge reviewed SHA and close only fully verified issues.

## Execution ledger

- Base: 89e4d44 (includes approved IPinfo documentation #134). Clean dedicated branch.
- Approval already supplied: execute inline without another product/design approval round.
- Separate commits/PRs for credential notices and operational retention to keep review scope focused.
- #68 RED: new recovery notice absent and rollback injection never fired; GREEN: credential/dispatch/quota suite 63 passed, expanded locale coverage included in 71-test focused pass.
- #46 RED: absent retention implementation; GREEN: PostgreSQL policies, boundaries, recurrence, parallel claim, fencing, review and rollback passed.
- Final review P1 reproduced: RLS/advisory barrier proves newly committed Pending sibling was cascaded. Fix locks orphan candidates and rechecks in a fresh READ COMMITTED statement.
- Final review P2 reproduced: a failing observation blocks the worker. Fix isolates observation and advances backoff before attempting it.
- Test harness correction: NpgsqlDataSource.ConnectionString redacts password; worker tests now retain the original synthetic connection string. First full run found that fixture failure and was interrupted after the correction; final full suite is still required.
- Ruling: SMTP homologation remains open on #68; current product has only pickup adapter, so adding a provider/transport is outside the approved notice change. Cost: real delivery cannot yet be claimed.
- Ruling: Retention rollout defaults off until compatible workers/schema and release validation; no production expurgo is performed by this task. Cost: operator must activate the documented flag during release.
- Added direct Done-job cutoff and explicit retry-backoff/exhaustion regressions; included in final suite.
- Final review fixes: both adverse interleavings reproduced RED, retention regression suite GREEN (11/11 before the two additional coverage cases).
- Full default-parallel run: 2711 tests, 2707 passed, 4 failed; root cause PostgreSQL 53300 at 400 connections, cascading registration/reauth 503 in unrelated profiles/editorial/security tests. Rerun entire solution with --max-threads 4; no tests or production limits changed.
- Bounded rerun (4 groups): 2710/2711 passed; ParallelWrongReauthenticationsAreAllCounted still returned database-unavailable 503, but passed alone. Retention scratch harness now clears only its own migrator pool on disposal, releasing idle synthetic database connections. Full suite rerun at 2 groups; concurrent requests inside each test remain unchanged.

- Final full backend run: 2711 passed, zero failed/skipped, --max-threads 2 (6m09s). Frontend: typecheck/lint/format/build and 774 tests passed. CI remains the final merge gate.
