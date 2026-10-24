# Regional publication pause implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` in this session. Execution is already delegated; no new approval or subagent is required.

**Goal:** prevent an administrative matching pause from admitting new event publications during a V3 cutover.

**Architecture:** reuse the existing process-bound `ParticipationOptions.MatchingEnabled` flag. `PublishAsync` refuses before PostgreSQL access when false; the existing operator drain still covers requests already admitted and older processes.

**Tech Stack:** C#/.NET 10, Npgsql/PostgreSQL 18, xUnit v3, existing hosted CI.

**Spec:** [regional-matching.md](../../operations/regional-matching.md), G4 publication suspension; INV-02/06/08/09 and administrative event publication.

## Global constraints

- Preserve default true, public contracts, migration history and persisted V2/V3 versions.
- No configuration change, production access, IPinfo download, credentials or new country.
- No heavy Mac build/test. Required backend/PostgreSQL and nine gates run in normal hosted CI.
- No workflow, infrastructure, protection, MFA or catalog UI edits in this change.

## Review focus

- Valid administrator/content must be denied during pause without events, rounds or jobs created.
- Resumption must use the explicitly configured cutover, not create V2 silently.
- Already admitted work and old processes still require draining; a local check is not a distributed fence.
- Existing deadlines, recovery jobs, reservations and historical versions must stay intact.
- Paused publication retains the existing unsuccessful HTTP/service contract; no new private data is returned.

## Task 1 — enforce and verify the pause

**Files:** `backend/src/Duora.Infrastructure/Participation/ParticipationService.Events.cs`, `backend/tests/Duora.IntegrationTests/Participation/RegionalMatchingTests.cs`, `docs/operations/regional-matching.md`.

**Interface:** existing `PublishAsync(...) : Task<Guid?>`; false flag returns null before connection/transaction, true continues unchanged.

- [x] Add `MatchingPauseRefusesPublicationWithoutCreatingRoundsOrJobsAndResumeUsesV3`: isolated migrated PostgreSQL, valid product administrator and published content, snapshot event/round/job counts, deny paused publication and compare unchanged counts, resume fresh service with explicit cutover and require `[3,3,3]`.
- [x] Add the early guard `if (!MatchingEnabled) { return null; }` before existing validation and I/O.
- [x] Update the runbook to distinguish the corrected release from older publishers and retain process configuration/drain requirements.
- [x] Run `git diff --check`; exit 0. A local .NET RED run was not performed because heavy Mac execution is excluded; no RED result is claimed.
- [ ] Commit the frozen change, open the PR and run the existing nine hosted gates once. Backend must execute the new PostgreSQL regression; link its actual result before calling it passed.
- [ ] Review final diff/head and report evidence/limits. Do not merge until coordinated passage.
