using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// P3 da #173 (ADR-0035, decisões de 29/10/2026): retenção de 1 ano dos tombstones e recibos de exclusão de foto.
/// Aditiva: cria só a função de expurgo; nenhuma linha muda e <c>duora_app</c> continua sem UPDATE/DELETE/TRUNCATE nas
/// duas tabelas. A função roda como dona das tabelas (<c>SECURITY DEFINER</c>), mas só apaga o que a política manda:
/// mais de 1 ano no relógio do banco, sem obrigação física pendente, em lote limitado.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261029150000_PhotoDeletionRecordRetention")]
public sealed class PhotoDeletionRecordRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create function media.purge_photo_deletion_records(p_as_of timestamptz, p_limit integer)
        returns table (receipts integer, tombstones integer)
        language plpgsql
        security definer
        set search_path = pg_catalog, pg_temp
        set "TimeZone" = 'UTC'
        set lock_timeout = '500ms'
        as $function$
        declare
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
          -- enquanto houver uma, ele fica, por mais velho que seja.
          with doomed as (
            select t.photo_id from media.photo_tombstones t
            where t.deleted_at <= v_cutoff
              and not exists (select 1 from media.photo_deletion_cleanup c where c.photo_id = t.photo_id)
              and not exists (select 1 from media.review_candidates rc
                              where rc.upload_id = t.photo_id and rc.deleted_at is null)
            order by t.deleted_at, t.photo_id
            limit p_limit
            for update of t skip locked)
          delete from media.photo_tombstones t using doomed where t.photo_id = doomed.photo_id;
          get diagnostics tombstones = row_count;

          return next;
        end
        $function$;

        revoke all on function media.purge_photo_deletion_records(timestamptz, integer) from public;
        grant execute on function media.purge_photo_deletion_records(timestamptz, integer) to duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "drop function if exists media.purge_photo_deletion_records(timestamptz, integer);");
}
