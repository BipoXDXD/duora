# Private preparation of human review promotions — #173 / M4a2

ADR-0035 requires copying sanitized derivatives outside SQL before an authorized approval commits.
This slice owns only preparation metadata, isolated final keys and fenced cleanup of abandoned preparations.
It does not approve a photo, create an asset, register a service, expose HTTP, change flags or touch production.
The approval coordinator and committed-candidate retention remain later slices.

## Invariants and ownership

- Media owns new empty `review_promotions` and `review_promotion_variants` tables. Safety still owns cases/decisions.
- Every attempt gets a new generation. `derivatives/review-{generation:N}-{thumb|card|full}` is structurally
  disjoint from legacy derivative keys even if their 128-bit tokens coincide. Existing factories remain compatible.
- Confirm preparation and all three canonical manifests before the first PUT. A lost preparation commit response
  never permits a PUT. No old file or generation is enrolled or scanned.
- Verify candidate generation, case/upload versions, policy, current state, owner eligibility, deadlines and the
  exact three manifests. Reads and PUTs hold no SQL transaction. Bound bytes to 16 MiB each / 24 MiB total and
  preparation to 15 seconds. Recheck after I/O; wipe temporary bytes and observe cleanup/cancellation.
- A prepared result is private and temporary, never approval. The later coordinator must authorize before and
  after preparation and atomically commit the asset, upload, case, decision, receipt, audit and cleanup obligation.
- Record an unsettled writer before storage can accept a write. Uncertainty never becomes settlement by age.
  No next PUT follows a deadline, cancellation or late response. Retry creates a fresh generation.
- GC marks cleanup before DELETE. Commit must refuse cleanup-marked, expired, deleted or claimed generations.
  If commit wins first, GC cannot claim that generation. Only the three metadata-owned keys may be deleted.
- Capture `WriterWasSettled` before a sweep starts. A writer settling during DELETE cannot make that sweep terminal.
  A later sweep must run. Claims, retries and terminal ACKs use a token and live lease; stale claims cannot affect a
  replacement. An uncertain intent stays durable through restart and never expires merely by age.
- Approved-only SQL backup manifests are a mandatory composition gate before activation: recursive derivatives
  backup currently includes uncommitted preparations. Do not exclude all new keys, which would also lose approved
  photos. No backup or real storage configuration is changed by this slice.

## Verification sequence

1. Write keys/preparation tests against nonfunctional seams; observe their own behavioral failures on remote CI.
2. Implement minimal keys/preparation, preserving baseline bytes. Confirm metadata before first PUT, exact copies,
   rejection of stale snapshots/corruption, uncertain PUT retention and SQL availability during storage I/O.
3. Add independent GC race tests before its implementation: late PUT after DELETE, settlement during sweep,
   stale ACK/retry, restart, claim vs commit in both orders and unchanged legacy/winner sentinel bytes.
4. Add commit-fault and budget/cancellation proofs; no hidden fire-and-forget tasks or false confirmation.
5. Independently review, restack to final main, then require all nine official checks and any triggered native checks.

All data is synthetic and all heavy tests/builds run on GitHub. No Docker, installs or builds on the Mac.
This plan does not close #173 or activate HumanReview; HTTP, appeals, deletion/restore, backup composition and
independent appeal coverage remain required. Luca Grimmer is owner/principal; no real account grants follow.
