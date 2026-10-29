using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// Issue #299 (ADR-0042): medidas de moderação de conta em <c>safety.moderation_actions</c> e o estado anterior da
/// conta suspensa em <c>identity.users</c>. Aditiva: tabela vazia e coluna nula, sem backfill. Uma suspensão ativa por
/// conta é garantida pelo índice único parcial, não por consulta antes do INSERT. A decisão registrada é imutável
/// (trigger); a revogação só preenche, uma única vez, as colunas <c>lifted_*</c>, e a aplicação não apaga linhas.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261030090000_AccountSuspensions")]
public sealed class AccountSuspensions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table identity.users add column status_before_suspension varchar(32) null;
        alter table identity.users add constraint ck_users_status_before_suspension check (
          status_before_suspension is null
          or (status = 'Suspended' and status_before_suspension in ('PendingEligibility', 'Active', 'Restricted')));

        create table safety.moderation_actions (
          id uuid constraint pk_moderation_actions primary key,
          subject_user_id uuid not null,
          measure varchar(32) not null,
          reason_code varchar(64) not null,
          internal_reason varchar(2000) not null,
          author_kind varchar(16) not null,
          author_user_id uuid null,
          decided_at timestamptz not null,
          command_key varchar(64) not null,
          command_intent bytea not null,
          version bigint not null default 1,
          lifted_at timestamptz null,
          lifted_by_kind varchar(16) null,
          lifted_by_user_id uuid null,
          lift_reason varchar(2000) null,
          constraint fk_moderation_actions_subject foreign key (subject_user_id)
            references identity.users (id) on delete restrict,
          constraint fk_moderation_actions_author foreign key (author_user_id)
            references identity.users (id) on delete restrict,
          constraint fk_moderation_actions_lifted_by foreign key (lifted_by_user_id)
            references identity.users (id) on delete restrict,
          constraint ux_moderation_actions_command unique (command_key),
          constraint ck_moderation_actions_measure check (measure in ('Suspension')),
          constraint ck_moderation_actions_reason check (reason_code in ('TermsViolation', 'SelfieVerificationFailed')),
          constraint ck_moderation_actions_author check (
            (author_kind = 'Human' and author_user_id is not null)
            or (author_kind = 'Automated' and author_user_id is null)),
          constraint ck_moderation_actions_terms_need_human check (reason_code <> 'TermsViolation' or author_kind = 'Human'),
          constraint ck_moderation_actions_not_self check (author_user_id is distinct from subject_user_id),
          constraint ck_moderation_actions_internal_reason check (length(btrim(internal_reason)) > 0),
          constraint ck_moderation_actions_command check (
            command_key ~ '^[0-9a-f]{64}$' and octet_length(command_intent) = 32),
          constraint ck_moderation_actions_lift check (
            (version = 1 and lifted_at is null and lifted_by_kind is null and lifted_by_user_id is null
              and lift_reason is null)
            or (version = 2 and lifted_at is not null and lifted_at >= decided_at and lift_reason is not null
              and length(btrim(lift_reason)) > 0
              and ((lifted_by_kind = 'Human' and lifted_by_user_id is not null)
                or (lifted_by_kind = 'Automated' and lifted_by_user_id is null)))),
          constraint ck_moderation_actions_dates check (
            isfinite(decided_at) and (lifted_at is null or isfinite(lifted_at))));

        -- Uma suspensão ativa por conta: duas decisões simultâneas criam uma só medida (INV-06).
        create unique index ux_moderation_actions_active_suspension on safety.moderation_actions (subject_user_id)
          where measure = 'Suspension' and lifted_at is null;
        -- Histórico da conta e índices das FKs (o PostgreSQL não os cria sozinho).
        create index ix_moderation_actions_subject on safety.moderation_actions (subject_user_id, decided_at);
        create index ix_moderation_actions_author on safety.moderation_actions (author_user_id);
        create index ix_moderation_actions_lifted_by on safety.moderation_actions (lifted_by_user_id);

        create function safety.keep_moderation_decision() returns trigger language plpgsql as $$
        begin
          if old.lifted_at is not null then
            raise exception 'A lifted moderation action is immutable' using errcode = '23514';
          end if;
          if (new.id, new.subject_user_id, new.measure, new.reason_code, new.internal_reason, new.author_kind,
              new.author_user_id, new.decided_at, new.command_key, new.command_intent)
             is distinct from
             (old.id, old.subject_user_id, old.measure, old.reason_code, old.internal_reason, old.author_kind,
              old.author_user_id, old.decided_at, old.command_key, old.command_intent) then
            raise exception 'A moderation decision is immutable' using errcode = '23514';
          end if;
          return new;
        end
        $$;
        create trigger tg_moderation_actions_keep_decision before update on safety.moderation_actions
          for each row execute function safety.keep_moderation_decision();

        -- A medida e sua revogação são história (INV-10): a aplicação não apaga.
        revoke delete, truncate on safety.moderation_actions from duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve moderation history; use a compatible release or a reviewed forward migration.");
}
