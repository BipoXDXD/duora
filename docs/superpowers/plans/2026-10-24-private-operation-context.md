# Private Operation Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bind every private HTTP read/write and audio result to the session and intent that created it, including cookie changes without BroadcastChannel.

**Architecture:** Reuse ASP.NET antiforgery with additional session/purpose data; bootstrap a canonical context atomically with /me. Opaque handles and operation snapshots keep proofs outside caches, pin intent before async work and reject late responses. A page-wide bounded Web Audio coordinator replaces the private native audio URL without changing CSP.

**Tech Stack:** Existing .NET10/ASP.NET/Identity/PostgreSQL18, React19/TypeScript6/TanStack Query5/Zod4, browser Web Audio, Vitest/MSW/Playwright. No package, migration, service or credential added.

**Spec:** ../specs/2026-10-24-private-operation-context.md. The expiry-warning spec is a DEPENDENT second plan, not implemented by this plan.

**Execution:** Native in this chat with superpowers:executing-plans, small local commits and independent e3/root review. No fresh subagent per task, provisional PR, workflow change, Mac SDK/Docker or production activation. Spec4dd7ec4 was accepted by root and independent reviewers; the factual corrections below do not change product MFA policy. This plan itself requires review before product code.

## Global Constraints

- Participant idle30min/absolute12h, administrative idle15min/absolute8h remain unchanged.
- No UserId/SessionId/proof in headers of identity, URLs, logs, telemetry, storage or BroadcastChannel. Existing X-XSRF-TOKEN carries only the protected framework request token.
- Canonical UserId+SessionId+phase exists only in private no-store bootstrap bodies. SessionId is the existing non-bearer identifier; never accept it from a client as authorization.
- Capture immutable owner/generation/proof before the FIRST await and keep it through all body/error parsing. A new owner cannot inherit an old DTO.
- Same tuple/phase/auth view preserves the active handle. New SID/phase/authorization view or explicit protection invalidates. Protected state only exits with an explicit click and its new epoch.
- Webhooks preserve their existing HMAC exemptions and closed route tests. Public auth has six explicit POST routes; authenticated logout is private and anonymous logout remains public/idempotent.
- Private GET/HEAD require explicit ValidateRequestAsync, not IsRequestValidAsync which skips safe methods.
- CSP media-src 'self' and the test blocking blob/data media remain intact. No native unbound audio fallback or service worker.
- WAV PCM16 canonical:6MiB/60s,1–2channels,8–48kHz; effective AudioContext sample rate at most48kHz; final PCM at most23.040.000B.
- Page-wide reservation:4×6.291.456 +2×23.040.000 =71.245.824B under72MiB counted representations. One cached PCM included; one decoder across ALL trees/epochs until its finally, no unbounded queue. This is not a browser RSS limit.
- Proofs and secrets stay out of Me/mutation caches; cached values may contain an opaque owner reference with no token/identity fields. Error objects never contain the raw bootstrap proof/body.
- Root owns the queue, operator69 owns the host. No main merge, SSH, flag, transport, country or service activation in these tasks. Branch421 Submit remains a separate blocked operation.
- Working directories: Node commands run in the NEW checkout's frontend directory; git commands run in that checkout's ROOT as separate tool invocations. Do not execute a mixed block from one directory. Frontend dependencies may be COW-copied from our own243 checkout; never mutate another author's dependency folder or install merely for planning.

## Review Focus

- Same-user replacement session and same-SID privilege/phase changes: task2/3 compare canonical binding/auth view and invalidate correctly; ordinary focus refetch does not reset forms.
- Automatic or out-of-order /me while protected: task2/3 never unlocks or adopts a proof; only a live explicit ticket can install a new owner.
- A callback awaiting validation when cookie/owner changes: task2/3 captures its operation before that await, emits zero POST after known invalidation and never reads a new cookie token.
- A decoder completing after its component unmounts: task4 keeps its PAGE slot occupied, rejects a second decode and discards the result without sound/cache updates.
- MFA success creates a new session while returning codes once: task5 checks original snapshot, resultSessionId, explicit transition epoch and fresh /me before displaying codes in the new tree; mismatches show an explicit failure without replay.

---

## Files and boundaries

Server additions: `backend/src/Duora.Web/Security/PrivateOperationContext.cs` (purpose metadata/provider/issuer), `backend/tests/Duora.IntegrationTests/Identity/PrivateOperationContextTests.cs` (causal HTTP/PG cases).
Server edits: `DuoraWebApp.cs`, `Security/CsrfProtection.cs`, `Security/SessionAuthorization.cs`, `Accounts/{MeEndpoints,AuthEndpoints,MfaEndpoints}.cs`, `Duora.Contracts/Identity/IdentityContracts.cs`.
Test fixture edits: `backend/tests/Duora.IntegrationTests/Identity/{Browser,IdentityScenario,MfaScenario}.cs` and existing CSRF/bootstrap/Me/secret-leak tests only where the contract requires it. Never repair an intentionally absent/stale proof in a negative test.

SPA additions: `shared/session-context.ts` (pure state/opaque ownership), `shared/private-audio.ts` (one coordinator/page), `shared/pcm-wave.ts` (bounded canonical preflight), `features/session/PrivateOperationProvider.tsx`, `features/participation/PrivateAudioPlayer.tsx`, `features/auth/security-result.ts` (one typed result handoff). Each has a matching test file.
SPA edits: central API/client/error helpers; `features/session/{session-api,session-hooks,RequireSession}.tsx/ts`, `app/{App,query-client}.tsx/ts`, private API modules/pages listed in task3, MFA/password pages in task5 and existing PT-BR/EN-US catalogs.
Contract/docs: `docs/api/openapi.json`, generated `frontend/src/shared/api/schema.d.ts`, `docs/security/auth.md`, new `docs/testing/private-operation-context.md`, `frontend/e2e/session-context.spec.ts`.

### Task 1: Server binding and complete contract

**Consumes:** Current DuoraClaims.ReadUserId/ReadSessionId/ReadSession, IAntiforgery, existing authorization metadata and ApiProblems.
**Produces:** Public/private purpose metadata, PrivateSessionContextResponse(UserId,SessionId,Phase,RequestToken), MeResponse.SessionContext, RecoveryCodesResponse.ResultSessionId. Only /me200 and its specific password-change403 issue a private proof.

- [ ] **1. Compose the actual base in a NEW own checkout before product edits.** Record current main and coordinator disposition. Preserve literal ancestors `d1b8b039b1170f693bbd59e6f0d3c78bec83cb76` (#237), `a6d22e2e155912c8084b6798ba6fd78056f553f4` (#241), `2472c001c17c2a1e3b8a720079f862bc00f73193` (#243). If already in main, reuse ancestry; otherwise compose their frozen heads locally only and resolve conflicts with owners. Copy the reviewed final docs without editing their source branches. No push/CI for this composition alone.

```bash
git merge-base --is-ancestor d1b8b039b1170f693bbd59e6f0d3c78bec83cb76 HEAD
git merge-base --is-ancestor a6d22e2e155912c8084b6798ba6fd78056f553f4 HEAD
git merge-base --is-ancestor 2472c001c17c2a1e3b8a720079f862bc00f73193 HEAD
git diff --check
```

- [ ] **2. Write causal integration tests FIRST.** Two confirmed PendingEligibility accounts and two Browser cookie jars; capture A's private proof then replace auth cookies with B's while preserving the old proof. Both GET time-zone and PUT time-zone return403 before handler effect; no private body of B and persisted B time-zone/version unchanged. Same user/new SID also rejects; same session succeeds. Test GET/HEAD, bootstrap200, password-change403 only, missing/empty/invalid version, public proof on private and private proof on public, known HMAC exemptions and anonymous logout. Preserve existing negative CSRF cases.

```csharp
var beforeB = await ReadPersistedTimeZoneAsync(accountB.UserId);
using var read = await browser.GetAsync("/api/v1/me/time-zone", privateProofOverride: proofA);
Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
using var write = await browser.PutJsonAsync("/api/v1/me/time-zone",
    new { timeZone = "America/Sao_Paulo" },
    new MutationHeaders(CsrfTokenOverride: proofA,
        Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{beforeB.Version}\"" }));
Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
Assert.Equal(beforeB, await ReadPersistedTimeZoneAsync(accountB.UserId));
```

Define `ReadPersistedTimeZoneAsync(Guid)` in this test with the real table/fields used by TimeZoneStore:

```csharp
private async Task<(string? Zone, long Version)> ReadPersistedTimeZoneAsync(Guid userId)
{
    await using var connection = new NpgsqlConnection(postgres.ConnectionString);
    await connection.OpenAsync(TestContext.Current.CancellationToken);
    await using var command = new NpgsqlCommand(
        "select preferred_time_zone,time_zone_version from identity.users where id=$1", connection);
    command.Parameters.AddWithValue(userId);
    await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
    Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
    return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
}
```

Browser gains GetAsync's explicit override and PutJsonAsync delegating to existing SendAsync(HttpMethod.Put, path, JsonContent.Create(body), headers). No backend RED result is claimed before a hosted run. Root prohibits provisional pushes and Mac SDK; write tests before implementation, run Node RED locally where applicable and execute all PG cases in the final necessary hosted CI.

- [ ] **3. Implement purpose provider, issuer and bootstrap DTOs.** AdditionalData version1 contains purpose+canonical session ID or public anonymous marker inside framework protection. Validate against current authenticated principal; malformed/empty/unknown version fails. No signing/hash/lifetime implementation. Register after AddAntiforgery so the custom interface implementation is used. Mark explicit public auth operations, conditional logout, bootstrap /me and private routes; no client-selected prefix grants an exception.

```csharp
public sealed record PrivateSessionContextResponse(
    Guid UserId, Guid SessionId, string Phase, string RequestToken);
public sealed record MeResponse(
    Guid Id, string Email, string Status, bool EmailConfirmed, bool MfaEnabled,
    IReadOnlyList<string> Roles, PrivateSessionContextResponse SessionContext,
    string? PreferredLocale = null, string? PreferredTimeZone = null);
public sealed record RecoveryCodesResponse(
    IReadOnlyList<string> RecoveryCodes, Guid ResultSessionId);
```

SessionContext is required BEFORE the two existing optional constructor parameters; placing a required
parameter after them is invalid C#. Update positional construction at MeEndpoints accordingly.

The private issuer calls GetAndStoreTokens once, under private issuance purpose, and returns its request token only in the body. It does not append XSRF-TOKEN. /auth/csrf and existing login/logout issuance stay public. Avoid private issuance in a renewal response that already generated a public token. Me200 uses canonical phase (`active`/`mfa-enrollment`); only GET/me PASSWORD_CHANGE_REQUIRED403 adds the typed context with `password-change`. No other403/401/503 contains it. No permission is widened.

- [ ] **4. Guard private reads and writes after authorization and before handlers.** Preserve Origin/FetchMetadata protection on unsafe requests. On private safe requests explicitly await ValidateRequestAsync and catch only AntiforgeryValidationException into the existing neutral403 code. Keep /me bootstrap, public auth, assets/health and HMAC exemptions explicit and closed.

```csharp
try { await antiforgery.ValidateRequestAsync(context); }
catch (AntiforgeryValidationException)
{
    await ApiProblems.Problem(403, ApiProblems.CsrfValidationFailed).ExecuteAsync(context);
    return;
}
await next(context);
```

MfaEndpoints builds ResultSessionId from CurrentSession.Require AFTER successful renewal for confirmation and from the unchanged current session for regeneration. Do not rotate/adopt stamp in RegenerateRecoveryCodesAsync; do not alter MFA service policy.

- [ ] **5. Update Browser deliberately, inspect source and commit.** Browser stores a private proof from explicit bootstrap200/typed403 and chooses it for private operations; public operations use their public proof. It does NOT obtain a fresh private proof transparently per request, so adversarial cookie swaps retain the captured stale token. Add `GetAsync(string path,string? privateProofOverride = null)` and retain a raw path for tests intentionally lacking proofs. Explicit overrides and SendCsrfToken=false always win. Existing post-login fixture helpers explicitly bootstrap once. Initial-password fixtures accept only the specific typed403. Normal tests must not gain immunity to stale contexts from a fixture refresh.

```bash
git diff --check
git add backend/src backend/tests/Duora.IntegrationTests/Identity
git commit -m "feat: bind private HTTP context to the login session"
```

### Task 2: Pure owner/operation state and HTTP body boundary

**Files:** create `frontend/src/shared/session-context.ts` and `.test.ts`; edit `shared/api/{api-client,index,antiforgery-refresh,api-errors}.ts` and their tests.
**Consumes:** Validated canonical bootstrap values and request/body completion events.
**Produces:** Opaque OwnerHandle/OperationHandle/BootstrapTicket/RevalidationTicket; no public token getter and no identity fields on handles.

```ts
type BootstrapSource = { readonly kind: 'automatic' } |
  { readonly kind: 'explicit'; readonly ticket: RevalidationTicket }
interface SessionContextStore {
  beginBootstrap(source: BootstrapSource): BootstrapTicket
  acceptBootstrap(ticket: BootstrapTicket, value: ValidatedBootstrap): OwnerHandle
  completeOwnerInstallation(epoch: number, owner: OwnerHandle): void
  begin(owner: OwnerHandle): OperationHandle
  assertCurrent(operation: OperationHandle): void
  finish(operation: OperationHandle): void
  protect(reason: 'identity' | 'context' | 'security-transition'): void
  beginExplicitRevalidation(): RevalidationTicket
  subscribe(listener: () => void): () => void
  getSnapshot(): ContextSnapshot
}
interface ValidatedBootstrap {
  readonly userId: string; readonly sessionId: string
  readonly phase: 'active' | 'mfa-enrollment' | 'password-change'
  readonly requestToken: string
  readonly authView: { readonly status: string; readonly emailConfirmed: boolean;
    readonly mfaEnabled: boolean; readonly roles: readonly string[] } | null
}
interface ContextSnapshot {
  readonly protected: boolean; readonly owner: OwnerHandle | null
  readonly pendingOwner: OwnerHandle | null; readonly transitionEpoch: number
}
declare function createSessionContextStore(): SessionContextStore
```

Define each opaque handle as a separate branded NON-PLAIN class instance with no enumerable/serializable identity/token fields; internals live in WeakMaps. Do not represent owners as empty plain objects that replaceEqualDeep may merge. ValidatedBootstrap's roles are sorted/deduplicated before comparison. authView is null ONLY for the typed password-change403 (the restricted body does not expose Me account fields);200 requires all account/auth fields and any other missing value rejects the bootstrap. A change between restricted/normal also changes phase and invalidates. ContextSnapshot exposes protected/current/pending-owner state and local transition epoch, never a proof/identity. Production API receives the store through dependencies; tests use fresh isolated stores. Proof access stays inside the store-to-client adapter, never in a cache/variable DTO/error string. JsonRequest gains optional opaque OperationHandle; central requestJson/sendCommand enforce it for private routes. ApiClient.fetchFor(operation) returns an ApiFetch bound to that snapshot for any generated client; unbound fetch is bootstrap/public only. A dedicated readMeBootstrap(BootstrapTicket) is fixed to GET/me and returns its status/raw body ephemerally for the session parser, including the specific403; it is not an arbitrary private-path bypass.

- [ ] **1. Add RED tests for stable same-context refetch, different SID/phase/auth view, explicit protection and late body/error.** Test accepts an automatic /me response while protected and expects AbortError, not unlock. Start an operation, pause a promise before sending, change owner and assert zero POST. Parse late JSON and late ProblemDetails after change and assert no callback/cache write. Same tuple with a new random token preserves owner; the earlier operation retains its original token.

```ts
const operation = store.begin(ownerA)
store.protect('identity')
expect(() => store.assertCurrent(operation)).toThrow(expect.objectContaining({ name: 'AbortError' }))
expect(store.getSnapshot().protected).toBe(true)
expect(JSON.stringify(operation)).not.toContain('request-token-A')
```

- [ ] **2. Run only the new/changed lightweight tests on Mac.** Use existing COW-isolated node_modules and a512MiB heap, one worker, no install/build/browser.

```bash
NODE_OPTIONS=--max-old-space-size=512 node_modules/.bin/vitest run src/shared/session-context.test.ts src/shared/api/api-client.test.ts src/shared/api/antiforgery-refresh.test.ts --maxWorkers=1
```

Record the actual RED assertions; a missing import alone does not prove the causal case. Add tests in two passes when the module first needs a minimal executable shell.

- [ ] **3. Implement generation, opaque snapshots and response checks.** Same tuple/fase/auth view updates the proof for future operations only. Explicit protection invalidates even the same tuple; automatic bootstrap never releases it. Each operation owns its combined caller/generation AbortSignal and snapshot from before first await; assert both before request and after body/error parsing. Use AbortError on invalidation, preserving shouldRetryQuery and never wrapping it in ApiNetworkError.

Changed/initial bootstrap acceptance returns a PENDING owner and no active owner. The pure store only
activates it through completeOwnerInstallation with the matching captured epoch/handle; unit setup
must explicitly acknowledge this boundary. Test begin(pendingOwner) rejects before acknowledgment.
Task3 supplies the real UI-unmount/cache acknowledgment; the shared store itself imports no UI/cache.

```ts
store.assertCurrent(operation)
const response = await sendOwnedRequest(operation, request)
store.assertCurrent(operation)
const body: unknown = await response.json()
store.assertCurrent(operation)
return body
```

Define sendOwnedRequest in the API adapter, with proof lookup from the immutable operation internals; it attaches X-XSRF-TOKEN for private GET/HEAD as well as writes. Arbitrary caller headers cannot replace it. Safe public/bootstrap operations have explicit API methods. Existing fresh-CSRF helpers remain PUBLIC ONLY and reject private paths; they never acquire a current global owner for an old request.

- [ ] **4. Rerun the directed tests GREEN, typecheck/lint changed files, commit.** Add a serializer/cache spy proving no proof/raw bootstrap body enters query data, mutation variables or ApiProblemError. Verify normal network errors still retry and AbortError does not.

```bash
NODE_OPTIONS=--max-old-space-size=512 node_modules/.bin/vitest run src/shared/session-context.test.ts src/shared/api/api-client.test.ts src/shared/api/antiforgery-refresh.test.ts --maxWorkers=1
node_modules/.bin/tsc --noEmit -p tsconfig.app.json
git diff --check
git add frontend/src/shared
git commit -m "feat: pin private operations through response parsing"
```

### Task 3: Bind private trees, queries and every API consumer

**Files:** add `features/session/PrivateOperationProvider.tsx`/test; edit `session-api.ts`, `session-hooks.ts`, `RequireSession.tsx`, `app/App.tsx`, `app/query-client.ts`; `features/{profile/profile-api,account/selfie-verification-api,preferences/preferences-api,preferences/regional-preferences-api,preferences/time-zone-api,preferences/region-detection-api,participation/participation-api}.ts`; their actual consuming pages/forms, plus session lists/account context.
**Interfaces:** PrivateOperationProvider receives an opaque owner. `usePrivateOwner():OwnerHandle` returns that render's owner, not a mutable global. Each private API function's FIRST parameter becomes OperationHandle; public auth functions remain separate. Me returned to QueryCache has the existing public account fields plus only opaque `owner`, never SessionContext's proof/SID. ContextSnapshot gains `pendingOwner:OwnerHandle|null` and `transitionEpoch:number`. `completeOwnerInstallation(epoch:number,owner:OwnerHandle):void` activates only the still-current pending owner after the UI adapter acknowledges old-tree unmount and private-cache cleanup; other epochs/owners reject. Shared core does not import QueryClient or features.

- [ ] **1. Add RED DOM/transport tests before changing call sites.** Initial Me without context blocks private UI; typed password403 enables only its restricted form. Same-owner focus refetch retains typed fields/QR. Replace cookie without Broadcast then return403 to GET: old tree hides, no B data/query key/toast, no automatic /me until click. A submit handler paused in async validation must have captured its operation before the pause, so owner change emits zero POST. With a REAL QueryClient, cache otherwise identical Me fields for same user with ownerA then new-SID ownerB and assert exact ownerB identity survives. Seed a same-user private query key with A data and batch MeB/owner notifications: B's Outlet must not mount/observe that cache before unmount-ack and cleanup.

```ts
const operation = store.begin(ownerFromThisRender)
await validatePendingFile()
await updateOwnProfile(operation, baseVersion, validatedDraft)
// Test switches owner while validatePendingFile is pending; updateOwnProfile rejects before fetch.
```

- [ ] **2. Add dedicated bootstrap parsing.** `fetchMe` begins its BootstrapTicket before await, parses the canonical context and normal Me body together and checks outer id matches context UserId. For200, return `{ ...accountWithoutContext, owner }` atomically. For typed password403, throw a specific restriction error carrying only opaque owner/code; retain proof in the store, not in QueryCache's error. Other403/401/503 follow existing typed errors without a context. No shared 'last /me' slot.

Set `structuralSharing:false` for the Me query, its query defaults and explicit setQueryData path BEFORE creating that query, so opaque owner changes cannot be discarded when visible account fields are equal. Keep this setting local to the Me key. Test the actual cache behavior rather than asserting a fake store. Same-owner refetch still reuses its owner by the store's canonical comparison and does not reset forms.

RequireSession verifies its cached Me/error owner matches the ACTIVE owner before mounting an Outlet.
Changed bootstrap acceptance installs only a PENDING owner, sets active owner null and aborts old
operations. It does NOT immediately activate B when bodyB arrives. The boundary commits a neutral
branch with old Outlet/QR unmounted, then its layout/effect adapter cancels/removes private queries
and mutation entries except Me and calls completeOwnerInstallation with the matching epoch/owner.
Only then B's provider/Outlet and queries may mount. Me/error owner and pending owner must match
before acknowledgment; a superseding bootstrap/protection aborts it. This two-phase barrier also
prevents batched React notifications mounting B with A's same-user cache key before cleanup. Begin
operation rejects inactive/pending owners, so a leftover observer cannot issue network during the
barrier. No side effect in render. Same-owner refetch bypasses this barrier. Remove lastRestriction
behavior that keeps A's QR while unrelated identity verification is pending.

The acknowledgment callback captures epoch/pendingOwner at the neutral render, not from a later
global read. Recheck both before cache side effects, after any awaited cancellation and before final
activation; a stale callback must neither clear a newer owner's cache nor activate a superseded owner.
Add a test where acknowledgmentB waits on cancellation, C supersedes/activates, then B resumes: C's
cache and active tree survive unchanged and B never mounts.

- [ ] **3. Route all private API calls through explicit snapshots.** Queries begin an operation at queryFn entry, before await, and finish in finally. Submit handlers bind the owner of their render and begin before any async validation. Pass only opaque operation references as intent plumbing; never copy proof/SID to mutation variables. Do not obtain a new owner from global state inside a resumed callback. At every API module change, run its causal tests and update only the affected callers.

```ts
const operation = store.begin(owner)
try { return await fetchOwnProfile(operation, signal) }
finally { store.finish(operation) }
```

Inventory confirmed consumers: ProfilePage, AccountPage/SessionList, SelfieVerificationPage/SelfieCapture, AccountTimeZoneForm, PreferencesPage, RegionalPreferenceForm, RegionSuggestion, EventsPage, RoomPage/RoutesGamePanel, private password/MFA calls in auth-api. Preserve selfie disabled/410 behavior and current country/permission flags. Initial bootstrap/public auth never borrows a private proof; logout selects private when authenticated and public only when already anonymous.

- [ ] **4. Make protection quiescent.** QueryClient handlers call protect on private context403 and suppress automatic invalidate/resetQueries/refetch while protected. Disable Me/private observers/focus resume during that state. Explicit button creates a RevalidationTicket, fetches Me with that exact ticket and installs the paired opaque owner+body; stale automatic responses cannot release protection. Network/5xx keeps protection,401 uses existing sanitized login return with fixed tab=details accepted. Broadcast invalidates/aborts before old UI/cache can survive; it carries no data.

```ts
const ticket = store.beginExplicitRevalidation()
const me = await fetchMeForRevalidation(ticket)
queryClient.setQueryData(meQueryKey, me)
```

Define fetchMeForRevalidation in session-api using the same parser as fetchMe, with a ticket parameter rather than a mutable global flag. Add tests for two overlapping explicit clicks and an earlier automatic response; only the current ticket wins.

- [ ] **5. Run focused DOM/API tests, then one justified full frontend suite after all consumers are routed; commit.** Do not repeat unchanged239/237 tests separately. Existing MSW Me factories gain canonical synthetic contexts; negative tests intentionally omit them. Do not add a global MSW auto-repair that hides missing/mismatched contexts.

```bash
NODE_OPTIONS=--max-old-space-size=512 node_modules/.bin/vitest run src/features/session src/app/query-client.test.ts src/features/profile src/features/preferences --maxWorkers=1
node_modules/.bin/tsc --noEmit -p tsconfig.app.json
git diff --check
git add frontend/src
git commit -m "feat: bind private UI and API consumers to their owner"
```

### Task 4: Page-wide bounded private audio without CSP changes

**Files:** new `shared/{pcm-wave,private-audio}.ts`/tests and `features/participation/PrivateAudioPlayer.tsx`/test; edit RoomPage and PT/EN catalogs. Reuse Button/range styles, descriptions and transcripts; preserve #237's literal ancestor/reflow/focus hook.
**Interfaces:** `inspectCanonicalPcm(bytes:Uint8Array):PcmDescription` returns channels/rate/frames/duration after all bounds. `PrivateAudioCoordinator.acquire(operation):AudioLease` reserves the PAGE slot; `AudioLease.dispose():void` stops/disconnects/closes and marks canceled, but only decoder finally releases a decoding slot. `loadOwnedAudio(operation,path,lease):Promise<OwnedAudioBuffer>` uses bounded API byte reads and verifies current operation before returning. Context/proof/identity never goes into the URL.

- [ ] **1. Write RED pure/DOM tests with injected fake audio/fetch.** No gesture => zero requests/context creations. Invalid Content-Length, streaming body over6MiB, malformed PCM,>60s,>2channels,>48k real context all fail before decode. Queue a deferred decode; dispose old tree/epoch; acquire from a new tree must fail busy until old decode settles. Its late result never starts sound or installs cache. Two quick cues cannot allocate/decode concurrently.

```ts
const first = coordinator.acquire(operationA)
const pending = loadOwnedAudio(operationA, audioPath, first)
first.dispose()
expect(() => coordinator.acquire(operationB)).toThrow('audio-busy')
decoder.resolve(fakeBuffer)
await expect(pending).rejects.toMatchObject({ name: 'AbortError' })
expect(output.start).not.toHaveBeenCalled()
```

- [ ] **2. Run the new tests RED, then implement bounds and the singleton coordinator.** One preallocated6MiB collection buffer with a checked offset; release chunks after copying, no unlimited chunk array. Reserve both encoded copies and PCM/copy headroom from the72MiB ledger before calling decode. Header preflight mirrors actual RIFF/fmt/data canonical layout and PCM16 constraints; do not decode compressed/audio-tag inputs. Cache one current PCM included in the ledger. No unlimited queue; busy state requires a new gesture. Decode's finally owns slot release even if the component is gone.

```ts
const MAX_INPUT = 6 * 1024 * 1024
const MAX_PCM = 48_000 * 60 * 2 * 4
const RESERVED = 4 * MAX_INPUT + 2 * MAX_PCM
if (RESERVED > 72 * 1024 * 1024) throw new Error('audio-budget')
```

Create/resume AudioContext synchronously from the gesture, request48kHz and reject actual rate above48kHz before decode. Validate the returned buffer dimensions too. Cancellation stops outputs immediately; decode may finish, but its result is discarded and references released. Do not claim native/GC RSS enforcement.

- [ ] **3. Replace the unbound native source with the owned player.** Play/pause/restart, mute initially true, explicit sound toggle, keyboard seek/progress and stable focus; description/transcript remain available on unsupported audio. No autoplay/preload/network before gesture, no raw src fallback, no Blob URL, no new CSP directive. Use a polite static error/status announcement; progress ticks do not announce.

- [ ] **4. Run directed audio/Room DOM tests GREEN, type/lint, commit.** Add hosted browser tests for keyboard controls and one real canonical short WAV under the existing Caddy CSP, plus the unchanged blob/data rejection probe. Playwright runs in existing CI, not on Mac. The fake decoder test covers lifecycle; do not claim it measures browser memory/physical AT.

```bash
NODE_OPTIONS=--max-old-space-size=512 node_modules/.bin/vitest run src/shared/pcm-wave.test.ts src/shared/private-audio.test.ts src/features/participation/PrivateAudioPlayer.test.tsx --maxWorkers=1
git diff --check
git add frontend/src
git commit -m "feat: play private audio through a bounded owned fetch"
```

### Task 5: Explicit credential transitions and unique security result handoff

**Files:** new `features/auth/security-result.ts`/test; edit auth-api, MfaEnrollmentPage, ChangePasswordPage, session transitions and tests. No AccountSecurityService policy/outbox/transport edits.
**Interfaces:** A typed security result store holds at most ONE pending `mfa-enrollment` or `recovery-codes` result in memory, outside query caches. `stageSecurityResult(sourceOperation,kind,payload,expectedUserId,expectedSessionId):SecurityResultTicket` ATOMICALLY asserts the original snapshot, creates a unique owned transition epoch/ticket, transfers the minimal result into that ticket, invalidates other operations and starts protection/unmount. There is no intermediate generic protect() call that would clear its own new result. Only this specific validated entry owns that result; a subsequent/external protection clears it. `completeSecurityResult(ticket,validatedMe):OwnedSecurityResult` requires that exact current transition epoch and expected owner; UI may show it only when that owner passes task3's activation barrier. Mismatch discards with an explicit uncertain-completion notice. No general endpoint/epoch bypass or 'ignore next protection' flag.

- [ ] **1. Add RED tests before transition edits.** ConfirmMFA returns resultSessionIdNew with codes; pause /me; neither old tree nor generic mutation callback may publish codes. Me exact UserId+SIDNew+phase succeeds in the new owner view. Me another user OR same user/different SID, stale transition ticket, late JSON, failed body/schema, or automatic Me => discard and an explicit notice, no codes/toast/query variables. Do not replay confirmation/regeneration. A failed receipt may mean the server completed; offer only an explicit later regeneration with normal proofs.

Also test the lifecycle contradiction directly: the ATOMIC own transition preserves its minimal staged
result while creating protection; a second/external protection immediately clears it, and the later
matching /me cannot restore it. No write is replayed in either case.

```ts
const ticket = stageSecurityResult(opA, 'recovery-codes', codes, userA, resultSessionId)
expect(oldTree.queryByText(codes[0])).toBeNull()
expect(() => completeSecurityResult(ticket, meOtherSession)).toThrow('security-result-owner')
expect(resultStore.current()).toBeNull()
expect(replayConfirmation).not.toHaveBeenCalled()
```

- [ ] **2. Implement narrowly ordered handoff.** Parse the success body and validate the ORIGINAL operation. Atomic stageSecurityResult performs the owned transition entry described above; it preserves only the result transferred to its fresh exclusive ticket while protecting/unmounting old UI. Read /me using that ticket; exact expected owner and live epoch validate the new context. Only after task3's unmount/cache/activation barrier may the typed result view mount under the new provider, before normal navigation. Remove current setRecoveryCodes inside mutationFn of the old tree. Clear staged result on mismatch/error/navigation or ANY SUBSEQUENT/EXTERNAL protection; the initial owned entry is not a separate clear event. No silent loss, persistence or generic retry. Original parsing never skips its epoch check because the endpoint is MFA, and a stale original operation cannot enter a transition.

- [ ] **3. Cover all actual transitions from the independent inventory.** Login/logout, effective password change, effective MFA disable/confirm create or end sessions as implemented. MFA already disabled204 does not demonstrate renewal. Reauthentication retains SID and absolute limit; a successful explicit proof change may invalidate old intents without falsely declaring expiry. Start/restart MFA changes/adopts stamp on the SAME SID: discard old QR and use only the current explicit result, confirmed against original SID/fase. Regenerate recovery codes retains SID WITHOUT adopting/changing stamp: local secret/result lifecycle still invalidates prior codes. Revoking another session refreshes the list without resetting current forms. Public password reset revokes sessions and creates no login. Preserve all server authorization/fresh-proof rules.

- [ ] **4. Run directed MFA/password/session tests GREEN, commit.** Verify payload secrets/proofs never enter QueryCache, mutation variables, error details or logs, and two overlapping security actions cannot adopt each other's response. Add hosted confirmation/new-SID/ResultSessionId and regeneration/same-SID tests to existing real PG suites; do not claim they ran until CI returns.

```bash
NODE_OPTIONS=--max-old-space-size=512 node_modules/.bin/vitest run src/features/auth/security-result.test.ts src/features/auth/MfaEnrollmentPage.test.tsx src/features/session/session-hooks.test.tsx --maxWorkers=1
git diff --check
git add frontend/src/features/auth frontend/src/features/session
git commit -m "feat: hand off security results only to their confirmed owner"
```

### Task 6: Final contract, evidence and one normal qualification

**Files:** docs/api/openapi.json, frontend/src/shared/api/schema.d.ts, docs/security/auth.md, docs/testing/private-operation-context.md, existing/e2e new tests. No workflow/CSP/security-gate weakening.

- [ ] **1. Reconcile source and contract before push.** Generate OpenAPI only through the authorized hosted backend or existing bundled generation path; Mac runs no .NET. Preserve all new required bootstrap/result fields and typed password403 extension in the committed contract. Frontend api:generate produces the exact TS schema; generated schema is ignored by Prettier and must not be mass-formatted. Normal contract gate must regenerate and match. If no authorized preflight generator is available, prepare the narrowly additive JSON from the actual DTO shape and prove it with the normal final contract gate as with243; never claim it was generated locally.

- [ ] **2. Run the relevant lightweight checks once after final changes.** Full frontend tests are justified by routing all private consumers; no duplicate unchanged baseline tests after they pass. Type/lint/format changed sources, diffcheck, ancestry checks and targeted serializer/route inventory inspection. Add backend test cases first but final real PG execution remains hosted. Record separate actual Node RED/GREEN, backend execution and browser evidence; no invented backend RED.

```bash
NODE_OPTIONS=--max-old-space-size=512 node_modules/.bin/vitest run --maxWorkers=1
node_modules/.bin/tsc --noEmit -p tsconfig.app.json
git diff --check
```

- [ ] **3. Independent review before the necessary final push/PR.** Review the whole composed head, exact scope/metadata,15GET plus additions, bootstrap/403, proof leakage, result handoff and page-wide decoder teardown. Address concrete findings only and reverify affected tests. Create one regular PR through the authorized direct GitHub app capability, with actual final head/base and attach it. Never use this to recreate the separate421 human-Submit operation. Normal automatic CI runs all nine gates; no provisional CI, manual rerun/cancel or heavy Mac check. Inspect actual failures before fixes; a new source fix merits its own normal new-head CI, unchanged valid evidence is reused.

- [ ] **4. Record final results and limits.** Required gates: backend, frontend, e2e, ops, images(amd64), images(arm64), secret-scan, contract, supply-chain. Require each completed SUCCESS on the exact final run/head; PG/body/role/CSRF suites and real browser audio/CSP cases must actually execute. Keep physical AT/Safari/NVDA comfort and total browser RSS unproven unless measured separately. No T21/issue closure from a design review or generic CI count alone.

- [ ] **5. Deliver to the coordinated queue with explicit compatibility/rollback.** Old SPA can read only public/bootstrap; old private GET/HEAD/write tokens fail403 and require compatible assets. New SPA with old backend mounts no private area. Verify coherent Web+SPA release identity in the later authorized window; rollback both together, never disable the provider or accept empty proof to obtain green. This task author does no host action/main merge. The independent warning/protection plan still needs its own reviewed implementation and evidence before #9/#79/#80 closure.

## Review record and plan self-check

Design reviewers: e3 original b7314280, stable/CSP adendum35f2fba0, final8130 fd9bb1eb, delta4dd5d3d70b9; root auditor bd3a3c85. Independent12-transition inventory SHA2629e8550a8a629bcae5c17f855de30bb5efe5e5d8ceb9b65bec3b25059d8293;53-operation route inventory is only a source inventory, not authorization evidence.

Coverage: server/context contract task1; owner/async/body boundary task2; private consumers/bootstrap/cleanup task3; native audio and bounds task4; same/new SID secrets task5; compatibility/ancestry/gates/limits task6. Review Focus cases map to actual task assertions above. No implementation task starts until this plan receives independent/root review. All tasks remain unchecked because this document is not an execution receipt.
