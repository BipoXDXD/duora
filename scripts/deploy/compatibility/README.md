# Existing PR260: current3d99/schema48 functional compatibility lab

**Candidate for M2 review. No Actions execution, image download, build, SDK or
Docker test has run locally. No new PR, issue, workflow, automation or product
change is proposed.** The only workflow delta appends one job to existing
`.github/workflows/ci.yml`, eligible only for existing PR260. It does not add a
fence to cohort259 or change the already-qualified259 source/results. The human
instruction to use existing260 supersedes the repository's usual separate-workflow
PR guideline for this specific reviewed proposal; no other workflow policy changes.

## Precise proof target

Current product target: public source `3d99eec4f9a78dd9a9ea509ae89bba35f7d85205`,
release `2026.10.25-3d99eec-40194977432-1-amd64`, protocol v2. The known current
manifest prefix `79f205` is historical context, **not a fabricated full hash** or
an operator receipt. Old web/edge/migrator and PostgreSQL images use the exact
qualified **transport leaf references** and config digests in
`current_schema_pins.json`. The worker reference is retained as provenance and
is not executed. Old web is never rebuilt or replaced by a WebApplicationFactory.

Incoming DDL is the precise public cohort259 commit
`1a9b0194d253dee9a810f710a4fdf9ff2a05c07b`, tree
`8e8cca1eceebe7480494e8103241288194a27091`. Actions verifies both before a single
new **migrator-only** build. It verifies fixture hashes and SQL bytes. This
qualification covers schema states **35 and 48 only**. Prefixes36..47 remain
**UNQUALIFIED/REFUSED** for recovery; nothing in a48 receipt authorizes all prefix
states, nor does head48 alone authorize startup without the confirmed exact
incoming SQL transaction. A future lab could exercise explicit forward-only EF targets with the
same built migrator, but its command contract and every state would need their
own reviewed native receipts. This candidate makes no such claim.

The job binds actual PR260 HEAD, its executor-source SHA256 (read only), actual
workflow SHA/ref, merge SHA, run/attempt, tested source trees, old runtime refs/
config IDs, actual newly built migrator ID, PG version, canonical SQL SHA and
harness SHA. M2 has not frozen a new executor head in this proposal. The eventual
job reads/binds its actual reviewed PR260 checkout; **it invokes the real reviewed `_recovery_require_quiescent`,
`_recovery_execute_sql` and `_recovery_acl_witness` helper bodies**, using a strict
transport that maps only Compose `exec postgres psql` to Docker `exec` on this
owned PostgreSQL. All psql arguments, SQL bytes, stdout/stderr and return codes
are real and preserved; Docker ps/inspect/top commands are forwarded unchanged.
No source/helper/guard is patched. **It does not execute the full product
`deploy`/attempt/current recovery flow.** A later proof of that path must name M2's final source
and durable attempt-receipt contract explicitly.

## What the disposable job actually does

1. Require a GitHub-hosted Ubuntu24.04, Linux/x86_64, root, Python3.12 process,
   existing PR260 and at least6GiB physical RAM. Assert the checkout HEADs/trees
   and tracked cleanliness; no secret environment, environment dump, registry
   login, SSH, OIDC, package/SDK installation or production endpoint.
2. Pull only the four required immutable old image references in Actions,
   verify platform/config/source identities, and build only the incoming
   migrator from the exact1a9 checkout. A fresh empty Docker config avoids
   inherited registry credentials. No old-binary rebuild or new worker build.
3. Reuse cohort `duora_secrets.init_secrets` for **synthetic-only** DB credentials
   and key-ring certificate. Reuse production FileSystem area/marker and TLS
   contracts from compose/continuity fixtures. PostgreSQL18.6 uses its real
   image/initdb role setup and app/migrator separation. `archive_mode=off` is an
   **explicit lab-only difference**: this is not pgBackRest/PITR/backup acceptance.
   The private network is internal, with only loopback HTTPS published by old
   Caddy using `tls internal`; no external mail, B2 or participant data.
4. Apply the **real old migrator** to35, reconcile the old literal SQL, and prove
   exact35 migration IDs. Start actual old web/edge. Through HTTPS and real CSRF,
   register, read the synthetic confirmation code from its DB outbox, confirm by
   the public API, log in, GET profile, PATCH with its ETag and GET the persisted
   value. Verify two consent rows, outbox and login-session rows. No provider
   worker sends email; confirmation still uses the actual old endpoint.
5. Hold a real **duora_app SELECT-only** session and sentinel across the
   transition using one `pgbench` client already in the exact PG18.6 image,
   nonce-bound `PGAPPNAME`, `default_transaction_read_only=on` and a SELECT-only
   script that fails if the read-only setting is absent. Observe the same actual
   backend PID **and backend_start** before/after. If pgbench is absent, FAIL
   concretely; no install or alternative driver. Every setup/admin psql process
   closes before the unchanged quiescence helper. No persistent postgres or
   migrator session and no hidden/renamed psql process. Record PostgreSQL
   ID, image, startedAt, restartCount, all mounts and postmaster start. Stop/remove
   **only the owned old web**, keep PostgreSQL/volume/session/edge and apply the
   real incoming migrator to48. **Head48 alone does not permit recovery.** The
   four finite cases below must pass before old web can start. The actual
   production helper applies SQL with its unchanged `--single-transaction`;
   its actual ACL witness must reject adversarial grants and pass after repair.
   The actual quiescence helper checks other privileged SQL sessions, psql client
   processes and one-shot migrators; a pre-existing foreign project blocks the
   entire disposable lab. No guard/source mutation or fake adapter output.
   These cases use exact
   incoming canonical SQL SHA256
   `5d666adcaefeaa7883d4e2596c6ef66ee03b67d04bca79d590e690f801573747`,
   authenticated against cohort1a9/tree8e8, through the actual SQL helper transaction.
   Assert all48 IDs, not only the count, and `document_sha256` nullable text.
6. Exercise these **lab-only SQL boundary states** with web absent throughout
   the first three: (a)48 before canonical SQL, with a deliberately unsafe
   `duora_app` UPDATE/DELETE/TRUNCATE grant on the synthetic audit table as a
   negative fixture; (b) exact incoming SQL followed by deliberate division by
   zero inside the transaction, require that exact failure and prove the full
   schema/grants digest and unsafe ACL rolled back; (c) incoming SQL committed
   but confirmation absent, require startup refusal; (d) repair-forward by
   replaying **the same authenticated incoming SQL transaction**, verify commit,
   closed ACL and identical schema/grants digest across replay, then confirm.
   Actual migration IDs are rechecked at every case. There is no oldSQL repair,
   Down, fake product seal or new product receipt. The lab's process flag is not
   a durable product seal; M2 must separately qualify that crash/restart contract.
   Old web startup on48 is guarded by confirmed repair-forward, including a real
   attempted startup refusal in each earlier state.
7. Inject exactly one **app/current-boundary lab-only** exception after real DDL48 and confirmedSQL, before any
   product-current commit. No executor/observer, fake attempt receipt, success
   history or real current file is invoked/written. Restart the exact same old
   web binary with the same key ring, **retain DDL48**, and read the previous
   authenticated profile with its existing cookie. Verify original consents.
8. Repeat the same old functional paths under48, verify its consent writes leave
   the new fingerprint null, and perform a legacy five-column consent INSERT as
   **duora_app**, omitting both nullable locale/fingerprint. Verify the resulting
   nulls. Assert PG/session/sentinel preservation and unchanged full48 schema/
   grants digest across recovery. No Down migration or database/volume recreation.
9. Verify tracked source remains unchanged. Cleanup only resources whose unique
   nonce labels match: owned containers, volumes, network and newly built lab
   migrator. Never global prune, global stack names or production resources.
   Cleanup failure makes the receipt fail. Only a <=64KiB public-metadata receipt
   is uploaded, never configuration, token/cookie, SQL dump, password, key or
   environment. Missing evidence, wrong identity/history/function, changed PG,
   unknown initdb/startup configuration or nonzero command cannot report green.

## Limits and gates still open

The current test source is unexecuted. The current public260 pin55ef does not
establish the new helper interface: M2 must freeze/review the final actual260
head. If the helper names are absent from that checkout, the lab refuses before
Docker resources/builds. The native helper module has not been imported locally. Local stdlib tests use **synthetic parser
outcomes** and prove rejection logic only; they are not native database evidence.
The loopback TLS/network and initdb fixtures require actual Actions acceptance.
The job refuses unresolved startup/configuration instead of using permissive
fallbacks. No app settings are injected to bypass authorization or auto-migration.

A successful run would qualify the tested **old web DB interactions** on retained
full48. It would not prove all worker paths, every application feature, intermediate
migration prefixes, full Coolify/observer/executor recovery beyond the three SQL helpers, durable v2 attempt
receipts, production backup compatibility, actual-host receipt, resource continuity
outside this controlled window or permission to publish259. It does not erase the
current operational blocker. M2/root must review before adding this delta to260;
no job has been dispatched and existing259 CI is not rerun by this preparation.

API coverage uses existing API-A01/A02/A03/A04 and API-P01/P02, preserving CSRF,
ETag and role boundaries (T-13/T-19 relevance). This adds no API/DTO, does not
alter modules, migration source, runtime grants, assets or any executor/receptor.
