using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Issue #305: additive provenance for withdrawal; old timestamps are never inferred.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261031120000_SuspendedRegistrations")]
public sealed class SuspendedRegistrations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table events.registrations
          add column confirmed_at timestamptz null,
          add column withdrawn_at timestamptz null,
          add column withdrawn_by_suspension_id uuid null,
          add column withdrawn_from_state text null,
          add column withdrawn_waitlist_position bigint null,
          add column version bigint not null default 1,
          add constraint fk_registrations_withdrawn_suspension foreign key (withdrawn_by_suspension_id)
            references safety.moderation_actions(id) on delete restrict,
          add constraint ck_registrations_version check (version >= 1),
          add constraint ck_registrations_withdrawal check (
            (withdrawn_by_suspension_id is null and withdrawn_at is null
              and withdrawn_from_state is null and withdrawn_waitlist_position is null)
            or (withdrawn_by_suspension_id is not null and withdrawn_at is not null and isfinite(withdrawn_at)
              and state='Withdrawn' and withdrawn_from_state is not null
              and withdrawn_from_state in ('Waitlisted','Confirmed','CheckedIn')
              and ((withdrawn_from_state='Waitlisted' and withdrawn_waitlist_position is not null and withdrawn_waitlist_position > 0)
                or (withdrawn_from_state<>'Waitlisted' and withdrawn_waitlist_position is null))));
        create index ix_registrations_withdrawn_suspension on events.registrations(withdrawn_by_suspension_id);
        create index ix_registrations_user on events.registrations(user_id);

        create function events.keep_suspension_withdrawal() returns trigger language plpgsql as $$
        begin
          if old.withdrawn_by_suspension_id is not null then
            raise exception 'A suspension withdrawal is historical and cannot be rewritten or deleted' using errcode='23514';
          end if;
          return case when tg_op='DELETE' then old else new end;
        end $$;
        create trigger keep_suspension_withdrawal before update or delete on events.registrations
          for each row execute function events.keep_suspension_withdrawal();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve registration withdrawal history; use a compatible release or a reviewed forward migration.");
}
