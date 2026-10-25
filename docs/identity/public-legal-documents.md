# Public legal documents and registration

The canonical bilingual source is `backend/src/Duora.Infrastructure/Identity/LegalDocuments/catalog.json`, embedded in the application assembly. `GET /api/v1/legal-documents` is an exact anonymous read, with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. It grants no other public API access. `/terms` and `/privacy` select the current document in the interface language; `/terms/:version/:documentLocale` and `/privacy/:version/:documentLocale` resolve an exact retained version and language, without silent substitution.

## Publication state

The four texts in version `2026-10-25` are **drafts**, with a null controller identity and `approved: false`. `support@meetduora.com` is the designated support and privacy-request contact. The controller's full civil or company name must be supplied by the responsible owner. Actual providers, recipients, transfers, retention practices and rights handling must be qualified before approving a final text; historical architecture plans are not proof of the current operation. No placeholder legal entity or legal compliance opinion is supplied.

Each active document must have both `pt-BR` and `en-US` variants, meaningful text, the same identified controller and public contact, and explicit source approval. Missing, duplicate, invalid or unapproved variants keep registration closed. This intentionally changes registration availability: **the default source currently returns 503 for registration**. Existing login and account recovery are unaffected by this publication gate. Deployment and enabling real registration require separate operational authorization and the completed publication facts; green CI alone does not authorize activation.

## Exact acceptance

The public catalog is revalidated on mount and focus without a timer or automatic retry; stale readiness cannot remain indefinitely cached across visits. The registration form appears only after a valid published catalog. Each document has its own language selector and exact versioned link. The user interface language does not rewrite document metadata. Changing a document identity clears the form and acceptance; it also cancels pending public operations before CSRF can continue to registration. Credentials remain component-local and are not mutation variables.

The request sends each document's version, locale and SHA-256. The digest is over UTF-8 bytes of the canonical web JSON object in this property order: `document`, `version`, `locale`, `title`, `controllerName`, `privacyContact`, `sections` (section property order `title`, `paragraphs`). The .NET web JSON escaping and compact serialization define the bytes. Approval is excluded from content identity. Clients use the digest returned by the server rather than reconstructing serialization.

The endpoint validates the active publication and both exact receipts before rate consumption or password hashing; `AccountService` repeats the policy before SQL. Consent writes retain the current transaction, pending-account replacement and confirmed-account immutability. A new nullable `document_sha256` column records new fingerprints. No historical locale or digest is inferred or backfilled. The existing database update guard also protects the added column; rollback acquires an exclusive table lock before checking for evidence and refuses to remove recorded fingerprints, including a first concurrent writer.

Once approved, retain every published version and language unchanged. Text, identity, contact or translation changes require a new version and renewed explicit publication approval. Keep archived variants in the catalog so evidence links remain resolvable. Do not rewrite historical consent rows.

## Validation and scope

Integration tests use an explicit **synthetic document source** with the same real publication policy, not a Testing-mode exemption. A test can select the actual embedded drafts with `LegalDocuments = null`. Existing negative request bodies are not silently repaired. Browser mocks also require exact synthetic receipts.

Only targeted Node tests, type checks, lint and formatting may run locally with the agreed resource limits. .NET compilation, PostgreSQL migration/transaction tests, generated OpenAPI comparison and real browser checks are hosted CI gates. This work does not publish licensed media, activate regional flags, prove SMTP delivery, close #108 or establish a cause for #174.
