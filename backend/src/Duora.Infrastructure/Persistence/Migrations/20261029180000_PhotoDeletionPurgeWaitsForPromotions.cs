using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// #297 (ADR-0035, P3): o expurgo de tombstones passa a esperar também a promoção privada não confirmada
/// (<c>media.review_promotions</c> com <c>committed_at</c> nulo), com a mesma regra do candidato: fica enquanto ela não
/// for removida e por mais 65 dias (janela de backup) depois da remoção no relógio do banco. O filtro não exige linha em
/// <c>review_promotion_variants</c>: as variantes nascem na mesma transação da promoção, antes de qualquer byte, e o
/// coletor apaga as três chaves da geração de qualquer forma. Aditiva:
/// só substitui o corpo da função, com a mesma assinatura, dono e grants de
/// <see cref="PhotoDeletionRecordRetention"/>; nenhuma linha muda. O <c>Down</c> devolve o corpo anterior.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261029180000_PhotoDeletionPurgeWaitsForPromotions")]
public sealed class PhotoDeletionPurgeWaitsForPromotions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create or replace function media.purge_photo_deletion_records(p_as_of timestamptz, p_limit integer)
        returns table (receipts integer, tombstones integer)
        language plpgsql
        security definer
        set search_path = pg_catalog, pg_temp
        set "TimeZone" = 'UTC'
        set lock_timeout = '500ms'
        as $function$
        declare
          -- Janela de backup de docs/privacy/retention.md: 14 dias de recuperação + 21 de lock + 30 do lifecycle.
          c_backup_window constant interval := interval '65 days';
          v_cutoff timestamptz;
        begin
          if p_as_of is null or not isfinite(p_as_of) then
            raise exception 'p_as_of must be a finite instant' using errcode = '22023';
          end if;
          if p_limit is null or p_limit < 1 or p_limit > 1000 then
            raise exception 'p_limit must be between 1 and 1000' using errcode = '22023';
          end if;

          -- O relógio do banco é teto do instante pedido: nenhum chamador, nem com relógio adiantado ou comprometido,
          -- torna elegível uma exclusão com menos de 1 ano, e 1 ano cobre os ~65 dias em que um backup a desfaria.
          -- O fuso UTC fixo deixa "1 year" igual ao AddYears(-1) do .NET, sem depender do fuso da sessão.
          v_cutoff := least(p_as_of, statement_timestamp()) - interval '1 year';

          with doomed as (
            select r.operation_key from media.photo_deletion_commands r
            where r.created_at <= v_cutoff
            order by r.created_at, r.operation_key
            limit p_limit
            for update skip locked)
          delete from media.photo_deletion_commands r using doomed where r.operation_key = doomed.operation_key;
          get diagnostics receipts = row_count;

          -- O tombstone explica obrigações físicas ainda pendentes (bytes no disco entram em cada novo backup de mídia):
          -- enquanto houver uma, ele fica, por mais velho que seja. Um candidato ou uma promoção não confirmada
          -- (bytes em review_promotion_variants) só libera o tombstone depois da janela de backup, contada da remoção
          -- física, cujo deleted_at os coletores gravam com o relógio do banco.
          with doomed as (
            select t.photo_id from media.photo_tombstones t
            where t.deleted_at <= v_cutoff
              and not exists (select 1 from media.photo_deletion_cleanup c where c.photo_id = t.photo_id)
              and not exists (select 1 from media.original_cleanup o where o.upload_id = t.photo_id)
              and not exists (select 1 from media.review_candidates rc
                              where rc.upload_id = t.photo_id
                                and (rc.deleted_at is null
                                  or rc.deleted_at > statement_timestamp() - c_backup_window))
              and not exists (select 1 from media.review_promotions p
                              where p.upload_id = t.photo_id and p.committed_at is null
                                and (p.deleted_at is null
                                  or p.deleted_at > statement_timestamp() - c_backup_window))
            order by t.deleted_at, t.photo_id
            limit p_limit
            for update of t skip locked)
          delete from media.photo_tombstones t using doomed where t.photo_id = doomed.photo_id;
          get diagnostics tombstones = row_count;

          return next;
        end
        $function$;

        alter function media.purge_photo_deletion_records(timestamptz, integer) owner to duora_migrator;
        revoke all on function media.purge_photo_deletion_records(timestamptz, integer) from public;
        grant execute on function media.purge_photo_deletion_records(timestamptz, integer) to duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create or replace function media.purge_photo_deletion_records(p_as_of timestamptz, p_limit integer)
        returns table (receipts integer, tombstones integer)
        language plpgsql
        security definer
        set search_path = pg_catalog, pg_temp
        set "TimeZone" = 'UTC'
        set lock_timeout = '500ms'
        as $function$
        declare
          -- Janela de backup de docs/privacy/retention.md: 14 dias de recuperação + 21 de lock + 30 do lifecycle.
          c_backup_window constant interval := interval '65 days';
          v_cutoff timestamptz;
        begin
          if p_as_of is null or not isfinite(p_as_of) then
            raise exception 'p_as_of must be a finite instant' using errcode = '22023';
          end if;
          if p_limit is null or p_limit < 1 or p_limit > 1000 then
            raise exception 'p_limit must be between 1 and 1000' using errcode = '22023';
          end if;

          -- O relógio do banco é teto do instante pedido: nenhum chamador, nem com relógio adiantado ou comprometido,
          -- torna elegível uma exclusão com menos de 1 ano, e 1 ano cobre os ~65 dias em que um backup a desfaria.
          -- O fuso UTC fixo deixa "1 year" igual ao AddYears(-1) do .NET, sem depender do fuso da sessão.
          v_cutoff := least(p_as_of, statement_timestamp()) - interval '1 year';

          with doomed as (
            select r.operation_key from media.photo_deletion_commands r
            where r.created_at <= v_cutoff
            order by r.created_at, r.operation_key
            limit p_limit
            for update skip locked)
          delete from media.photo_deletion_commands r using doomed where r.operation_key = doomed.operation_key;
          get diagnostics receipts = row_count;

          -- O tombstone explica obrigações físicas ainda pendentes (bytes no disco entram em cada novo backup de mídia):
          -- enquanto houver uma, ele fica, por mais velho que seja. Um candidato removido só libera o tombstone depois
          -- da janela de backup, contada da remoção física no relógio do banco.
          with doomed as (
            select t.photo_id from media.photo_tombstones t
            where t.deleted_at <= v_cutoff
              and not exists (select 1 from media.photo_deletion_cleanup c where c.photo_id = t.photo_id)
              and not exists (select 1 from media.original_cleanup o where o.upload_id = t.photo_id)
              and not exists (select 1 from media.review_candidates rc
                              where rc.upload_id = t.photo_id
                                and (rc.deleted_at is null
                                  or rc.deleted_at > statement_timestamp() - c_backup_window))
            order by t.deleted_at, t.photo_id
            limit p_limit
            for update of t skip locked)
          delete from media.photo_tombstones t using doomed where t.photo_id = doomed.photo_id;
          get diagnostics tombstones = row_count;

          return next;
        end
        $function$;

        alter function media.purge_photo_deletion_records(timestamptz, integer) owner to duora_migrator;
        revoke all on function media.purge_photo_deletion_records(timestamptz, integer) from public;
        grant execute on function media.purge_photo_deletion_records(timestamptz, integer) to duora_app;
        """);
}
