-- PROPOSTA PARA REVISÃO. Não executada nesta auditoria.
-- Executar somente pelo operador autorizado, com identidade de leitura aprovada.
-- Suspender publicações e drenar transações antes da coleta final.
-- Sem dados individuais; não substitui inventário de processos/digests/configuração.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '10s';

SELECT transaction_timestamp() AT TIME ZONE 'UTC' AS snapshot_utc;

SELECT "MigrationId"
FROM ops.__ef_migrations_history
ORDER BY "MigrationId";

SELECT state, algorithm_version, count(*) AS rounds,
       min(starts_at) AS first_start, max(starts_at) AS last_start
FROM events.rounds
GROUP BY state, algorithm_version
ORDER BY state, algorithm_version;

-- Inclui V2 abertas ainda que o início já tenha passado.
SELECT algorithm_version, state, count(*) AS open_or_future_rounds,
       min(starts_at) AS first_start, max(starts_at) AS last_start
FROM events.rounds
WHERE state NOT IN ('Closed', 'Cancelled')
   OR starts_at >= transaction_timestamp()
GROUP BY algorithm_version, state
ORDER BY algorithm_version, state;

SELECT kind, status, count(*) AS jobs,
       min(due_at) AS first_due, max(due_at) AS last_due,
       min(lease_expires_at) AS first_lease_expiry,
       max(lease_expires_at) AS last_lease_expiry
FROM ops.jobs
WHERE kind IN ('build-round', 'close-round', 'expire-invite', 'game-deadline')
GROUP BY kind, status
ORDER BY kind, status;

SELECT count(*) AS pair_round_version_mismatches
FROM events.pairings p
JOIN events.rounds r ON r.id = p.round_id
WHERE p.algorithm_version <> r.algorithm_version OR p.event_id <> r.event_id;

-- Catálogo persistido; não comprova habilitação operacional do país.
SELECT code AS catalog_country FROM profiles.countries ORDER BY code;

COMMIT;
