# Suspended registrations — implementation plan (#305)

**Goal:** implement the policy authorized on 2026-10-31: withdraw active registrations of suspended participants, preserve history, promote only eligible waitlisted participants before the event starts, and never restore a withdrawn registration after lifting.

**Architecture:** Events owns a transactional registration operation shared by suspension and registration. Plan affected events and waitlisted user IDs before locks; acquire all user guards/accounts, then all event coordinators; revalidate the plan and restart the whole transaction if resources changed. Safety invokes Events on the existing connection/transaction, then closes rooms and records the existing moderation audit.

**Constraints:** INV-01/02/06/10; API-E03/E05 and ADM22/23; spec 10.4 and T-01/T-06/T-28. No new dependencies, endpoint changes, production data access or deployment. Preserve account authorization, MFA, idempotency and the append-only audit. A forward-only additive migration adds withdrawal provenance and registration version; no inferred historical backfill. The approved policy and its operational limitations are recorded in ADR-0044.

- [x] Baseline: build and existing account suspension integration tests.
- [x] RED: integration test for confirmed suspension, FIFO promotion, preservation of row/check-in, temporal boundary, ineligible waiters and terminal withdrawal after lift.
- [x] Implement additive registration history fields and transactional withdrawal/promotion, including outbox facts and audit.
- [x] Share plan/guard/promotion protocol with new registration; reject attempts to re-register Withdrawn rows rather than report a false success.
- [x] Add deterministic PostgreSQL concurrency tests: suspension versus a new registration of the target; suspension versus third-party registration; two suspensions/promotions; candidate suspension versus promotion; queue discovery change; rollback on lock timeout.
- [x] Verify focused tests, unit/architecture tests, full backend suite and formatting; record platform failures separately without changing gates.
- [x] Update ADR, model, concurrency and acceptance documentation. Review complete diff and deliver a patch/report; merge requires human review under AGENTS.md.

**Review focus:** all event coordinators must precede every room/round lock; no new user guard after event locks; current eligibility must stay stable through promotion; repeated suspension must not decrement twice; completed/cancelled events and historical timestamps remain unchanged. Consumer delivery of promotion notifications is separate from the durable outbox fact (no new notification transport in this issue).

Verification record: baseline 48/48 suspension tests passed; initial policy tests reproduced 11 failures; current focused suite (suspension + regional migration) passes 79/79. Build passes with existing SixLabors Debug-license warnings; formatting verification passes. Full backend run is being recorded separately, including platform-dependent failures. Independent static review found no actionable issue. The historical regional migration fixture seeds the old registration shape directly because the new writer correctly requires the new schema.
