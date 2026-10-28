using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Contract da issue #78: retira a cópia legada <c>region_code</c>, seu CHECK e o trigger de ponte do expand
    /// (20261021025720_ProfileLocation). A API continua derivando <c>regionCode</c> de país + subdivisão. Nenhum
    /// binário em serviço lê ou escreve a coluna; o anterior detecta sua ausência a cada gravação.
    /// </summary>
    public partial class ProfileRegionContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Falha rápido em vez de enfileirar o tráfego atrás do ACCESS EXCLUSIVE; o migrator pode repetir.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                DROP TRIGGER sync_legacy_region ON profiles.profiles;
                DROP FUNCTION profiles.sync_legacy_region();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_profiles_region_code",
                schema: "profiles",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "region_code",
                schema: "profiles",
                table: "profiles");
        }

        /// <summary>
        /// Recuo compatível: devolve coluna, CHECK e trigger como o expand os deixou, com a cópia legada recomposta da
        /// localização canônica (só UF do Brasil). Não altera país, subdivisão, versão nem <c>updated_at</c>.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "region_code",
                schema: "profiles",
                table: "profiles",
                type: "text",
                nullable: true);

            // Antes do trigger, para o backfill não passar pela ponte.
            migrationBuilder.Sql("""
                UPDATE profiles.profiles
                SET region_code = subdivision_code
                WHERE country_code = 'BR' AND subdivision_code IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_profiles_region_code",
                schema: "profiles",
                table: "profiles",
                sql: "region_code IN ('BR-AC', 'BR-AL', 'BR-AM', 'BR-AP', 'BR-BA', 'BR-CE', 'BR-DF', 'BR-ES', 'BR-GO', 'BR-MA', 'BR-MG', 'BR-MS', 'BR-MT', 'BR-PA', 'BR-PB', 'BR-PE', 'BR-PI', 'BR-PR', 'BR-RJ', 'BR-RN', 'BR-RO', 'BR-RR', 'BR-RS', 'BR-SC', 'BR-SE', 'BR-SP', 'BR-TO')");

            // Corpo idêntico ao de 20261021025720_ProfileLocation.
            migrationBuilder.Sql("""
                CREATE FUNCTION profiles.sync_legacy_region() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.country_code IS NULL AND NEW.subdivision_code IS NULL AND NEW.region_code IS NOT NULL THEN
                            NEW.country_code := 'BR';
                            NEW.subdivision_code := NEW.region_code;
                        END IF;
                    ELSIF NEW.region_code IS DISTINCT FROM OLD.region_code
                        AND NEW.country_code IS NOT DISTINCT FROM OLD.country_code
                        AND NEW.subdivision_code IS NOT DISTINCT FROM OLD.subdivision_code THEN
                        NEW.country_code := CASE WHEN NEW.region_code IS NULL THEN NULL ELSE 'BR' END;
                        NEW.subdivision_code := NEW.region_code;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER sync_legacy_region BEFORE INSERT OR UPDATE ON profiles.profiles
                FOR EACH ROW EXECUTE FUNCTION profiles.sync_legacy_region();
                """);
        }
    }
}
