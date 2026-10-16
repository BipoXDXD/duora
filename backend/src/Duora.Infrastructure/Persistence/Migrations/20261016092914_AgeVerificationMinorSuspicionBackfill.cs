using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgeVerificationMinorSuspicionBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Suspeita de menor recebida antes da coluna (ADR-0024). Falha fechada: toda Rejected, porque só NotAdult
            // produz esse estado, e toda solicitação citada na auditoria de NotAdult tardio (primeiro instante).
            migrationBuilder.Sql(
                """
                update identity.age_verifications
                set minor_suspected_at = coalesce(decided_at, requested_at)
                where state = 'Rejected' and minor_suspected_at is null;

                update identity.age_verifications v
                set minor_suspected_at = late.first_at
                from (
                    select target_id, min(occurred_at) as first_at
                    from ops.audit_events
                    where action = 'identity.age_verification_late_not_adult' and target_type = 'age_verification'
                    group by target_id) late
                where v.id = late.target_id and v.minor_suspected_at is null;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Migração de dados sem volta: apagar suspeita de menor seria falha aberta. O migrador nunca roda Down.
        }
    }
}
