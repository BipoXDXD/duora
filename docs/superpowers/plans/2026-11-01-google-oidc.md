# Google OIDC Implementation Plan

> For agentic workers: execute with superpowers:executing-plans; independent frontend work uses superpowers:dispatching-parallel-agents. No heavy checks without the coordinator's exclusive test window.

Goal: participant Google signup/login with local password/code retained, explicit dual-possession linking, local MFA and sessions preserved; default off.
Architecture: official OIDC handler stages validated proof in PostgreSQL; protected POSTs complete application operations. Identity user_logins owns issuer+sub association. Same-origin frontend never sees provider tokens.
Tech stack: .NET10/Identity/EF/Npgsql/PostgreSQL18.6, official OpenIdConnect10.0.13 (MIT), React/TypeScript.
Spec: [ADR-0046](../../architecture/adr/0046-google-oidc-com-senha-local.md); approved policy recorded by user on 2026-11-01. Concrete execution evidence and pending gates: [validation](../../development/google-oidc-validation.md).

## Global constraints
- Base6c397a34a19969e93148b7e5cdb760c508bbdec2; clone/branch only, no remote write/deploy/real secrets/data.
- No auto-link by email; provider fixed Google; verified issuer+sub; local confirmation and password retained.
- Privileged accounts remain local; local MFA and role separation/eligibility preserved.
- INV01/05/06/11/12; acceptance T01/06/13/14/15/21.
- Authentication recent is1hour inclusive; external flow TTL5minutes; code15minutes.
- No policy/workflow/gate weakening. All gates remain required for integration.

## Review focus
- Pre-registration followed by local replacement must not preserve attacker Google binding: tie pending link to registration stamp and confirm in same transaction.
- In-flight callbacks/login racing unlink/reset must not restore credentials: CAS flow stages and recheck stamp/link under account lock.
- Google login must not confer local step-up: ReauthenticatedAt remains null until password reauthentication.
- Cross-site OIDC callback must bypass only application CSRF at exact handler path, with state/nonce/PKCE validated by real handler.
- Disabled configuration must boot and generate contracts without credentials; no frontend login button when off.

## Task1: behavioral regressions and runtime foundation
Files: new Identity/GoogleOidcTests.cs, OIDC simulated server; Web.Identity/GoogleOidcAuthentication.cs; Infrastructure/Identity/ExternalAuthenticationService.cs; Contracts/Identity/ExternalAuthenticationContracts.cs; Web/Accounts/GoogleOidcEndpoints.cs.
- [ ] Write HTTP tests: off options and start denial, real handler invalid/valid token, no email-based login, explicit account link.
- [ ] Run RED in coordinator test window; preserve output.
- [ ] Implement durable flow state, official handler, endpoints and key validation; expand-only migration with unique provider key/per-user provider.
- [ ] Run GREEN and additional protocol/MFA/cookie/grant assertions.

## Task2: transactional lifecycle
Files: AccountService.cs, ExternalAuthenticationService.cs, audit and account notice catalogs/outbox; integration tests.
- [ ] Add RED tests for registration replacement/confirm/link atomicity, unlink/login/reset races and notification rollback.
- [ ] Implement shared confirmation transaction hook and register-bound stamp, explicit local step-up linkage, revocation and recovery behavior.
- [ ] Run GREEN on PostgreSQL real role/constraint/transaction tests; no InMemory/SQLite.

## Task3: usable frontend
Files: new google auth components/API, auth/account integration, routes/i18n and frontend behavioral tests.
- [ ] Write RED tests for disabled UI, POST start/navigation, continuation MFA/registration/confirmation, account link/removal and reauthentication.
- [ ] Implement using existing operation/context and session transition APIs; provider tokens absent from storage/URL/cache.
- [ ] Run GREEN, typecheck/lint/format/tests/build in reserved window.

## Task4: documentation, full validation and local handoff
Files: ADR0046 and auth/data/concurrency/acceptance docs; OpenAPI/schema generated; local handoff report and patch.
- [ ] Record accepted policy, dependency MIT/locked version, threats/contracts/rollback/default-off and operational limitations.
- [ ] Run backend restore/build/format/test plus appropriate frontend/e2e/contract checks without concurrent heavy work.
- [ ] Review whole branch independently; fix material findings with reproducing regression.
- [ ] Commit local branch and produce patch applying to base with exact test evidence; no push/PR/merge/activation.
