using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Private immutable recovery receipts survive outbox retention and a human lift (#319).</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261101120000_SelfieSuspensionRecovery")]
public sealed class SelfieSuspensionRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table safety.selfie_suspension_recoveries (
          delivery_id uuid primary key,
          command_key varchar(64) not null unique,
          command_intent bytea not null check (octet_length(command_intent)=32),
          moderator_user_id uuid not null references identity.users(id) on delete restrict,
          subject_user_id uuid not null references identity.users(id) on delete restrict,
          event_id uuid not null,
          verification_id uuid not null,
          incident_reference varchar(64) not null check (incident_reference ~ '^[A-Za-z0-9_-]{1,64}$'),
          internal_reason varchar(2000) not null check (length(btrim(internal_reason)) between 1 and 2000),
          moderation_action_id uuid references safety.moderation_actions(id) on delete restrict,
          disposition varchar(32) not null check (disposition in ('Suspended','ExistingSuspension','NoLongerNeeded','NotSuspendable')),
          recorded_at timestamptz not null check (isfinite(recorded_at)),
          check ((disposition in ('Suspended','ExistingSuspension')) = (moderation_action_id is not null)));
        create index ix_selfie_suspension_recoveries_subject on safety.selfie_suspension_recoveries(subject_user_id);
        create index ix_selfie_suspension_recoveries_moderator on safety.selfie_suspension_recoveries(moderator_user_id);
        create index ix_selfie_suspension_recoveries_action on safety.selfie_suspension_recoveries(moderation_action_id);
        revoke update, delete, truncate on safety.selfie_suspension_recoveries from duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Keep recovery receipts; use a compatible release or a reviewed forward migration.");
}
