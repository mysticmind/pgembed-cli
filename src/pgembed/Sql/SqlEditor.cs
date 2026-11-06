using Npgsql;
using PgEmbed.Cli.Core;
using PgSqlParser;

namespace PgEmbed.Cli.Sql;

internal enum SqlStyle { Keyword, Identifier, String, Number, Parameter, Operator, Comment, Meta, Error }

internal readonly record struct StyledSpan(int Start, int Length, SqlStyle Style);

/// <summary>What the interactive prompt does with the text being typed; no console involved, so it can be tested.</summary>
internal static class SqlEditor
{
    /// <summary>Colors from the PostgreSQL scanner, and the parser's first syntax error underlined.</summary>
    public static IReadOnlyList<StyledSpan> Highlight(string text)
    {
        if (text.TrimStart().StartsWith('\\')) return [new StyledSpan(0, text.Length, SqlStyle.Meta)];

        var spans = new List<StyledSpan>();
        var tokens = Parser.Tokenize(text);
        if (tokens.IsSuccess)
        {
            foreach (var token in tokens.Value)
            {
                SqlStyle? style = token.Kind switch
                {
                    SqlTokenKind.Keyword => SqlStyle.Keyword,
                    SqlTokenKind.StringLiteral => SqlStyle.String,
                    SqlTokenKind.NumericLiteral => SqlStyle.Number,
                    SqlTokenKind.Parameter => SqlStyle.Parameter,
                    SqlTokenKind.Operator => SqlStyle.Operator,
                    SqlTokenKind.Comment => SqlStyle.Comment,
                    _ => null
                };
                if (style is { } s && token.End > token.Start) spans.Add(new StyledSpan(token.Start, token.End - token.Start, s));
            }
        }

        if (ErrorAt(text) is { } error) spans.Add(error);
        return spans;
    }

    /// <summary>
    /// The token at the parser's first error. An error at the end of the text only means the statement is
    /// not finished yet, so it is not shown.
    /// </summary>
    private static StyledSpan? ErrorAt(string text)
    {
        var parsed = Parser.Parse(text);
        if (parsed.IsSuccess || parsed.Error.CursorPos <= 0) return null;

        var index = Connections.CodePointToIndex(text, parsed.Error.CursorPos);
        if (index >= text.TrimEnd().Length) return null;

        var end = index;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ';' and not ',' and not '(' and not ')') end++;
        return new StyledSpan(index, Math.Max(1, end - index), SqlStyle.Error);
    }

    /// <summary>
    /// Whether Enter should run the text: a backslash command, or SQL ending with a semicolon outside any
    /// string, comment or dollar-quoted body. Otherwise Enter starts a new line.
    /// </summary>
    public static bool IsComplete(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('\\') || trimmed is "exit" or "quit") return true;

        var tokens = Parser.Tokenize(text);
        if (!tokens.IsSuccess) return false; // an unterminated string or body

        var code = tokens.Value.Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        return code.Count > 0 && text[code[^1].Start..code[^1].End] == ";";
    }

    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "EXISTS", "BETWEEN", "LIKE", "ILIKE", "AS", "DISTINCT",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "LATERAL", "ON", "USING", "GROUP", "BY", "HAVING", "ORDER",
        "ASC", "DESC", "NULLS", "FIRST", "LAST", "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT", "ALL", "WITH",
        "RECURSIVE", "INSERT", "INTO", "VALUES", "DEFAULT", "RETURNING", "UPDATE", "SET", "DELETE", "MERGE", "MATCHED", "THEN",
        "CONFLICT", "DO", "NOTHING", "CREATE", "ALTER", "DROP", "TRUNCATE", "TABLE", "VIEW", "MATERIALIZED", "INDEX", "UNIQUE",
        "SCHEMA", "DATABASE", "EXTENSION", "FUNCTION", "PROCEDURE", "TRIGGER", "SEQUENCE", "TYPE", "IF", "CASCADE", "RESTRICT",
        "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "CHECK", "CONSTRAINT", "COLUMN", "ADD", "RENAME", "TO", "OWNER", "GRANT",
        "REVOKE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "TRANSACTION", "EXPLAIN", "ANALYZE", "VERBOSE", "VACUUM", "CASE",
        "WHEN", "ELSE", "END", "CAST", "COALESCE", "NULLIF", "TRUE", "FALSE", "COUNT", "SUM", "AVG", "MIN", "MAX", "NOW",
        "INTEGER", "BIGINT", "SMALLINT", "TEXT", "VARCHAR", "BOOLEAN", "NUMERIC", "TIMESTAMPTZ", "TIMESTAMP", "DATE", "UUID",
        "JSONB", "JSON", "BYTEA", "SERIAL", "GENERATED", "ALWAYS", "IDENTITY", "WINDOW", "OVER", "PARTITION", "FILTER", "COPY"
    ];

    // after these, a table name is expected
    private static readonly HashSet<string> RelationContext = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "TRUNCATE", "ONLY" };

    /// <summary>Completions for the word at [start, start+length): tables, columns, schemas and keywords.</summary>
    public static IReadOnlyList<string> Complete(SqlCatalog catalog, string text, int start, int length)
    {
        var word = text.Substring(start, length);

        // schema.<table> or table.<column>
        if (start > 0 && text[start - 1] == '.')
        {
            var qualifier = IdentifierBefore(text, start - 1);
            var inSchema = catalog.Relations.Where(r => r.Schema.Equals(qualifier, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name);
            var ofTable = catalog.ColumnsOf(qualifier);
            return Filter(inSchema.Concat(ofTable), word);
        }

        var previous = PreviousWord(text, start);
        var relations = catalog.Relations.Select(r => r.Schema is "public" ? r.Name : $"{r.Schema}.{r.Name}");
        if (previous is not null && RelationContext.Contains(previous))
        {
            return Filter(relations.Concat(catalog.Schemas), word);
        }

        var lower = word.Length > 0 && word.All(c => !char.IsLetter(c) || char.IsLower(c));
        var keywords = Keywords.Select(k => lower ? k.ToLowerInvariant() : k);
        return Filter(catalog.AllColumns.Concat(relations).Concat(keywords), word);
    }

    private static IReadOnlyList<string> Filter(IEnumerable<string> candidates, string word) =>
        candidates.Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();

    private static string IdentifierBefore(string text, int end)
    {
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '"')) start--;
        return text[start..end].Trim('"');
    }

    private static string? PreviousWord(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0) return null;
        var end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i--;
        return end - (i + 1) > 0 ? text[(i + 1)..end] : null;
    }
}

internal sealed record CatalogRelation(string Schema, string Name);

/// <summary>Schemas, tables, views and their columns, for completion. Reloaded after schema changes.</summary>
internal sealed class SqlCatalog
{
    public static readonly SqlCatalog Empty = new([], [], new Dictionary<string, List<string>>());

    private readonly Dictionary<string, List<string>> _columns;

    public SqlCatalog(IReadOnlyList<string> schemas, IReadOnlyList<CatalogRelation> relations, Dictionary<string, List<string>> columns)
    {
        Schemas = schemas;
        Relations = relations;
        _columns = new Dictionary<string, List<string>>(columns, StringComparer.OrdinalIgnoreCase);
        AllColumns = columns.Values.SelectMany(c => c).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Schemas { get; }
    public IReadOnlyList<CatalogRelation> Relations { get; }
    public IReadOnlyList<string> AllColumns { get; }

    public IReadOnlyList<string> ColumnsOf(string relation) => _columns.TryGetValue(relation, out var list) ? list : [];

    private const string Query = """
        SELECT n.nspname, c.relname, a.attname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
          AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
        ORDER BY n.nspname, c.relname, a.attnum
        """;

    public static async Task<SqlCatalog> LoadAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var schemas = new List<string>();
        await using (var command = new NpgsqlCommand(
                         "SELECT nspname FROM pg_namespace WHERE nspname <> 'information_schema' AND nspname NOT LIKE 'pg\\_%' ORDER BY 1", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) schemas.Add(reader.GetString(0));
        }

        var relations = new List<CatalogRelation>();
        var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new NpgsqlCommand(Query, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (relations.Count == 0 || relations[^1] != new CatalogRelation(schema, name)) relations.Add(new CatalogRelation(schema, name));
                if (reader.IsDBNull(2)) continue;
                if (!columns.TryGetValue(name, out var list)) columns[name] = list = [];
                list.Add(reader.GetString(2));
            }
        }

        return new SqlCatalog(schemas, relations, columns);
    }
}
