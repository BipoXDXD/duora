# English routes implementation plan — issue #105

Goal: one canonical English SPA route tree with backward-compatible Portuguese links.
Architecture: central paths and explicit legacy aliases; replace navigation within the SPA preserves query, fragment and history state. Existing email-page hook consumes and erases credentials. Login return destinations are normalized against a closed route allowlist.
Spec: issue #105; ADR-0026; security/auth.md; INV-01/05; API-A01–A10; acceptance T-13/14/21. No endpoint/DTO, authorization, persistence, concurrency or locale-contract changes.

- [x] Task 1: Pin English route and legacy-link expectations in router/session tests; run failures. Implement aliases in AppRoutes and canonical paths; normalize internal login returns, reject external/ambiguous URLs, strip credential parameters and fragments from returns. Update existing tests to canonical expectations.
- [x] Task 2: Test then update AccountEmails destinations (confirmation, reset, registration, recovery, security notice); preserve numeric-code marker and legacy token encoding. Keep the unregistered historical age producer unchanged; alias its old browser return to account/selfie without reviving age verification.
- [x] Task 3: E2E matrix for aliases and canonical destinations, both locales, code and legacy confirmation, reset, protected return, URL/history/network/storage privacy, axe. Run Chromium, WebKit and mobile suite.
- [x] Task 4: Inventory and compatibility documentation; run frontend gates, backend gates, contract generation, secrets and available supply-chain checks. Record actual failures/limitations. Review diff and prepare human-review deliverables; no merge/deployment.

Review focus: encoded token round trips; code is entered manually and never placed in URL; URL normalization cannot escape origin; aliases do not bypass session/MFA/password guards; replacements do not preserve credential-bearing history entries. Repeated navigation uses existing pages and no new network redirects or telemetry.
Rollback: retain alias support for both URL sets even if emitters are rolled back; do not invalidate already sent English links. Release SPA compatibility before switching mail emitters. No data migration.

Validation ledger: rebased onto main 01c595b (PR #120); retained its time-zone behavior and resolved only
the auth documentation conflict. Frontend unit suite 739/739 (two workers), full browser suite 210/210
including 60 new compatibility cases. Typecheck/lint/format/build and OpenAPI equality passed.
Backend, security and real-backend browser evidence is recorded in the PR delivery report.
License policy execution fails because the local SBOM lacks license metadata for NuGet components;
no dependency/policy/exception changes were made. Human review and CI remain required.
Earlier parallel browser runs rebuilt the same bundle and caused two HTTP 404s; the final full suite
ran without concurrent rebuilds and passed without changing test timeouts or removing cases.
