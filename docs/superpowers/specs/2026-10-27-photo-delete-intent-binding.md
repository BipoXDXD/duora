# Photo DELETE: stable operation key and immutable intent

Bounded source candidate for M5 (API-M03, INV-06/11; T-06/11/26/27). This pure Core value binds the
already approved delete protocol; it does not expose an endpoint or perform storage/SQL operations.

The operation key is HMAC-SHA256 over these newline-terminated ASCII fields, in order:
`DUORA-PHOTO-DELETE-OP-V1`, key ID, environment UUID, owner UUID, client Idempotency-Key.
UUIDs use lowercase D format. The key is dedicated stable material, 32–64 bytes; this length check proves
neither entropy nor custody. The ID uses the existing safe 1–64 ASCII character alphabet.

Intent SHA256 hashes newline-terminated fields: `DUORA-PHOTO-DELETE-INTENT-V1`, environment UUID,
operation-key lowercase hex, logical photo UUID, positive expected version in invariant decimal,
and the captured snapshot SHA256 as 64 lowercase hex characters. The snapshot digest must come from a
trusted SQL capture. This helper validates its length only, not the snapshot's provenance or contents.

Changing photo/version/snapshot MUST keep the same operation key and change the intent digest. The real
external atomic CreateOrGet must return the original Prepared binding or conflict on changed intent;
generating another UUID or overwriting Prepared would violate idempotency. Retrieve an existing binding
before recapturing SQL state on replay. Owner, environment, client key and key generation scope the key.
The key ID and material must remain admitted across retries and restores: rotating either requires an
explicit custody/recovery protocol, not an implicit new operation. No raw owner/client key/key material
is retained in the result. Result properties are immutable and ToString is redacted.

Acceptance here is bounded to literal independent HMAC/SHA known answers, scope separation, same-key
intent conflicts, input boundaries and non-retention of mutable input arrays. Tests are written before
implementation. No local SDK/build/test/formatter is authorized; executable gates belong to the eventual
normal PR CI after the composition vehicle is agreed. Do not claim RED/GREEN before actual execution.

Still missing: actual authenticated/durable atomic Prepared and conditional terminal writes, SQL
revalidation and durable SQLAborting, independent writer/restore fence, ambiguous COMMIT recovery,
read/export adapter, key custody and real provider semantics. This value proves none of them and never
authorizes SQL commit, Abort, HTTP 204, host installation, restore or production HumanReview admission.
No concurrency locks or permissions are changed. Rollback removes the unused helper/tests together;
once a real writer adopts V1, changing canonical bytes requires an explicit versioned transition.
