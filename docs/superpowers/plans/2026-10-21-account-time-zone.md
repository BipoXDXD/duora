# Account time zone and readable event times implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. The owner approved the three UX recommendations and explicitly requested implementation in a new PR; do not request the same approval again.

**Goal:** Implement issue #81's approved generic event zone plus UTC offset, optional explicit account zone, and preferred security-notice time alongside the UTC reference.

**Architecture:** Identity owns nullable `preferred_time_zone` and a separate optimistic version. An authenticated GET/PUT resource uses ETag/If-Match, validates server-supported IANA IDs and serializes updates on the account row without changing matching declarations or security stamps. The SPA derives formatting from the authenticated account context; no zone preference is inferred or stored in browser storage. Security notices snapshot the saved zone in their transactional outbox payload, retain their existing bilingual UTC content, and add the preferred local time.

**Tech Stack:** Existing .NET 10, PostgreSQL 18, React/TypeScript, Intl, Vitest and Playwright. No new product dependency.

**Spec:** Owner-approved three recommendations in this conversation; ADR-0026 proposal 4 and `docs/testing/event-time-zones.md`. Record the accepted extension in ADR-0026 and update the email/contract documentation with the implementation.

## Global constraints

- Login minimum stays exactly 350 ms; existing floor, jitter, deadline and tests are untouched.
- Preserve UTC instants, civil dates, event IANA zones, eligibility and matching behavior.
- Explicit Save is the only operation persisting a zone; removal stores null. SPA fallback is browser; email fallback is UTC.
- Preferred zone never overwrites event zone or changes because the device/locale changes.
- Event output always has an instant-specific UTC offset; UTC is displayed as UTC, and offset-only fallbacks are not duplicated.
- New PR for human review; no merge, production access, weakened tests, scanners or workflow changes.

## Review focus

1. Same 01:30 in the November New York transition must have different UTC offsets in both languages.
2. Clearing or switching accounts must immediately stop using the previous account's zone.
3. Invalid/omitted values, stale versions and concurrent saves must not silently clear or overwrite a choice.
4. Existing outbox payloads must retain UTC output; a queued notice must use its zone snapshot after the account preference changes.
5. Browser ICU unable to format a server-supported zone must show an explicit UTC reference rather than crash or mislabel the time.

## Task 1: Private account preference

Files: `Identity/TimeZoneStore.cs`, `Core/Identity/TimeZones.cs`, Identity user/configuration and migration, `Web/Profiles/TimeZoneEndpoints.cs`, preference/Me DTOs and projection, DI registration, integration tests.

Interfaces: `OwnTimeZone(string? TimeZone,long Version)`; `TimeZoneStore.GetOwnAsync(Guid,CancellationToken)` and `UpdateOwnAsync(Guid,long,string?,CancellationToken)`; `GET/PUT /me/time-zone`; `GET /me/time-zone/options`; optional nullable `MeResponse.PreferredTimeZone`.

- [x] Add failing HTTP tests: default null, save NY/SP/UTC and remove, invalid IDs/empty/offset/omitted field, missing/stale If-Match, concurrent winner, own-account isolation and unchanged declarations.
- [x] Run `dotnet test --project backend/tests/Duora.IntegrationTests -- --filter-class '*AccountTimeZoneTests'`; expect 404 before implementation.
- [x] Add `preferred_time_zone text null`, `time_zone_version bigint default 0`, and private account resource. Reject invalid IANA with 400, missing precondition with 428, stale with 412; serialize and recheck status on the user row.
- [x] Generate additive migration/snapshot, rerun HTTP tests and verify fresh null defaults.

## Task 2: Account form and event display

Files: i18n formatter/provider/catalogs, authenticated boundary, session and preference APIs, new time-zone form, event page, MSW fixtures and Vitest/Playwright tests.

Interfaces: event formatter retains `(Date,string|null)=>string`; preferred source is explicit and falls back to the browser only when null. Account-specific nested provider derives from `/me`, so logout/account switching cannot retain a private preference. The form submits `{timeZone: string|null}` with If-Match and updates only the matching account cache.

- [x] Add failing literal pt-BR/en-US tests for generic+offset, March/November DST, date rollover, UTC, same zone and browser/saved labels.
- [x] Implement output using Intl parts, never parsing a localized date into an instant. Numeric offset is calculated for the event's instant. Preserve `short` for other timestamp consumers.
- [x] Add searchable native datalist with readable labels and supported IDs; device action only fills, Save persists, clearing is explicit; 412 preserves draft until explicit reload.
- [x] Add tests for no implicit write, invalid selection, saved/remove, account switch, locale switch and device fallback. Update browser literals independently for known Chromium/WebKit CLDR differences.
- [x] Run targeted Vitest, typecheck, lint, formatting and Playwright.

## Task 3: Preferred security-notice time

Files: `Core/Notifications/AccountEmails.cs`, account outbox, dispatcher, security notice enqueue, notification unit tests and email integration tests.

Interfaces: optional `preferredTimeZone` parameter on `SecurityNotice` and `CreateSecurityNotice`; additive optional payload field omitted when null. Existing bilingual UTC sentences, no-token rule, delivery windows and retry/lease behavior remain intact.

- [x] Add failing literal preferred-time tests for NY/SP/UTC in both languages and both November occurrences; default UTC tests remain unchanged.
- [x] Render preferred local time and explicit IANA/UTC offset alongside the original UTC reference, without new dependencies. Snapshot preference when enqueueing, not when delivering.
- [x] Validate the optional payload field; reject unsupported zones and zone data on unrelated email kinds. Old payloads without the field keep their existing behavior.
- [x] Test actual enqueue/dispatch and queued snapshot after preference change; document worker-before-web deployment of the additive payload reader.

## Task 4: Contract, documentation and full checks

- [x] Regenerate OpenAPI and SPA types and verify no mismatches.
- [x] Record approved UX in ADR/design/auth/data docs, including account privacy, null fallback and migration/rollback ordering.
- [x] Run locked restore, build, format, complete backend/frontend suites, production E2E and review the final diff. Add coverage to existing route inventories where required; never remove assertions or cases to make them pass.
- [ ] Commit, push and open a draft PR; attach it to this chat. Wait for all nine checks for the final head and investigate failures. No merge. Results of this external gate are recorded in the PR, not predicted here.
