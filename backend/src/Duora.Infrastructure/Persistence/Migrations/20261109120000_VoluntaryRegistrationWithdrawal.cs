using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// API-E04 (ADR-0053): a retirada voluntária grava quando e de qual estado a pessoa saiu, sem suspensão vinculada. Só
/// amplia <c>ck_registrations_withdrawal</c>: o ramo com histórico deixa de exigir a suspensão e o ramo legado, tudo nulo,
/// continua. Toda linha válida antes segue válida, e o binário anterior continua gravando o que gravava.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261109120000_VoluntaryRegistrationWithdrawal")]
public sealed class VoluntaryRegistrationWithdrawal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table events.registrations drop constraint ck_registrations_withdrawal;
        alter table events.registrations add constraint ck_registrations_withdrawal check (
          (withdrawn_by_suspension_id is null and withdrawn_at is null
            and withdrawn_from_state is null and withdrawn_waitlist_position is null)
          or (withdrawn_at is not null and isfinite(withdrawn_at)
            and state='Withdrawn' and withdrawn_from_state is not null
            and withdrawn_from_state in ('Waitlisted','Confirmed','CheckedIn')
            and ((withdrawn_from_state='Waitlisted' and withdrawn_waitlist_position is not null and withdrawn_waitlist_position > 0)
              or (withdrawn_from_state<>'Waitlisted' and withdrawn_waitlist_position is null))));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve voluntary withdrawal history; use a compatible release or a reviewed forward migration.");
}
