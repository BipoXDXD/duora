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

## Approved decisions (2026-10-21)

The owner explicitly approved US and Brazil, ISO as the reference, CLDR 48.2 and Unicode-3.0. The dated addendum to [ADR-0030](../../architecture/adr/0030-profile-country-subdivision.md) preserves the original expansion decision and records this later approval.

- [x] Product scope: US with 50 states + DC (51 subdivisions), and BR with the existing 27 UFs; no territories or other countries. Country/subdivision remain optional.
- [x] Source and license: versioned snapshot from [CLDR release-48-2](https://github.com/unicode-org/cldr/releases/tag/release-48-2), checked against ISO 3166, under Unicode-3.0. License authorization and provenance are recorded separately in [catalog-licenses.md](../../security/catalog-licenses.md); approval is specific to the release, not a global license allowlist change.

**Runtime state:** BR + 27 UFs only. No country enablement, dataset import, schema/API/SPA change or deployment is authorized by these decision records. The first future implementation must preserve version/hashes/notices, validate the approved ISO subset and supply an auditable data inventory; package SBOMs do not automatically cover vendored data.

## Outstanding work

- [x] License policy: [PR #117](https://github.com/BipoXDXD/duora/pull/117) merged as `e3d399b66d48b58f19a6fdacfd3d50bf44e1f1e1` after all nine checks passed in [CI 39561009857](https://github.com/BipoXDXD/duora/actions/runs/39561009857). The exception is scoped to the reserved CLDR 48.2 identity; the future data component is not imported, and an unused-exception warning is expected until incorporation/inventory are implemented.
- [ ] Maintenance governance: appoint technical/editorial owners and confirm the update cadence. Monthly review remains a recommendation, not an approved assignment or schedule.
- [ ] Editorial review: review all PT-BR/EN-US labels before incorporation; the 78 Portuguese subdivision labels inspected in CLDR 48.2 are marked provisional. Preserve provenance for editorial changes and referenced historical codes.
- [ ] Jurisdictional eligibility/minimum age: resolve J-05 of the data map. Optional declared profile location is not proof of residence or eligibility; `age_band` was not changed.
- [ ] Future catalog implementation: only after explicit authorization, import the approved snapshot/subset, extend validation/contracts/generated client/form and tests, and implement inventory/hash/license-notice verification. Country activation and deployment are separate approvals; neither is part of this task.
- [ ] Contract: retire old consumers, then propose a separate approved breaking-change PR to remove `region_code`, its CHECK, the compatibility trigger and the legacy API field. Retain the additive schema and data until then.
- [ ] Before deployment: measure lock/backfill/index duration with representative synthetic data and rehearse the documented transition/rollback. Do not run mixed old writers after activating the new SPA; returning to the old binary requires pausing profile edits and preserving the expanded schema/data.

No geolocation, city, address or inferred country is introduced. The issue stays open for the work above; approving the product/catalog decisions alone does not complete activation, eligibility, contract or deployment.

## Verification ledger

2026-10-21: 1,448 unit + 30 architecture + 1,050 PostgreSQL integration tests passed (native xUnit, maxThreads 2; no skips). Frontend: 681 tests passed with maxWorkers 2. Browser: 130/132 initially passed; the two WebKit timeouts passed on a one-worker rerun. Builds, typecheck, lint, formatting, oasdiff and Gitleaks passed. No test limits or assertions were weakened. Initial full backend run was stopped after known failures to restart on the final tree: the table inventory and runtime privilege declarations were corrected; two setup 503s did not recur.

Initial local diagnostics (historical): Trivy reported 7 existing Pillow 12.3.0 findings (6 HIGH, 1 CRITICAL), and locally generated SBOMs lacked license metadata for 110 packages. The local Debug build warned about a missing SixLabors license; local Release/image builds and optional `e2e:real` were not run. No dependencies or policy exceptions were added by the location change.

Resolved integration status (2026-10-21): [PR #107](https://github.com/BipoXDXD/duora/pull/107) updated Pillow and was merged as `f5af8c2fe001a76046790bbf88a67b04243e7a38`. Its [post-merge main CI](https://github.com/BipoXDXD/duora/actions/runs/39550420781) passed all nine checks. PR #110 incorporated that main, preserved both API documentation sections when resolving the only conflict, and passed all nine PR checks in [run 39550445540](https://github.com/BipoXDXD/duora/actions/runs/39550445540). Local build, typecheck and contract regeneration also passed after integration.

The [main CI after #110](https://github.com/BipoXDXD/duora/actions/runs/39551321634), on `d6de23fe76d2dbe763e43d19ade4e4cf08c297e1`, passed backend, frontend, e2e, secret-scan, contract, supply-chain, ops, images (amd64) and images (arm64). The initial license-metadata failure did not reproduce in CI: remote license checks and Release/image builds passed. No gate is left blocked by those initial diagnostics. The main branch was synchronized locally with GitHub. Deployment and production migration remain unperformed, and the outstanding work above is not implied complete by the merges.
