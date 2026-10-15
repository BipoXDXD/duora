using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserNameFollowsEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users_normalized_user_name",
                schema: "identity",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name",
                schema: "identity",
                table: "users",
                column: "normalized_user_name");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_user_name_is_email",
                schema: "identity",
                table: "users",
                sql: "normalized_user_name IS NOT DISTINCT FROM normalized_email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_normalized_user_name",
                schema: "identity",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_user_name_is_email",
                schema: "identity",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_user_name",
                schema: "identity",
                table: "users",
                column: "normalized_user_name",
                unique: true);
        }
    }
}
