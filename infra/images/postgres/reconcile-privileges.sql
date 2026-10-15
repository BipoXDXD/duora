-- Reconciliação de privilégios depois de cada migração (duora-deploy, como postgres, no banco duora).
-- Idempotente. Existe porque default privileges não aceitam exceção: toda tabela nova do migrador nasce com DML
-- para duora_app, inclusive o histórico do EF Core, que o app não deve ler nem alterar (docs/data/model.md,
-- "Permissões de banco"). Tabelas append-only são tratadas na própria migration (REVOKE UPDATE, DELETE, TRUNCATE).
\set ON_ERROR_STOP on

DO $$
BEGIN
  IF to_regclass('ops.__ef_migrations_history') IS NOT NULL THEN
    REVOKE ALL ON TABLE ops.__ef_migrations_history FROM duora_app;
  END IF;
END
$$;
