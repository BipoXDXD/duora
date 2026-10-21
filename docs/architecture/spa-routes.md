# SPA routes and legacy compatibility (#105)

One route tree serves PT-BR and EN-US. Locale changes labels and messages, never URLs. This implements the
single-route-set proposal in ADR-0026 without changing API endpoints, DTOs, authorization or storage.

## Inventory

| Previous path | Canonical path | Consumer / producer |
| --- | --- | --- |
| `/` | `/` | Public landing, unchanged |
| `/criar-conta`, `/cadastro` | `/register` | Registration; pending/unknown-address account emails |
| `/verifique-seu-email` | `/check-email` | Neutral registration result |
| `/entrar` | `/login` | Sign in, logout/session-expiry destination |
| `/confirmar-email` | `/confirm-email` | Current code confirmation and legacy confirmation email |
| `/esqueci-a-senha`, `/recuperar-senha` | `/forgot-password` | Recovery form; existing-account registration email |
| `/redefinir-senha` | `/reset-password` | Password-reset email |
| `/trocar-senha-inicial` | `/change-initial-password` | Restricted first-login password change |
| `/conta/ativar-mfa` | `/account/enable-mfa` | Required/optional MFA enrollment |
| `/conta` | `/account` | Account/session management, security-notice emails |
| `/conta/perfil` | `/account/profile` | Authenticated profile |
| `/conta/preferencias` | `/account/preferences` | Authenticated private preferences |
| `/conta/selfie` | `/account/selfie` | Authenticated presence verification |
| `/conta/verificacao-etaria` | `/account/selfie` | Historical browser return; never grants eligibility |
| `/events` | `/events` | Authenticated events, unchanged |
| `/rooms/:roomId` | `/rooms/:roomId` | Authenticated room, unchanged |

`frontend/src/shared/paths.ts` owns SPA destinations and exact aliases; `AppRoutes` registers aliases as
replace-only redirects, outside page/session trees. All canonical protected pages still pass through
`RequireSession`, including required password change and MFA. Unknown paths retain the existing 404.
Router case-insensitive/trailing-slash matching remains unchanged; arbitrary subpaths are not aliases.

`AccountEmails` is the active backend link producer. Both language catalogs consume its URLs, including
bilingual security notices. Outbox dispatch composes links through that class; already delivered messages
remain usable. The API responds with 401/403 instead of HTTP redirects to login. No proxy redirect is added.

The old `AgeVerificationDispatcher.ReturnPath` remains historical, unregistered code (ADR-0029). Its old
browser destination is an alias to presence onboarding, but retired API endpoints/callbacks keep their 410
behavior. A redirect never processes a provider result or revives age-based authorization. Other Portuguese
routes in the proposed information architecture are not implemented SPA routes and are not invented here.

## Credentials and returns

- Aliases preserve query, fragment and router state through a same-origin SPA `replace`; they never turn a
  fragment into a query or navigate to a target supplied by a URL parameter.
- Confirmation and reset pages consume `#userId=…&token=…` once, keep credentials in component memory and
  remove **both query and fragment** with another replace. Tokens only travel in the existing CSRF-protected
  POST body. Query-only credentials remain invalid; they are not promoted into the fragment.
- Current confirmation emails show the eight-digit code in the body and link with `token=code`, not with
  the numeric code. The form requires code plus registration password. Legacy emails retain their encoded
  token and password flow. Opening either URL does not submit confirmation. Existing backend expiry,
  single-use, attempt limits and code-versus-legacy-record checks remain authoritative.
- Login history state is untrusted. Returns are parsed against a fixed origin, normalized to a canonical
  known SPA path, and restricted to home/account/events and UUID room paths. Schemes, protocol-relative URLs,
  backslashes, controls and unknown/authentication paths are rejected. API authorization still checks rooms.
- Safe queries and simple section anchors survive login. Credential keys (`token`, `userId`, `code`,
  `password`, case-insensitive) are removed from return queries; complex fragments are discarded. The session
  guard applies this filtering before putting a return in history state. Login independently validates it.
  No return parameter in the login query is interpreted as a destination. Nothing is stored in local/session
  storage and no new telemetry is added.

## Acceptance and rollout

INV-01/05 and API-A01–A10 retain their contracts. The existing session-privacy suite covers T-14/T-21, and
CSRF/identity integration tests retain T-13. `legacy-routes.test.tsx`, `login-state.test.ts`, and `routes.spec.ts`
pin aliases, state, safe returns, both email flows, token encoding/erasure, history and both locales.
Existing authentication, MFA, account, profile and accessibility suites now exercise canonical routes.
E2E runs use synthetic API data; backend outbox and identity integration tests run against PostgreSQL.

Release the compatibility-capable SPA before switching backend/worker emitters. A rollback must retain both
English and Portuguese route support: a full rollback to an old SPA would break English emails already sent.
There is no data migration and no concurrent state transition change. No merge/deployment is automated.
Human review and normal CI gates remain required; axe is automated coverage, not a manual assistive-technology audit.
