using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegionalPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "regional_preferences",
                schema: "profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_regional_preferences", x => x.user_id);
                    table.CheckConstraint("ck_regional_preferences_mode", "mode IN ('countries', 'unrestricted')");
                    table.CheckConstraint("ck_regional_preferences_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_regional_preferences_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "regional_preference_countries",
                schema: "profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_regional_preference_countries", x => new { x.user_id, x.country_code });
                    table.ForeignKey(
                        name: "fk_regional_preference_countries_catalog",
                        column: x => x.country_code,
                        principalSchema: "profiles",
                        principalTable: "countries",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_regional_preference_countries_preference",
                        column: x => x.user_id,
                        principalSchema: "profiles",
                        principalTable: "regional_preferences",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_regional_preference_countries_country_code",
                schema: "profiles",
                table: "regional_preference_countries",
                column: "country_code");
            migrationBuilder.Sql("""
                -- Serialize direct child writes with mode changes before checking the final transaction state.
                create function profiles.lock_regional_preference_parent() returns trigger
                language plpgsql set search_path = pg_catalog as $$
                declare previous_id uuid; next_id uuid;
                begin
                  if TG_OP <> 'INSERT' then previous_id := OLD.user_id; end if;
                  if TG_OP <> 'DELETE' then next_id := NEW.user_id; end if;
                  perform 1 from profiles.regional_preferences
                    where user_id in (previous_id, next_id) order by user_id for update;
                  if TG_OP = 'DELETE' then return OLD; end if;
                  return NEW;
                end $$;
                create trigger lock_regional_preference_parent
                  before insert or update or delete on profiles.regional_preference_countries
                  for each row execute function profiles.lock_regional_preference_parent();

                create function profiles.check_regional_preference_countries() returns trigger
                language plpgsql set search_path = pg_catalog as $$
                declare previous_id uuid; next_id uuid; invalid boolean;
                begin
                  if TG_OP <> 'INSERT' then previous_id := OLD.user_id; end if;
                  if TG_OP <> 'DELETE' then next_id := NEW.user_id; end if;
                  select exists (
                    select 1 from profiles.regional_preferences p
                    where p.user_id in (previous_id, next_id)
                      and ((p.mode = 'countries') <> exists (
                        select 1 from profiles.regional_preference_countries c where c.user_id = p.user_id))
                  ) into invalid;
                  if invalid then
                    raise exception 'Inconsistent regional preference.'
                      using errcode = '23514', constraint = 'ck_regional_preferences_countries';
                  end if;
                  return null;
                end $$;
                create constraint trigger check_regional_preferences_countries
                  after insert or update or delete on profiles.regional_preferences
                  deferrable initially deferred for each row
                  execute function profiles.check_regional_preference_countries();
                create constraint trigger check_regional_preference_country_rows
                  after insert or update or delete on profiles.regional_preference_countries
                  deferrable initially deferred for each row
                  execute function profiles.check_regional_preference_countries();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
            "Preserve regional preferences and schema; revert with a reviewed forward migration.");
    }
}
