using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Index only; no enrollment, backfill, decision, grant or existing-row mutation.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261024220000_ApprovedCandidateCleanupIndex")]
public sealed class ApprovedCandidateCleanupIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create index ix_review_candidates_approved_cleanup
          on media.review_candidates(cleanup_available_at,generation)
          where committed_at is not null and cleanup_requested_at is not null and deleted_at is null;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop index media.ix_review_candidates_approved_cleanup;
        """);
}
