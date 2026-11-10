using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.ArchitectureTests.Migrations;

/// <summary>
/// Critério da guarda com migrations fictícias: cada fixture é o <c>Up</c> de uma migration montado pelo mesmo
/// <see cref="MigrationBuilder"/> que o EF usa, sem banco.
/// </summary>
public sealed class DestructiveMigrationGuardTests
{
    private const string FixtureId = "20990101000000_Fixture";
    private const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public static TheoryData<string, Action<MigrationBuilder>> BuilderOperations => new()
    {
        { "DropTable", b => b.DropTable(name: "t", schema: "s") },
        { "DropColumn", b => b.DropColumn(name: "c", table: "t", schema: "s") },
        { "AlterColumn", b => b.AlterColumn<long>(name: "c", table: "t", schema: "s") },
        { "RenameColumn", b => b.RenameColumn(name: "a", table: "t", newName: "b", schema: "s") },
        { "RenameTable", b => b.RenameTable(name: "t", schema: "s", newName: "u") },
        { "RenameIndex", b => b.RenameIndex(name: "ix_a", newName: "ix_b", table: "t", schema: "s") },
        { "DropCheckConstraint", b => b.DropCheckConstraint(name: "ck_t", table: "t", schema: "s") },
        { "DropForeignKey", b => b.DropForeignKey(name: "fk_t", table: "t", schema: "s") },
        { "DropPrimaryKey", b => b.DropPrimaryKey(name: "pk_t", table: "t", schema: "s") },
        { "DropUniqueConstraint", b => b.DropUniqueConstraint(name: "ak_t", table: "t", schema: "s") },
        { "DropIndex", b => b.DropIndex(name: "ix_t", table: "t", schema: "s") },
        { "DropSchema", b => b.DropSchema(name: "s") },
        { "DropSequence", b => b.DropSequence(name: "seq", schema: "s") },
        { "DeleteData", b => b.DeleteData(table: "t", keyColumn: "id", keyValue: 1, schema: "s") },
    };

    public static TheoryData<string, string> SqlStatements => new()
    {
        { "SQL DROP TABLE", "DROP TABLE s.t;" },
        { "SQL DROP TABLE", "drop table if exists s.t;" },
        { "SQL DROP COLUMN", "ALTER TABLE s.t DROP COLUMN c;" },
        { "SQL DROP COLUMN", "ALTER TABLE s.t DROP c;" },
        { "SQL DROP COLUMN", "ALTER TABLE s.t DROP IF EXISTS \"c\";" },
        { "SQL DROP SCHEMA", "DROP SCHEMA s CASCADE;" },
        { "SQL DROP CONSTRAINT", "ALTER TABLE s.t DROP CONSTRAINT ck_t;" },
        { "SQL DROP TRIGGER", "DROP TRIGGER tg ON s.t;" },
        { "SQL DROP MATERIALIZED VIEW", "DROP MATERIALIZED VIEW s.v;" },
        { "SQL TRUNCATE", "TRUNCATE s.t;" },
        { "SQL TRUNCATE", "TRUNCATE TABLE ONLY s.t, s.u;" },
        { "SQL DELETE sem WHERE", "DELETE FROM s.t;" },
        { "SQL DELETE sem WHERE", "DELETE FROM s.t" },
        { "SQL DELETE sem WHERE", "DELETE FROM s.a; DELETE FROM s.b WHERE id = 1;" },
        { "SQL ALTER COLUMN TYPE", "ALTER TABLE s.t ALTER COLUMN c TYPE bigint;" },
        { "SQL ALTER COLUMN TYPE", "ALTER TABLE s.t ALTER \"c\" SET DATA TYPE bigint;" },
        { "SQL RENAME", "ALTER TABLE s.t RENAME TO u;" },
        { "SQL RENAME", "ALTER TABLE s.t RENAME COLUMN a TO b;" },
    };

    [Theory]
    [MemberData(nameof(BuilderOperations))]
    public void ADestructiveBuilderOperationOutsideTheAllowlistFailsNamingFileAndOperation(
        string operation, Action<MigrationBuilder> up)
    {
        var violation = Assert.Single(Violations(Fixture(up), allowlist: []));

        Assert.Equal(
            $"backend/src/Duora.Infrastructure/Persistence/Migrations/{FixtureId}.cs: {operation} fora da allowlist "
                + "(backend/tests/Duora.ArchitectureTests/Migrations/destructive-migrations.allowlist)",
            violation);
    }

    [Theory]
    [MemberData(nameof(SqlStatements))]
    public void ADestructiveSqlStatementOutsideTheAllowlistFailsNamingFileAndOperation(string operation, string sql)
    {
        var violation = Assert.Single(Violations(Fixture(b => b.Sql(sql)), allowlist: []));

        Assert.StartsWith($"backend/src/Duora.Infrastructure/Persistence/Migrations/{FixtureId}.cs: ", violation);
        Assert.Contains($": {operation} fora da allowlist", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE TABLE s.t (id bigint PRIMARY KEY);")]
    [InlineData("DELETE FROM s.t WHERE created_at < now() - interval '30 days';")]
    [InlineData("ALTER TABLE s.t ALTER COLUMN c DROP NOT NULL;")]
    [InlineData("ALTER TABLE s.t ALTER COLUMN c DROP DEFAULT;")]
    [InlineData("ALTER TABLE s.t ALTER COLUMN c DROP IDENTITY IF EXISTS;")]
    [InlineData("ALTER TABLE s.t ADD CONSTRAINT ck_t CHECK (c > 0);")]
    [InlineData("REVOKE UPDATE, DELETE, TRUNCATE ON TABLE s.t FROM app;")]
    [InlineData("revoke delete,truncate on s.t from app;")]
    [InlineData("-- DROP TABLE s.t;\nCREATE INDEX ix_t ON s.t (c);")]
    [InlineData("/* TRUNCATE s.t; DROP SCHEMA s; */ SELECT 1;")]
    [InlineData("SELECT 1; -- ALTER TABLE s.t RENAME TO u")]
    public void AnAdditiveSqlStatementOrACommentPasses(string sql)
    {
        Assert.Empty(Violations(Fixture(b => b.Sql(sql)), allowlist: []));
    }

    [Fact]
    public void AdditiveBuilderOperationsPass()
    {
        var up = Fixture(b =>
        {
            b.EnsureSchema(name: "s");
            b.CreateTable(name: "t", schema: "s", columns: table => new { id = table.Column<long>(type: "bigint") });
            b.AddColumn<string>(name: "c", table: "t", schema: "s", nullable: true);
            b.AddCheckConstraint(name: "ck_t", table: "t", schema: "s", sql: "c <> ''");
            b.CreateIndex(name: "ix_t", table: "t", schema: "s", column: "c");
        });

        Assert.Empty(Violations(up, allowlist: []));
    }

    [Fact]
    public void ADestructiveOperationInTheAllowlistPasses()
    {
        var up = Fixture(b => b.DropColumn(name: "c", table: "t", schema: "s"));

        Assert.Empty(Violations(up, [new AllowedOperation(FixtureId, "DropColumn", "contract revisado")]));
    }

    [Fact]
    public void TheAllowlistCoversOnlyTheOperationItNames()
    {
        var up = Fixture(b =>
        {
            b.DropColumn(name: "c", table: "t", schema: "s");
            b.Sql("TRUNCATE s.t;");
        });

        var violation = Assert.Single(Violations(up, [new AllowedOperation(FixtureId, "DropColumn", "contract revisado")]));
        Assert.Contains(": SQL TRUNCATE fora da allowlist", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAllowlistEntryWithoutAMatchingOperationFails()
    {
        var up = Fixture(b => b.AddColumn<string>(name: "c", table: "t", schema: "s", nullable: true));

        var violation = Assert.Single(Violations(up, [new AllowedOperation("20990102000000_Gone", "DropTable", "já aplicada")]));

        Assert.Equal(
            "backend/tests/Duora.ArchitectureTests/Migrations/destructive-migrations.allowlist: "
                + "'20990102000000_Gone | DropTable' não corresponde a nenhuma operação destrutiva; remova a linha",
            violation);
    }

    [Fact]
    public void TheAllowlistParsesEntriesAndSkipsCommentsAndBlankLines()
    {
        var entries = DestructiveMigrationAllowlist.Parse("""
            # cabeçalho

            20261015192439_Identity | DropTable | já aplicada em produção
            """);

        Assert.Equal([new AllowedOperation("20261015192439_Identity", "DropTable", "já aplicada em produção")], entries);
    }

    [Theory]
    [InlineData("20261015192439_Identity | DropTable")]
    [InlineData("20261015192439_Identity | DropTable |   ")]
    [InlineData("20261015192439_Identity | | justificativa")]
    [InlineData("20261015192439_Identity | DropTable | a | b")]
    public void AnAllowlistLineWithoutThreeFilledFieldsIsRejected(string line)
    {
        var error = Assert.Throws<FormatException>(() => DestructiveMigrationAllowlist.Parse(line));

        Assert.Contains("linha 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicatedAllowlistEntryIsRejected()
    {
        var error = Assert.Throws<FormatException>(() => DestructiveMigrationAllowlist.Parse("""
            20261015192439_Identity | DropTable | um
            20261015192439_Identity | DropTable | dois
            """));

        Assert.Contains("linha 2", error.Message, StringComparison.Ordinal);
    }

    private static MigrationUp Fixture(Action<MigrationBuilder> up)
    {
        var builder = new MigrationBuilder(Provider);
        up(builder);
        return new MigrationUp(FixtureId, builder.Operations);
    }

    private static IReadOnlyList<string> Violations(MigrationUp migration, IReadOnlyList<AllowedOperation> allowlist) =>
        DestructiveMigrationGuard.FindViolations([migration], allowlist);
}
