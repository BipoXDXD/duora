-- Reconciliação de privilégios depois de cada migração (duora-deploy, como postgres, no banco duora).
-- Idempotente. Existe porque default privileges não aceitam exceção: toda tabela nova do migrador nasce com DML
-- para duora_app, inclusive o histórico do EF Core, que o app não deve ler nem alterar (docs/data/model.md,
-- "Permissões de banco"). Tabelas append-only perdem UPDATE, DELETE e TRUNCATE na própria migration e de novo aqui,
-- e o deploy falha se o app ainda puder alterar alguma delas.
\set ON_ERROR_STOP on

DO $$
BEGIN
  IF to_regclass('ops.__ef_migrations_history') IS NOT NULL THEN
    REVOKE ALL ON TABLE ops.__ef_migrations_history FROM duora_app;
  END IF;
END
$$;

-- Append-only (D3, TM-05): repete o REVOKE da migration, para uma migration que o esqueça não abrir o histórico.
DO $$
BEGIN
  IF to_regclass('ops.audit_events') IS NOT NULL THEN
    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE ops.audit_events FROM duora_app;
  END IF;
END
$$;

-- Falha alto: o deploy para aqui, antes de subir a release, se o app ainda puder reescrever a auditoria.
DO $$
BEGIN
  IF to_regclass('ops.audit_events') IS NOT NULL
     AND (has_table_privilege('duora_app', 'ops.audit_events', 'UPDATE')
          OR has_table_privilege('duora_app', 'ops.audit_events', 'DELETE')
          OR has_table_privilege('duora_app', 'ops.audit_events', 'TRUNCATE')) THEN
    RAISE EXCEPTION 'duora_app ainda pode alterar ops.audit_events (append-only, D3)';
  END IF;
END
$$;

-- Reference catalog: maintained by reviewed migrations, never by the runtime role.
DO $$
BEGIN
  IF to_regclass('profiles.countries') IS NOT NULL THEN
    REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON profiles.countries FROM duora_app;
  END IF;
  IF to_regclass('profiles.subdivisions') IS NOT NULL THEN
    REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON profiles.subdivisions FROM duora_app;
  END IF;
END
$$;

-- Private regional declarations use ordinary DML, never TRUNCATE (which bypasses row constraint triggers).
DO $$
BEGIN
  IF to_regclass('profiles.regional_preferences') IS NOT NULL THEN
    REVOKE TRUNCATE ON profiles.regional_preferences FROM duora_app;
  END IF;
  IF to_regclass('profiles.regional_preference_countries') IS NOT NULL THEN
    REVOKE TRUNCATE ON profiles.regional_preference_countries FROM duora_app;
  END IF;
END
$$;

-- Country database manifests are immutable; retention privileges require their own reviewed migration.
DO $$
BEGIN
  IF to_regclass('ops.country_database_approvals') IS NOT NULL THEN
    REVOKE UPDATE, DELETE, TRUNCATE ON ops.country_database_approvals FROM duora_app;
  END IF;
  IF to_regclass('ops.country_database_retired') IS NOT NULL THEN
    REVOKE UPDATE, DELETE, TRUNCATE ON ops.country_database_retired FROM duora_app;
  END IF;
  IF to_regclass('ops.country_database_versions') IS NOT NULL THEN
    REVOKE UPDATE, DELETE, TRUNCATE ON ops.country_database_versions FROM duora_app;
  END IF;
END $$;

-- Operational SMTP journal: parent deletion must not bypass the delivery evidence ACL by cascade.
DO $$
BEGIN
  IF to_regclass('ops.country_database_deliveries') IS NOT NULL THEN
    REVOKE DELETE, TRUNCATE ON ops.country_database_deliveries,ops.country_database_notifications FROM duora_app;
  END IF;
END
$$;

-- Explicit photo review permissions retain append-only history, independently of moderator role membership.
DO $$
BEGIN
  IF to_regclass('safety.photo_review_grants') IS NOT NULL THEN
    REVOKE UPDATE, DELETE, TRUNCATE ON safety.photo_review_grants FROM duora_app;
    IF has_table_privilege('duora_app', 'safety.photo_review_grants', 'UPDATE')
       OR has_table_privilege('duora_app', 'safety.photo_review_grants', 'DELETE')
       OR has_table_privilege('duora_app', 'safety.photo_review_grants', 'TRUNCATE') THEN
      RAISE EXCEPTION 'duora_app may not rewrite photo review permission history';
    END IF;
  END IF;
END $$;

-- Assignment, evidence access history and confirmed command receipts are immutable on an existing database.
DO $$
DECLARE review_table text;
BEGIN
  FOREACH review_table IN ARRAY ARRAY['media.review_promotion_variants','safety.photo_review_assignments','safety.photo_review_commands',
    'safety.photo_review_decision_commands','safety.photo_review_decision_variants','safety.photo_review_decisions',
    'safety.photo_review_evidence_accesses'] LOOP
    IF to_regclass(review_table) IS NOT NULL THEN
      EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %s FROM duora_app', review_table);
      IF has_table_privilege('duora_app', review_table, 'UPDATE')
         OR has_table_privilege('duora_app', review_table, 'DELETE')
         OR has_table_privilege('duora_app', review_table, 'TRUNCATE') THEN
        RAISE EXCEPTION 'duora_app may not rewrite photo review history';
      END IF;
    END IF;
  END LOOP;
END $$;
