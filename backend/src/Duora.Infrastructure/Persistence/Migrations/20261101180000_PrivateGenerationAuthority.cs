using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261101180000_PrivateGenerationAuthority")]
public sealed class PrivateGenerationAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table media.private_generation_bindings (
          generation uuid not null, area integer not null check(area in (1,2)),
          domain uuid not null check(domain<>'00000000-0000-0000-0000-000000000000'),
          epoch bigint not null check(epoch>0), layout integer not null check(layout=1),
          manifest_hash varchar(64) not null check(manifest_hash ~ '^[0-9a-f]{64}$'),
          created_at timestamptz not null default statement_timestamp(),
          primary key(generation,area));
        create table media.private_generation_proofs (
          id uuid primary key, generation uuid not null, area integer not null,
          domain uuid not null, epoch bigint not null, layout integer not null,
          manifest_hash varchar(64) not null, cleanup_token uuid not null,
          closed_revision bigint not null check(closed_revision>0),
          swept_at timestamptz not null default statement_timestamp(),
          foreign key(generation,area) references media.private_generation_bindings(generation,area),
          unique(generation,area,cleanup_token));
        revoke update,delete,truncate on media.private_generation_bindings from duora_app;
        revoke insert,update,delete,truncate on media.private_generation_proofs from duora_app;
        grant select on media.private_generation_bindings,media.private_generation_proofs to duora_app;
        create table media.private_generation_births(
          generation uuid not null,area integer not null check(area in(1,2)),
          created_in_transaction xid8 not null,primary key(generation,area));
        revoke insert,update,delete,truncate on media.private_generation_births from duora_app;
        create function media.capture_private_generation_birth() returns trigger
        language plpgsql security definer set search_path=pg_catalog,pg_temp as $$
        begin
          insert into media.private_generation_births(generation,area,created_in_transaction)
            values(NEW.generation,case TG_TABLE_NAME when 'review_candidates' then 1 else 2 end,
              pg_current_xact_id());
          return NEW;
        end $$;
        revoke all on function media.capture_private_generation_birth() from public,duora_app;
        create trigger capture_candidate_birth after insert on media.review_candidates
          for each row execute function media.capture_private_generation_birth();
        create trigger capture_promotion_birth after insert on media.review_promotions
          for each row execute function media.capture_private_generation_birth();
        create function media.guard_private_generation_binding() returns trigger
        language plpgsql security definer set search_path=pg_catalog,pg_temp as $$
        declare v_fresh boolean;
        begin
          if NEW.area=1 then
            select true into v_fresh from media.review_candidates c where c.generation=NEW.generation
              and exists(select 1 from media.private_generation_births b where b.generation=c.generation
                and b.area=1 and b.created_in_transaction=pg_current_xact_id())
              and c.committed_at is null and c.deleted_at is null and c.cleanup_requested_at is null
              and c.prepare_deadline>statement_timestamp();
          elsif NEW.area=2 then
            select true into v_fresh from media.review_promotions c where c.generation=NEW.generation
              and exists(select 1 from media.private_generation_births b where b.generation=c.generation
                and b.area=2 and b.created_in_transaction=pg_current_xact_id())
              and c.committed_at is null and c.deleted_at is null and c.cleanup_requested_at is null
              and c.prepare_deadline>statement_timestamp();
          end if;
          if v_fresh is distinct from true then
            raise exception 'Binding requires a fresh preparation transaction' using errcode='23514';
          end if;
          return NEW;
        end $$;
        revoke all on function media.guard_private_generation_binding() from public,duora_app;
        create trigger guard_private_binding before insert on media.private_generation_bindings
          for each row execute function media.guard_private_generation_binding();
        alter table media.review_candidates add column storage_proof_id uuid null
          references media.private_generation_proofs(id);
        alter table media.review_promotions add column storage_proof_id uuid null
          references media.private_generation_proofs(id);

        -- Preserve the old conditions; add explicit independent storage evidence rather than inventing a writer ACK.
        do $$
        declare item record;
        begin
          for item in select conname,conrelid::regclass as relation from pg_constraint
            where conrelid in ('media.review_candidates'::regclass,'media.review_promotions'::regclass)
              and contype='c' and pg_get_expr(conbin,conrelid) like '((deleted_at IS NULL) OR %'
          loop
            execute format('alter table %s drop constraint %I',item.relation,item.conname);
          end loop;
        end $$;
        alter table media.review_candidates add constraint ck_candidate_removal_evidence check
          (deleted_at is null or (cleanup_requested_at is not null and cleanup_token is null
            and (writer_settled_at is not null or storage_proof_id is not null)));
        alter table media.review_promotions add constraint ck_promotion_removal_evidence check
          (deleted_at is null or (committed_at is null and cleanup_requested_at is not null and cleanup_token is null
            and (writer_settled_at is not null or storage_proof_id is not null)));

        create function media.guard_private_generation_removal() returns trigger
        language plpgsql security definer set search_path=pg_catalog,pg_temp as $$
        declare v_area integer; v_binding media.private_generation_bindings%rowtype;
        begin
          v_area := case TG_TABLE_NAME when 'review_candidates' then 1 else 2 end;
          select * into v_binding from media.private_generation_bindings
            where generation=NEW.generation and area=v_area;
          if found and NEW.deleted_at is not null then
            if TG_OP='INSERT' then
              raise exception 'Managed removal requires a verified update' using errcode='23514';
            elsif OLD.deleted_at is not null then
              if NEW.deleted_at is distinct from OLD.deleted_at or NEW.storage_proof_id is distinct from OLD.storage_proof_id then
                raise exception 'Managed removal evidence is immutable' using errcode='23514';
              end if;
            elsif not exists(select 1 from media.private_generation_proofs p
              where p.id=NEW.storage_proof_id and p.generation=NEW.generation and p.area=v_area
                and p.domain=v_binding.domain and p.epoch=v_binding.epoch and p.layout=v_binding.layout
                and p.manifest_hash=v_binding.manifest_hash and p.cleanup_token=OLD.cleanup_token
                and OLD.cleanup_lease_until>statement_timestamp()
                and p.swept_at<=statement_timestamp()) then
              raise exception 'Managed generation requires a current-owner sweep proof' using errcode='23514';
            end if;
          end if;
          return NEW;
        end $$;
        revoke all on function media.guard_private_generation_removal() from public,duora_app;
        create trigger guard_candidate_removal before insert or update on media.review_candidates
          for each row execute function media.guard_private_generation_removal();
        create trigger guard_promotion_removal before insert or update on media.review_promotions
          for each row execute function media.guard_private_generation_removal();

        -- This function is called only by the separately credentialed storage verifier, AFTER Close + durable Sweep.
        create function media.record_private_generation_sweep(p_id uuid,p_generation uuid,p_area integer,
          p_domain uuid,p_epoch bigint,p_layout integer,p_hash text,p_token uuid,p_revision bigint)
        returns uuid language plpgsql security definer set search_path=pg_catalog,pg_temp as $$
        declare v_current boolean; v_id uuid;
        begin
          if p_id is null or p_id='00000000-0000-0000-0000-000000000000' or p_revision<1 then
            raise exception 'Invalid sweep evidence' using errcode='22023';
          end if;
          if p_area=1 then
            select true into v_current from media.review_candidates c
              where c.generation=p_generation and c.cleanup_token=p_token
                and c.cleanup_lease_until>statement_timestamp() and c.cleanup_requested_at is not null
                and c.deleted_at is null for update;
          elsif p_area=2 then
            select true into v_current from media.review_promotions c
              where c.generation=p_generation and c.cleanup_token=p_token
                and c.cleanup_lease_until>statement_timestamp() and c.cleanup_requested_at is not null
                and c.committed_at is null and c.deleted_at is null for update;
          else
            raise exception 'Invalid sweep area' using errcode='22023';
          end if;
          if v_current is distinct from true or not exists(select 1 from media.private_generation_bindings b
            where b.generation=p_generation and b.area=p_area and b.domain=p_domain and b.epoch=p_epoch
              and b.layout=p_layout and b.manifest_hash=p_hash) then
            raise exception 'Sweep ownership or binding lost' using errcode='23514';
          end if;
          insert into media.private_generation_proofs
            (id,generation,area,domain,epoch,layout,manifest_hash,cleanup_token,closed_revision)
            values(p_id,p_generation,p_area,p_domain,p_epoch,p_layout,p_hash,p_token,p_revision)
            on conflict(generation,area,cleanup_token) do nothing;
          select id into v_id from media.private_generation_proofs
            where generation=p_generation and area=p_area and cleanup_token=p_token
              and closed_revision=p_revision and manifest_hash=p_hash;
          if v_id is null then raise exception 'Sweep evidence mismatch' using errcode='23514'; end if;
          return v_id;
        end $$;
        revoke all on function media.record_private_generation_sweep(uuid,uuid,integer,uuid,bigint,integer,text,uuid,bigint)
          from public,duora_app;

        -- Feature-off migration applies without creating any role, LOGIN or secret.
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_storage_verifier') then
            grant usage on schema media to duora_storage_verifier;
            grant select on media.private_generation_bindings,media.review_candidates,media.review_candidate_variants,
              media.review_promotions,media.review_promotion_variants,media.uploads,media.photo_deletion_cleanup
              to duora_storage_verifier;
            grant execute on function media.record_private_generation_sweep
              (uuid,uuid,integer,uuid,bigint,integer,text,uuid,bigint) to duora_storage_verifier;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve private generation bindings and revocations; use a reviewed compatible forward change.");
}
