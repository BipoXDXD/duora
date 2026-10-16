using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgeVerificationWithoutExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_age_verifications_verified_expiry",
                schema: "identity",
                table: "age_verifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_age_verifications_verified_expiry",
                schema: "identity",
                table: "age_verifications",
                sql: "state <> 'VerifiedAdult' OR expires_at IS NOT NULL");
        }
    }
}
