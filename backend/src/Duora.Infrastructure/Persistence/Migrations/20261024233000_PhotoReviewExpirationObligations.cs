using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Nullable proof for new system-expiration obligations; no enrollment or rewrite of existing rows.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261024233000_PhotoReviewExpirationObligations")]
public sealed class PhotoReviewExpirationObligations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table media.review_candidates
          add column cleanup_audit_id bigint null references ops.audit_events(id) on delete restrict,
          add constraint ck_review_candidates_cleanup_audit
            check (cleanup_audit_id is null or (committed_at is not null and writer_settled_at is not null
              and cleanup_requested_at is not null));
        create index ix_review_candidates_expired_cleanup
          on media.review_candidates(cleanup_available_at,generation)
          where cleanup_audit_id is not null and deleted_at is null;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve audited expiration obligations; use a reviewed compatible forward change.");
}
