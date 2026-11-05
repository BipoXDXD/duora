using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// Issue #321 (ADR-0049): manifesto da reconciliação de inscrições legadas. Aditiva: três tabelas vazias em
/// <c>ops</c>, sem tocar Events nem Safety. Um só run aberto por índice único parcial; triggers mantêm o manifesto
/// imutável, o recibo imutável quando terminal e o estado terminal nascido só de <c>Pending</c>. O vínculo com a outbox
/// some em cascata com o fato (a retenção não trava), e a unidade nunca some. O DOWN recusa apagar evidência.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261105120000_RegistrationReconciliationManifest")]
public sealed class RegistrationReconciliationManifest : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table ops.registration_reconciliation_runs (
          id uuid constraint pk_registration_reconciliation_runs primary key,
          selector_version integer not null,
          cutover_evidence_id text not null,
          cutover_lower timestamptz not null,
          cutover_upper timestamptz not null,
          created_by_user_id uuid not null,
          justification text not null,
          authorization_reference text not null,
          state text not null,
          version bigint not null,
          created_at timestamptz not null,
          idempotency_key text not null,
          intent_hash bytea not null,
          constraint fk_registration_reconciliation_runs_creator foreign key (created_by_user_id)
            references identity.users (id) on delete restrict,
          constraint ux_registration_reconciliation_runs_command unique (created_by_user_id, idempotency_key),
          constraint ck_registration_reconciliation_runs_selector check (selector_version >= 1),
          constraint ck_registration_reconciliation_runs_evidence check (cutover_evidence_id ~ '^[A-Za-z0-9._:-]{1,128}$'),
          constraint ck_registration_reconciliation_runs_cutover check (
            isfinite(cutover_lower) and isfinite(cutover_upper) and cutover_lower <= cutover_upper),
          constraint ck_registration_reconciliation_runs_justification check (
            char_length(justification) <= 2000 and length(btrim(justification)) > 0
            and justification !~ '[\x01-\x1f\x7f-\x9f]'),
          constraint ck_registration_reconciliation_runs_reference check (
            char_length(authorization_reference) <= 200 and length(btrim(authorization_reference)) > 0
            and authorization_reference !~ '[\x01-\x1f\x7f-\x9f]'),
          constraint ck_registration_reconciliation_runs_state check (state in ('Open', 'Closed')),
          constraint ck_registration_reconciliation_runs_version check (version >= 1),
          constraint ck_registration_reconciliation_runs_created_at check (isfinite(created_at)),
          constraint ck_registration_reconciliation_runs_key check (idempotency_key ~ '^[A-Za-z0-9_-]{1,64}$'),
          constraint ck_registration_reconciliation_runs_intent check (octet_length(intent_hash) = 32));
        create unique index ux_registration_reconciliation_runs_open on ops.registration_reconciliation_runs ((true))
          where state = 'Open';

        create table ops.registration_reconciliation_units (
          id uuid constraint pk_registration_reconciliation_units primary key,
          run_id uuid not null,
          subject_user_id uuid not null,
          original_measure_id uuid not null,
          measure_version bigint not null,
          items jsonb not null,
          state text not null default 'Pending',
          receipt_reason text null,
          receipt_fact_ids uuid[] null,
          receipt_executed_by uuid null,
          receipt_executed_at timestamptz null,
          receipt_correlation_id text null,
          apply_idempotency_key text null,
          apply_intent_hash bytea null,
          constraint fk_registration_reconciliation_units_run foreign key (run_id)
            references ops.registration_reconciliation_runs (id) on delete restrict,
          constraint fk_registration_reconciliation_units_subject foreign key (subject_user_id)
            references identity.users (id) on delete restrict,
          constraint fk_registration_reconciliation_units_measure foreign key (original_measure_id)
            references safety.moderation_actions (id) on delete restrict,
          constraint fk_registration_reconciliation_units_executor foreign key (receipt_executed_by)
            references identity.users (id) on delete restrict,
          constraint ux_registration_reconciliation_units_manifest unique (run_id, subject_user_id, original_measure_id),
          constraint ck_registration_reconciliation_units_measure_version check (measure_version >= 1),
          constraint ck_registration_reconciliation_units_items check (
            jsonb_typeof(items) = 'array' and jsonb_array_length(items) between 1 and 100),
          constraint ck_registration_reconciliation_units_state check (
            state in ('Pending', 'Applied', 'SkippedChanged', 'NeedsReview')),
          constraint ck_registration_reconciliation_units_receipt check (
            (state = 'Pending' and receipt_reason is null and receipt_fact_ids is null and receipt_executed_by is null
              and receipt_executed_at is null and receipt_correlation_id is null and apply_idempotency_key is null
              and apply_intent_hash is null)
            or (state <> 'Pending' and receipt_fact_ids is not null and receipt_executed_by is not null
              and receipt_executed_at is not null and isfinite(receipt_executed_at)
              and receipt_correlation_id is not null and char_length(receipt_correlation_id) between 1 and 64
              and apply_idempotency_key is not null and apply_idempotency_key ~ '^[A-Za-z0-9_-]{1,64}$'
              and apply_intent_hash is not null and octet_length(apply_intent_hash) = 32
              and ((state = 'Applied' and receipt_reason is null)
                or (state in ('SkippedChanged', 'NeedsReview') and receipt_reason is not null
                  and receipt_reason ~ '^[A-Z][A-Za-z]{0,63}$' and cardinality(receipt_fact_ids) = 0)))));
        create index ix_registration_reconciliation_units_subject on ops.registration_reconciliation_units (subject_user_id);
        create index ix_registration_reconciliation_units_measure on ops.registration_reconciliation_units (original_measure_id);
        create unique index ux_registration_reconciliation_units_apply
          on ops.registration_reconciliation_units (receipt_executed_by, apply_idempotency_key);

        create table ops.registration_reconciliation_unit_facts (
          fact_id uuid constraint pk_registration_reconciliation_unit_facts primary key,
          unit_id uuid not null,
          constraint fk_registration_reconciliation_unit_facts_fact foreign key (fact_id)
            references ops.outbox_events (id) on delete cascade,
          constraint fk_registration_reconciliation_unit_facts_unit foreign key (unit_id)
            references ops.registration_reconciliation_units (id) on delete restrict);
        create index ix_registration_reconciliation_unit_facts_unit on ops.registration_reconciliation_unit_facts (unit_id);

        create function ops.guard_registration_reconciliation_run() returns trigger language plpgsql as $$
        begin
          if tg_op = 'DELETE' then
            raise exception 'A reconciliation run is evidence and cannot be deleted' using errcode = '23514';
          end if;
          if tg_op = 'INSERT' then
            if new.state <> 'Open' or new.version <> 1 then
              raise exception 'A reconciliation run is born Open at version 1' using errcode = '23514';
            end if;
            return new;
          end if;
          if (new.id, new.selector_version, new.cutover_evidence_id, new.cutover_lower, new.cutover_upper,
              new.created_by_user_id, new.justification, new.authorization_reference, new.created_at,
              new.idempotency_key, new.intent_hash)
             is distinct from
             (old.id, old.selector_version, old.cutover_evidence_id, old.cutover_lower, old.cutover_upper,
              old.created_by_user_id, old.justification, old.authorization_reference, old.created_at,
              old.idempotency_key, old.intent_hash) then
            raise exception 'The manifest of a reconciliation run is immutable' using errcode = '23514';
          end if;
          if old.state <> 'Open' or new.state <> 'Closed' or new.version <> old.version + 1 then
            raise exception 'A reconciliation run only goes from Open to Closed, once' using errcode = '23514';
          end if;
          return new;
        end $$;
        create trigger guard_registration_reconciliation_run
          before insert or update or delete on ops.registration_reconciliation_runs
          for each row execute function ops.guard_registration_reconciliation_run();

        create function ops.guard_registration_reconciliation_unit() returns trigger language plpgsql as $$
        begin
          if tg_op = 'DELETE' then
            raise exception 'A reconciliation unit is evidence and cannot be deleted' using errcode = '23514';
          end if;
          if tg_op = 'INSERT' then
            if new.state <> 'Pending' then
              raise exception 'A reconciliation unit is born Pending' using errcode = '23514';
            end if;
            -- O manifesto fecha com o commit da criação; o run nasce nesta mesma transação, sem savepoint no fluxo.
            -- Estado e versão contam porque o UPDATE do fechamento também grava uma tupla com o xmin atual.
            if not exists (select 1 from ops.registration_reconciliation_runs r
                           where r.id = new.run_id and r.xmin = pg_current_xact_id()::xid
                             and r.state = 'Open' and r.version = 1) then
              raise exception 'A reconciliation unit is born in the transaction of its run' using errcode = '23514';
            end if;
            return new;
          end if;
          if (new.id, new.run_id, new.subject_user_id, new.original_measure_id, new.measure_version, new.items)
             is distinct from
             (old.id, old.run_id, old.subject_user_id, old.original_measure_id, old.measure_version, old.items) then
            raise exception 'The manifest of a reconciliation unit is immutable' using errcode = '23514';
          end if;
          if old.state <> 'Pending' or new.state = 'Pending' then
            raise exception 'A reconciliation unit only leaves Pending, once' using errcode = '23514';
          end if;
          return new;
        end $$;
        create trigger guard_registration_reconciliation_unit
          before insert or update or delete on ops.registration_reconciliation_units
          for each row execute function ops.guard_registration_reconciliation_unit();

        revoke delete, truncate on ops.registration_reconciliation_runs, ops.registration_reconciliation_units from duora_app;
        revoke update, delete, truncate on ops.registration_reconciliation_unit_facts from duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve reconciliation evidence; use a compatible release or a reviewed forward migration.");
}
