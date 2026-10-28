using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// API-M03: tombstone, recibo idempotente e obrigações de limpeza física da exclusão pelo titular. Aditiva: começa
/// vazia, sem backfill, sem varrer nem apagar mídia existente.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261028090000_PhotoDeletions")]
public sealed class PhotoDeletions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table media.photo_tombstones (
          photo_id uuid constraint pk_photo_tombstones primary key,
          owner_id uuid not null,
          operation_key varchar(64) not null,
          intent_sha256 bytea not null,
          deleted_version bigint not null,
          deleted_at timestamptz not null,
          constraint fk_photo_tombstones_upload foreign key (photo_id) references media.uploads(id) on delete restrict,
          constraint fk_photo_tombstones_owner foreign key (owner_id) references identity.users(id) on delete restrict,
          constraint ux_photo_tombstones_operation unique (operation_key),
          constraint ck_photo_tombstones_operation check (operation_key ~ '^[0-9a-f]{64}$'),
          constraint ck_photo_tombstones_intent check (octet_length(intent_sha256) = 32),
          constraint ck_photo_tombstones_version check (deleted_version >= 2),
          constraint ck_photo_tombstones_deleted_at check (isfinite(deleted_at)));
        create index ix_photo_tombstones_owner on media.photo_tombstones (owner_id);

        create table media.photo_deletion_commands (
          operation_key varchar(64) constraint pk_photo_deletion_commands primary key,
          owner_id uuid not null,
          key_id varchar(64) not null,
          photo_id uuid not null,
          intent_sha256 bytea not null,
          snapshot_sha256 bytea not null,
          expected_version bigint not null,
          result_version bigint not null,
          created_at timestamptz not null,
          constraint fk_photo_deletion_commands_owner foreign key (owner_id) references identity.users(id) on delete restrict,
          constraint fk_photo_deletion_commands_upload foreign key (photo_id) references media.uploads(id) on delete restrict,
          constraint ck_photo_deletion_commands_operation check (operation_key ~ '^[0-9a-f]{64}$'),
          constraint ck_photo_deletion_commands_key_id check (key_id ~ '^[A-Za-z0-9_-]{1,64}$'),
          constraint ck_photo_deletion_commands_hashes check (octet_length(intent_sha256) = 32
            and octet_length(snapshot_sha256) = 32),
          constraint ck_photo_deletion_commands_versions check (expected_version >= 1
            and result_version >= expected_version),
          constraint ck_photo_deletion_commands_created_at check (isfinite(created_at)));
        create index ix_photo_deletion_commands_owner on media.photo_deletion_commands (owner_id);
        create index ix_photo_deletion_commands_photo on media.photo_deletion_commands (photo_id);

        create table media.photo_deletion_cleanup (
          object_key varchar(128) constraint pk_photo_deletion_cleanup primary key,
          photo_id uuid not null,
          created_at timestamptz not null,
          available_at timestamptz not null,
          attempts integer not null default 0,
          fencing_token uuid null,
          lease_until timestamptz null,
          constraint fk_photo_deletion_cleanup_tombstone foreign key (photo_id)
            references media.photo_tombstones(photo_id) on delete restrict,
          constraint ck_photo_deletion_cleanup_key check (object_key ~ '^quarantine/[0-9a-f]{32}$'
            or object_key ~ '^derivatives/(review-)?[0-9a-f]{32}-(thumb|card|full)$'),
          constraint ck_photo_deletion_cleanup_attempts check (attempts >= 0),
          constraint ck_photo_deletion_cleanup_dates check (isfinite(created_at) and isfinite(available_at)
            and (lease_until is null or isfinite(lease_until))),
          constraint ck_photo_deletion_cleanup_lease check ((lease_until is null) = (fencing_token is null)));
        create index ix_photo_deletion_cleanup_available on media.photo_deletion_cleanup (available_at, object_key);
        create index ix_photo_deletion_cleanup_photo on media.photo_deletion_cleanup (photo_id);

        -- Tombstone e recibo confirmados são imutáveis para a aplicação; só a obrigação de limpeza é removida.
        revoke update, delete, truncate on media.photo_tombstones from duora_app;
        revoke update, delete, truncate on media.photo_deletion_commands from duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve tombstones, receipts and cleanup obligations; use a compatible release or a reviewed forward migration.");
}
