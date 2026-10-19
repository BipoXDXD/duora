# Language matching implementation plan

Goal: implement the approved design, including the necessary M2 infrastructure, without weakening privacy or eligibility.
Spec: ../specs/2026-10-19-language-matching-design.md, approved by the owner on 2026-10-19.
Architecture: existing .NET modular monolith, PostgreSQL transactions, pure Core rules and React SPA. No new dependencies or production operations.

## Constraints
- Explicit communication languages `en`/`pt`; display locales `en-US`/`pt-BR` are independent.
- Legacy accounts have no communication declaration and cannot match.
- Shared user guards serialize preferences, display locale, blocking and reservations.
- No translation, microphone capture, waiting-based relaxation or geographic decisions.
- Same immutable experience version and IDs; localization is per participant.
- Keep generated contracts synchronized and record actual test results.

## Tasks and ownership
- [x] Preferences: Core declaration, private persistence, locale persistence, GET/PUT /me/preferences and /me/locale, ETag/If-Match, guard protocol, HTTP and PostgreSQL tests. Own Profiles/Identity changes; coordinate migration generation.
- [x] Core matching/content: bilingual policy V2, deterministic shared language, bilingual catalog and media validation, per-participant projection, legacy reader, unit tests. Own Core Matching/Experiences and their unit tests.
- [x] M2 backend: events, rounds, reservations, invitations, persistent rooms, deadlines, actions, blocking cleanup, API contracts and real PostgreSQL tests. Own new M2 Infrastructure/Web/Contracts files. Uses persisted preferences only after guards. Include recovery and authorized projections.
- [x] SPA: private preferences and display locale; events/check-in/invitations/room/conclusion with no microphone requirement; typed API and bilingual copy; component and E2E tests.
- [x] Integration/review: generate migrations serially, OpenAPI/client; run backend, frontend, PostgreSQL and browser checks; review privacy/locks/legacy/rollback; publish local review artifacts and commit coherent changes.

Each owner writes and runs meaningful failing tests before implementation, then records green results and unresolved failures. Parallel tasks have disjoint files; integration and migration generation are serial. No concurrent changes to generated contracts. Controller integrates shared registration points.

## Review focus
1. Preference or locale update racing reservation cannot use a stale snapshot.
2. Blocking or loss of eligibility invalidates reads, writes and idempotent replay.
3. Legacy clients/accounts never acquire implicit communication proficiency.
4. Published localization cannot change structure or leak another participant's private state.
5. Browser completion works without requesting microphone; simulated API tests do not stand in for PostgreSQL end-to-end evidence.

## Final validation (2026-10-19)
Backend: 2566 passed, zero failed/skipped (unit, architecture, real PostgreSQL). SPA: 672 passed; browser suite:129 passed; real HTTPS/PostgreSQL E2E:1 passed with zero microphone calls. Build/format/type/lint and generated contract checks passed. oasdiff reported no breaking changes; source Gitleaks and dependency Trivy passed. Full production image/Release gates were not run; existing SixLabors license warning remains. Static final review found no new P1/P2 issues. Operational slice is Constellation; full M2 and Routes HTTP adapter remain outside this slice, as documented in model and testing guide.
