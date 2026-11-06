using Npgsql;

namespace PgEmbed.Cli.Core;

internal sealed record RelationInfo(string Schema, string Name, string Type, long? RowEstimate, long SizeBytes, string? Comment);

internal sealed record ColumnInfo(string Name, string Type, bool Nullable, string? Default, bool PrimaryKey, string? Identity, string? Generated, string? Comment);

internal sealed record IndexInfo(string Name, bool Unique, bool Primary, string Definition);

internal sealed record ConstraintInfo(string Name, string Kind, string Definition, string? References);

/// <summary>One table, view or other relation, described in full.</summary>
internal sealed record TableSchema(
    string Schema, string Name, string Type, long? RowEstimate, long SizeBytes, string? Comment,
    IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<ConstraintInfo> Constraints);

/// <summary>
/// Reads table structure from the system catalogs. Shared by <c>pgembed schema</c> and the <c>\d</c>
/// commands, so both always show the same thing.
/// </summary>
internal static class SchemaReader
{
    public static readonly string[] Tables = ["r", "p"];
    public static readonly string[] Views = ["v", "m"];
    public static readonly string[] AllRelations = ["r", "p", "v", "m", "S", "f"];

    private const string TypeName = """
        CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'partitioned table' WHEN 'v' THEN 'view'
                       WHEN 'm' THEN 'materialized view' WHEN 'S' THEN 'sequence' WHEN 'f' THEN 'foreign table'
                       ELSE c.relkind::text END
        """;

    /// <summary>Relations of the given kinds whose name matches a pattern (* and ? wildcards), optionally in one schema.</summary>
    public static async Task<IReadOnlyList<RelationInfo>> ListAsync(
        NpgsqlConnection connection, string[] kinds, string? pattern, string? schema, CancellationToken ct)
    {
        var sql = $"""
            SELECT n.nspname, c.relname, {TypeName},
                   CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END,
                   pg_total_relation_size(c.oid), obj_description(c.oid, 'pg_class')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind::text = ANY($1)
              AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
              AND c.relname ILIKE $2 AND n.nspname ILIKE $3
              AND ($4::text IS NULL OR n.nspname = $4)
            ORDER BY 1, 2
            """;

        // schema.table patterns match each part, as psql does: sales.* is every table in sales
        var dot = pattern?.LastIndexOf('.') ?? -1;
        var (schemaPattern, namePattern) = dot > 0 ? (pattern![..dot], pattern[(dot + 1)..]) : ((string?)null, pattern);

        var list = new List<RelationInfo>();
        await using var command = Command(connection, sql, kinds, Like(namePattern), Like(schemaPattern), (object?)schema ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RelationInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }

    /// <summary>A relation by name as Postgres resolves it (the search path applies), or null when there is none.</summary>
    public static async Task<TableSchema?> DescribeAsync(NpgsqlConnection connection, string name, CancellationToken ct)
    {
        uint oid;
        RelationInfo relation;
        await using (var command = Command(connection, $"""
                         SELECT c.oid, n.nspname, c.relname, {TypeName},
                                CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END,
                                pg_total_relation_size(c.oid), obj_description(c.oid, 'pg_class')
                         FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                         WHERE c.oid = to_regclass($1)
                         """, name))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            oid = reader.GetFieldValue<uint>(0);
            relation = new RelationInfo(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        var columns = new List<ColumnInfo>();
        await using (var command = Command(connection, """
                         SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                                CASE WHEN a.attgenerated = '' THEN pg_get_expr(d.adbin, d.adrelid) END,
                                EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = a.attrelid AND i.indisprimary AND a.attnum = ANY(i.indkey)),
                                CASE a.attidentity WHEN 'a' THEN 'always' WHEN 'd' THEN 'by default' END,
                                CASE WHEN a.attgenerated <> '' THEN pg_get_expr(d.adbin, d.adrelid) END,
                                col_description(a.attrelid, a.attnum)
                         FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                         WHERE a.attrelid = $1 AND a.attnum > 0 AND NOT a.attisdropped
                         ORDER BY a.attnum
                         """, oid))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add(new ColumnInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), Text(reader, 3),
                    reader.GetBoolean(4), Text(reader, 5), Text(reader, 6), Text(reader, 7)));
            }
        }

        var indexes = new List<IndexInfo>();
        await using (var command = Command(connection, """
                         SELECT c.relname, i.indisunique, i.indisprimary, pg_get_indexdef(i.indexrelid)
                         FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                         WHERE i.indrelid = $1 ORDER BY i.indisprimary DESC, 1
                         """, oid))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) indexes.Add(new IndexInfo(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3)));
        }

        var constraints = new List<ConstraintInfo>();
        await using (var command = Command(connection, """
                         SELECT conname,
                                CASE contype WHEN 'p' THEN 'primary key' WHEN 'u' THEN 'unique' WHEN 'f' THEN 'foreign key'
                                             WHEN 'c' THEN 'check' WHEN 'x' THEN 'exclusion' END,
                                pg_get_constraintdef(oid),
                                CASE WHEN contype = 'f' THEN confrelid::regclass::text END
                         FROM pg_constraint WHERE conrelid = $1 AND contype IN ('p', 'u', 'f', 'c', 'x')
                         ORDER BY array_position(ARRAY['p', 'u', 'f', 'c', 'x']::"char"[], contype), 1
                         """, oid))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) constraints.Add(new ConstraintInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(reader, 3)));
        }

        return new TableSchema(relation.Schema, relation.Name, relation.Type, relation.RowEstimate, relation.SizeBytes, relation.Comment,
            columns, indexes, constraints);
    }

    private static string Like(string? pattern) =>
        pattern is null ? "%" : pattern.Replace("%", "\\%").Replace("_", "\\_").Replace('*', '%').Replace('?', '_');

    private static string? Text(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
        {
            command.Parameters.Add(p is uint oid
                ? new NpgsqlParameter { Value = oid, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid }
                : new NpgsqlParameter { Value = p });
        }
        return command;
    }
}
