using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261021090000_UnlikelyRoutesCatalog")]
public sealed class UnlikelyRoutesCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table experiences.experience_versions add column experience_kind text not null default 'constellation'
          check(experience_kind in ('constellation','unlikely-routes'));
        create or replace function experiences.keep_published_content() returns trigger language plpgsql as $$
        begin
          if tg_op='DELETE' then
            if old.editorial_state in ('Published','Retired') then raise exception 'Published content is immutable' using errcode='23514'; end if;
            return old;
          end if;
          if old.editorial_state in ('Published','Retired') and
            (new.editorial_state not in ('Published','Retired') or (old.editorial_state='Retired' and new.editorial_state<>'Retired') or new.content_en is distinct from old.content_en or new.content_pt is distinct from old.content_pt or
             new.audio_manifest is distinct from old.audio_manifest or new.approved_by is distinct from old.approved_by or new.published_at is distinct from old.published_at or
             new.experience_kind is distinct from old.experience_kind or new.content_version<>old.content_version or new.rules_version<>old.rules_version or new.schema_version<>old.schema_version)
          then raise exception 'Published content is immutable' using errcode='23514'; end if;
          return new;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Keep the additive catalog discriminator and pinned content; pause matching before binary rollback.");
}
