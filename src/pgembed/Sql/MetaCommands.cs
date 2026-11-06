using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using Format = PgEmbed.Cli.Infrastructure.Format;

namespace PgEmbed.Cli.Sql;

/// <summary>
/// The psql-style commands that describe the database: \d, \dt, \dv, \df, \dn and \l. They work in the
/// interactive prompt and as one-shot SQL (pgembed sql app "\d books"), and \d shares its reader with
/// pgembed schema.
/// </summary>
internal static class MetaCommands
{
    public static readonly string[] Names = ["\\d", "\\dt", "\\dv", "\\df", "\\dn", "\\l"];

    public static bool IsCommand(string text) => text.TrimStart().StartsWith('\\');

    public static (string Command, string? Argument) Split(string text)
    {
        var parts = text.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 ? parts[1].TrimEnd(';').Trim() : null);
    }

    /// <summary>False when the command is not one of these.</summary>
    public static async Task<bool> TryRunAsync(NpgsqlConnection connection, string command, string? argument, SqlRunner runner, CancellationToken ct)
    {
        switch (command)
        {
            case "\\d" when argument is null:
                ShowRelations(runner, await SchemaReader.ListAsync(connection, SchemaReader.AllRelations, null, null, ct));
                return true;
            case "\\d":
                var table = await SchemaReader.DescribeAsync(connection, argument, ct)
                            ?? throw new PostgresEmbedException($"There is no table, view or sequence named '{argument}'.");
                ShowTable(runner, table);
                return true;
            case "\\dt":
                ShowRelations(runner, await SchemaReader.ListAsync(connection, SchemaReader.Tables, argument, null, ct));
                return true;
            case "\\dv":
                ShowRelations(runner, await SchemaReader.ListAsync(connection, SchemaReader.Views, argument, null, ct));
                return true;
            case "\\dn":
                await runner.QueryAsync(connection, """
                    SELECT nspname AS name, pg_get_userbyid(nspowner) AS owner
                    FROM pg_namespace WHERE nspname <> 'information_schema' AND nspname NOT LIKE 'pg\_%' AND nspname ILIKE $1 ORDER BY 1
                    """, [Like(argument)], null, ct);
                return true;
            case "\\df":
                await runner.QueryAsync(connection, """
                    SELECT n.nspname AS schema, p.proname AS name, pg_get_function_result(p.oid) AS result,
                           pg_get_function_arguments(p.oid) AS arguments
                    FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%' AND p.proname ILIKE $1
                    ORDER BY 1, 2
                    """, [Like(argument)], null, ct);
                return true;
            case "\\l":
                await runner.QueryAsync(connection, """
                    SELECT datname AS name, pg_get_userbyid(datdba) AS owner, pg_encoding_to_char(encoding) AS encoding,
                           pg_size_pretty(pg_database_size(datname)) AS size
                    FROM pg_database WHERE NOT datistemplate ORDER BY 1
                    """, [], null, ct);
                return true;
            default:
                return false;
        }
    }

    public static void ShowRelations(SqlRunner runner, IReadOnlyList<RelationInfo> relations) =>
        runner.Show(null, ["schema", "name", "type", "rows", "size"],
            relations.Select(r => new object?[] { r.Schema, r.Name, r.Type, r.RowEstimate is { } n ? $"~{n}" : null, Format.Bytes(r.SizeBytes) }));

    public static void ShowTable(SqlRunner runner, TableSchema table)
    {
        var heading = $"{table.Schema}.{table.Name} ({table.Type}{(table.RowEstimate is { } n ? $", ~{n} rows" : "")}, {Format.Bytes(table.SizeBytes)})";
        runner.Show(heading, ["column", "type", "nullable", "default", "key"],
            table.Columns.Select(c => new object?[]
            {
                c.Name, c.Type, c.Nullable ? "" : "not null",
                c.Generated is not null ? $"generated: {c.Generated}" : c.Identity is not null ? $"identity ({c.Identity})" : c.Default,
                c.PrimaryKey ? "PK" : ""
            }));

        if (table.Indexes.Count > 0)
        {
            runner.Show("Indexes", ["index", "definition"], table.Indexes.Select(i => new object?[] { i.Name, i.Definition }));
        }

        if (table.Constraints.Count > 0)
        {
            runner.Show("Constraints", ["constraint", "kind", "definition"],
                table.Constraints.Select(c => new object?[] { c.Name, c.Kind, c.Definition }));
        }
    }

    private static string Like(string? pattern) =>
        pattern is null ? "%" : pattern.Replace("%", "\\%").Replace("_", "\\_").Replace('*', '%').Replace('?', '_');
}
