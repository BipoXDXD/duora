# Profile location — additive implementation plan

**Goal:** implement issue #78 option B without enabling additional countries.
**Architecture:** Profiles owns reference countries/subdivisions and nullable location columns. Preserve regionCode as a legacy adapter. Keep atomic version-conditional writes and authenticated self-only access (API-P01/P02, INV-05/06/12, T-19/21).
**Spec:** issue #78 and the user's implementation request; no precise location, new dependencies, deployment or country enablement.

## Execution

- [x] Add PostgreSQL/API regression tests: old records and version unchanged, optional country/subdivision, cross-country FK, unknown codes, index, old/new clients, stale ETags.
- [x] Add entities, reference tables, BR-only frozen migration seed and backfill. Keep legacy column. Forward-only rollback retains data. Old binary writes synchronized during expansion; no concurrent mixed binaries after new UI activation.
- [x] Add countryCode/subdivisionCode to reads and patches. A location replacement includes countryCode; omitted subdivision clears subdivision. Neither supplied preserves location. Reject subdivision without country field and mixing legacy/new fields. Legacy null clears location. Validation uses bootstrap BR catalog; not a final maintenance policy.
- [x] Regenerate OpenAPI and TypeScript. SPA sends the pair when either location selector changes, supports country only and no location, translates both languages, retains ETag conflict handling.
- [x] Run backend/frontend suites, formatting/build, contract diff and browser tests. Review diff and supply human-review patch/report with limitations and rollout/rollback instructions.

## Review focus

- Old client changing only bio must preserve country-only location.
- Clearing location must never resurrect a legacy UF.
- Migration must not invalidate outstanding ETags.
- Conflict reload must preserve untouched location fields.
- No US or other country seed; synthetic second country exists only in isolated integrity tests.

## Decisions deferred

Launch countries, catalog authority/update owner and cadence. Recommend BR-only compatibility baseline and reviewed versioned ISO snapshots while the list is small; US requires explicit product approval (50 states + DC only). No geolocation, city, address or inferred country.

## Verification ledger

2026-10-21: 1,448 unit + 30 architecture + 1,050 PostgreSQL integration tests passed (native xUnit, maxThreads 2; no skips). Frontend: 681 tests passed with maxWorkers 2. Browser: 130/132 initially passed; the two WebKit timeouts passed on a one-worker rerun. Builds, typecheck, lint, formatting, oasdiff and Gitleaks passed. No test limits or assertions were weakened. Initial full backend run was stopped after known failures to restart on the final tree: the table inventory and runtime privilege declarations were corrected; two setup 503s did not recur.

Supply-chain is not green: Trivy reports 7 existing Pillow 12.3.0 findings (6 HIGH, 1 CRITICAL) in tools/visual/requirements.txt; 110 existing packages have missing license metadata in locally generated SBOMs. No new dependencies or policy exceptions. Debug build warns about missing SixLabors license; Release/image builds not attempted. Human review and remaining CI gates required before merge. No deploy, push or merge performed.
