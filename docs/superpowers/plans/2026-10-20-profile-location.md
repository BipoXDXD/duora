# Profile location — additive implementation plan

**Goal:** implement issue #78 option B without enabling additional countries.
**Architecture:** Profiles owns reference countries/subdivisions and nullable location columns. Preserve regionCode as a legacy adapter. Keep atomic version-conditional writes and authenticated self-only access (API-P01/P02, INV-05/06/12, T-19/21).
**Spec:** issue #78 and the user's implementation request; no precise location, new dependencies, deployment or country enablement.
**Status (2026-10-21):** additive stage completed and merged in [PR #110](https://github.com/BipoXDXD/duora/pull/110), commit `d6de23fe76d2dbe763e43d19ade4e4cf08c297e1`. [ADR-0030](../../architecture/adr/0030-profile-country-subdivision.md) is accepted for this stage. No deployment or production migration was performed; [issue #78](https://github.com/BipoXDXD/duora/issues/78) remains open for the follow-ups below.

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

## Outstanding work

- [ ] Product: approve launch countries and the permitted subdivision level for each country. Review the jurisdictional eligibility/minimum-age decision identified by issue #78 and J-05 of the data map; `age_band` was not changed.
- [ ] Catalog maintenance: choose the authoritative source, distribution license/version, update owner and cadence, translated labels, and policy for retired codes. Recommend reviewed versioned ISO snapshots while the list is small. The package-license CI result does not approve a future catalog source.
- [ ] Contract: retire old consumers, then propose a separate approved breaking-change PR to remove `region_code`, its CHECK, the compatibility trigger and the legacy API field. Retain the additive schema and data until then.
- [ ] Before deployment: measure lock/backfill/index duration with representative synthetic data and rehearse the documented transition/rollback. Do not run mixed old writers after activating the new SPA; returning to the old binary requires pausing profile edits and preserving the expanded schema/data.

BR + the existing 27 UFs remains the compatibility baseline. US or another country requires explicit product approval; 50 states + DC is only a recommendation if US is approved. No geolocation, city, address or inferred country.

## Verification ledger

2026-10-21: 1,448 unit + 30 architecture + 1,050 PostgreSQL integration tests passed (native xUnit, maxThreads 2; no skips). Frontend: 681 tests passed with maxWorkers 2. Browser: 130/132 initially passed; the two WebKit timeouts passed on a one-worker rerun. Builds, typecheck, lint, formatting, oasdiff and Gitleaks passed. No test limits or assertions were weakened. Initial full backend run was stopped after known failures to restart on the final tree: the table inventory and runtime privilege declarations were corrected; two setup 503s did not recur.

Initial local diagnostics (historical): Trivy reported 7 existing Pillow 12.3.0 findings (6 HIGH, 1 CRITICAL), and locally generated SBOMs lacked license metadata for 110 packages. The local Debug build warned about a missing SixLabors license; local Release/image builds and optional `e2e:real` were not run. No dependencies or policy exceptions were added by the location change.

Resolved integration status (2026-10-21): [PR #107](https://github.com/BipoXDXD/duora/pull/107) updated Pillow and was merged as `f5af8c2fe001a76046790bbf88a67b04243e7a38`. Its [post-merge main CI](https://github.com/BipoXDXD/duora/actions/runs/39550420781) passed all nine checks. PR #110 incorporated that main, preserved both API documentation sections when resolving the only conflict, and passed all nine PR checks in [run 39550445540](https://github.com/BipoXDXD/duora/actions/runs/39550445540). Local build, typecheck and contract regeneration also passed after integration.

The [main CI after #110](https://github.com/BipoXDXD/duora/actions/runs/39551321634), on `d6de23fe76d2dbe763e43d19ade4e4cf08c297e1`, passed backend, frontend, e2e, secret-scan, contract, supply-chain, ops, images (amd64) and images (arm64). The initial license-metadata failure did not reproduce in CI: remote license checks and Release/image builds passed. No gate is left blocked by those initial diagnostics. The main branch was synchronized locally with GitHub. Deployment and production migration remain unperformed, and the outstanding work above is not implied complete by the merges.
