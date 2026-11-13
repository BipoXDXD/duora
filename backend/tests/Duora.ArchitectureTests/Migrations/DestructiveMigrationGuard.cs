using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Duora.ArchitectureTests.Migrations;

/// <summary>O <c>Up</c> de uma migration, já expandido em operações pelo <c>MigrationBuilder</c>.</summary>
internal sealed record MigrationUp(string Id, IReadOnlyList<MigrationOperation> Operations);

/// <summary>Uma linha da allowlist: a operação destrutiva revisada naquela migration e o porquê.</summary>
internal sealed record AllowedOperation(string MigrationId, string Operation, string Justification);

/// <summary>
/// Recusa operação destrutiva no <c>Up</c> de uma migration fora da allowlist revisada. Só o <c>Up</c> conta: o
/// migrador nunca roda <c>Down</c> (docs/operations/deploy.md). Critério:
/// <list type="bullet">
/// <item>operação do <c>MigrationBuilder</c> cujo tipo começa com <c>Drop</c> ou <c>Rename</c>, mais
/// <c>AlterColumn</c> e <c>DeleteData</c>;</item>
/// <item>SQL de <c>Sql(...)</c>, sem os comentários: <c>DROP</c> de qualquer objeto (exceto <c>DROP DEFAULT</c>,
/// <c>DROP NOT NULL</c>, <c>DROP IDENTITY</c> e <c>DROP EXPRESSION</c>, que mudam a coluna sem apagar valor),
/// <c>TRUNCATE</c>, <c>DELETE</c> sem <c>WHERE</c>, troca de tipo de coluna e <c>RENAME</c>.</item>
/// </list>
/// </summary>
internal static partial class DestructiveMigrationGuard
{
    public const string MigrationsDirectory = "backend/src/Duora.Infrastructure/Persistence/Migrations";
    public const string AllowlistPath = "backend/tests/Duora.ArchitectureTests/Migrations/destructive-migrations.allowlist";

    private static readonly HashSet<string> DroppableObjects = new(StringComparer.Ordinal)
    {
        "ACCESS", "AGGREGATE", "CAST", "COLLATION", "COLUMN", "CONSTRAINT", "CONVERSION", "DATABASE", "DOMAIN",
        "EVENT", "EXTENSION", "FOREIGN", "FUNCTION", "GROUP", "INDEX", "LANGUAGE", "MATERIALIZED VIEW", "OPERATOR",
        "OWNED", "POLICY", "PROCEDURE", "PUBLICATION", "ROLE", "ROUTINE", "RULE", "SCHEMA", "SEQUENCE", "SERVER",
        "STATISTICS", "SUBSCRIPTION", "TABLE", "TABLESPACE", "TEXT", "TRANSFORM", "TRIGGER", "TYPE", "USER", "VIEW",
    };

    public static IReadOnlyList<string> FindViolations(
        IEnumerable<MigrationUp> migrations, IReadOnlyList<AllowedOperation> allowlist)
    {
        var allowed = allowlist.Select(entry => (entry.MigrationId, entry.Operation)).ToHashSet();
        var found = migrations
            .SelectMany(migration => DestructiveOperationsOf(migration.Operations)
                .Select(operation => (MigrationId: migration.Id, Operation: operation)))
            .ToHashSet();

        var unreviewed = found
            .Where(item => !allowed.Contains(item))
            .Select(item => $"{MigrationsDirectory}/{item.MigrationId}.cs: {item.Operation} fora da allowlist ({AllowlistPath})");
        var stale = allowed
            .Where(item => !found.Contains(item))
            .Select(item => $"{AllowlistPath}: '{item.MigrationId} | {item.Operation}' não corresponde a nenhuma "
                + "operação destrutiva; remova a linha");

        return [.. unreviewed.Concat(stale).Order(StringComparer.Ordinal)];
    }

    private static IEnumerable<string> DestructiveOperationsOf(IEnumerable<MigrationOperation> operations) =>
        operations.SelectMany(operation => operation is SqlOperation sql
            ? DestructiveStatementsOf(sql.Sql)
            : DestructiveBuilderOperation(operation)).Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> DestructiveBuilderOperation(MigrationOperation operation)
    {
        var name = operation.GetType().Name.Replace("Operation", string.Empty, StringComparison.Ordinal);
        var destructive = name.StartsWith("Drop", StringComparison.Ordinal)
            || name.StartsWith("Rename", StringComparison.Ordinal)
            || name is "AlterColumn" or "DeleteData";

        return destructive ? [name] : [];
    }

    private static IEnumerable<string> DestructiveStatementsOf(string sql)
    {
        var code = Comment().Replace(sql, " ");

        foreach (Match drop in Drop().Matches(code))
        {
            var target = Whitespace().Replace(drop.Groups["target"].Value.ToUpperInvariant(), " ");
            // ALTER TABLE ... DROP [COLUMN] [IF EXISTS] c: sem palavra-chave de objeto, o alvo é uma coluna.
            yield return $"SQL DROP {(DroppableObjects.Contains(target) ? target : "COLUMN")}";
        }

        if (Truncate().IsMatch(code))
        {
            yield return "SQL TRUNCATE";
        }

        if (Delete().Matches(code).Any(delete => !Where().IsMatch(delete.Value)))
        {
            yield return "SQL DELETE sem WHERE";
        }

        if (AlterColumnType().IsMatch(code))
        {
            yield return "SQL ALTER COLUMN TYPE";
        }

        if (Rename().IsMatch(code))
        {
            yield return "SQL RENAME";
        }
    }

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\bDROP\s+(?!DEFAULT\b|NOT\s+NULL\b|IDENTITY\b|EXPRESSION\b)(?<target>MATERIALIZED\s+VIEW|""?\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex Drop();

    // Seguido de vírgula ou ON, TRUNCATE é privilégio de GRANT/REVOKE, não o comando.
    [GeneratedRegex(@"\bTRUNCATE\b(?!\s*,|\s+ON\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Truncate();

    [GeneratedRegex(@"\bDELETE\s+FROM\b[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex Delete();

    [GeneratedRegex(@"\bWHERE\b", RegexOptions.IgnoreCase)]
    private static partial Regex Where();

    [GeneratedRegex(@"\bALTER\s+(?:COLUMN\s+)?(?:""[^""]+""|\w+)\s+(?:SET\s+DATA\s+)?TYPE\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlterColumnType();

    [GeneratedRegex(@"\bRENAME\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rename();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// Formato: <c>&lt;id da migration&gt; | &lt;operação&gt; | &lt;justificativa&gt;</c>, uma por linha; <c>#</c> abre
/// comentário de linha inteira.
/// </summary>
internal static class DestructiveMigrationAllowlist
{
    private const int FieldCount = 3;

    public static IReadOnlyList<AllowedOperation> Parse(string text)
    {
        var entries = new List<AllowedOperation>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var entry = ParseLine(line, index + 1);
            if (entries.Exists(e => e.MigrationId == entry.MigrationId && e.Operation == entry.Operation))
            {
                throw new FormatException($"Allowlist, linha {index + 1}: '{entry.MigrationId} | {entry.Operation}' repetida.");
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static AllowedOperation ParseLine(string line, int lineNumber)
    {
        var fields = line.Split('|', StringSplitOptions.TrimEntries);
        if (fields.Length != FieldCount || fields.Any(field => field.Length == 0))
        {
            throw new FormatException(
                $"Allowlist, linha {lineNumber}: esperado '<migration> | <operação> | <justificativa>', recebido '{line}'.");
        }

        return new AllowedOperation(fields[0], fields[1], fields[2]);
    }
}
