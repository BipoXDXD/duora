# Regional policy and G2 qualification implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` in this session. Execution and product decisions are explicitly delegated; do not ask for a generic approval. A tool's explicit human Submit remains mandatory for that operation.

**Goal:** ratify remaining product choices and prevent stale/incomplete CI evidence from authorizing an IPinfo rehearsal.

**Architecture:** keep functional publication pause in PR236. This separate policy change tightens the existing read-only G2 gate and evidence retention, adds no provider/credentials/runtime feature and records delegated choices with physical-proof limits.

**Tech Stack:** Python standard library, existing GitHub Actions G2 workflow, versioned Markdown policy/ADRs; normal hosted CI.

**Spec:** ADR0033/0037/0038, issue9/78/108 and the operational homologation plan; INV01/02/03/05/06/08/09/12.

## Global constraints

- IPinfo Lite only, existing 64 MiB / 10 million networks / 180 seconds / one CDN host profile.
- Secret remains in the protected job. No participant data, new age/document collection or country.
- Environment reviewer/main restriction/admin-bypass prohibition unchanged.
- No SSH, VPS activation, new service purchase or heavy Mac test.
- Never replace a required human Submit with a different path; preserve the already-created PR236 without duplication.

## Review focus

- Old successful SHA after main advances must refuse provider access.
- A rerun between run/jobs reads must not inherit a successful old attempt.
- Duplicate/extra jobs, incomplete response, skipped/failing jobs or a different workflow must refuse.
- Approval may wait: recheck source/CI after it, before the download step.
- Delegated decisions and artifact retention must not imply real SMTP/storage/clock/assistive/production proof.

## Task 1 — G2 metadata boundary

**Files:** scripts/geolocation/g2_environment.py, scripts/geolocation/test_g2_environment.py, .github/workflows/ipinfo-g2.yml.

- [x] Add five regression methods: duplicate/extra/incomplete jobs; wrong workflow/invalid identity; main drift; changed attempt; exact-attempt and two-main reads. Execute existing eight plus new tests: RED13 methods with nine failing subcases.
- [x] Validate positive integer run/attempt, ci.yml path, complete count and exactly nine unique successful checks. Read current main before/after, fetch explicit attempt jobs and re-read run identity.
- [x] Recheck gate in protected rehearsal after approval, using contents/actions read only; retain environment protections and secret isolation. Set sanitized JSON artifact retention90days.
- [x] Execute Python suite after the delta:13/13 PASS in0.002s; actionlint without ShellCheck and diff-check PASS. No .NET/Mac build.
- [ ] Commit/push frozen policy and request a separate PR. Stop PR creation if the tool requires human Submit; no fallback for that pending operation.
- [ ] Obtain normal hosted nine gates plus G2 synthetic preflight and independent review of the final head before integration.

## Task 2 — decisions and evidence

- [x] Record product choices in decisions-2026-10-24.md; preserve unresolved execution instead of asking again for choices already delegated.
- [x] Ratify ADR0038 design, accept ADR0040 contract for future Connections, and update scope-specific accessibility/retention/moderation records without runtime/auth/UI edits.
- [x] Review all78 BR/US label pairs and freeze hashes/provenance, preserving raw CLDR snapshot and existing country set.
- [x] Create regional-product-acceptance dossier; include UI author's PR237 with its exact limits and independently produced evidence.
- [ ] Bind the real new inventory/validation and subsequent operator receipts; do not mark G2/G3/G4/G5 or aggregators passed from policy alone.
