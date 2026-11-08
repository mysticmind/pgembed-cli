using PgSqlParser;

namespace PgEmbed.Cli.Core;

/// <summary>A table a statement uses, and how: read (select), written (dml) or defined (ddl).</summary>
internal sealed record TableUse(string Name, string Use);

/// <summary>What one statement is and what it touches, as the parser sees it without a server.</summary>
internal sealed record StatementCheck(
    string Statement, IReadOnlyList<string> Kinds, IReadOnlyList<TableUse> Tables, IReadOnlyList<string> Functions,
    IReadOnlyList<string> FilterColumns, string? Change, bool ChangesEveryRow);

/// <summary>A syntax error: the parser's message and its 1-based position in the text (0 when unknown).</summary>
internal sealed record SyntaxError(string Message, int Position);

internal sealed record CheckResult(bool Valid, SyntaxError? Error, IReadOnlyList<StatementCheck> Statements);

/// <summary>
/// The offline checks behind <c>sql --check</c>, <c>check_sql</c>, <c>format</c> and <c>format_sql</c>: syntax,
/// a summary per statement (its kind, tables, functions, and whether it would need confirming), and formatting.
/// </summary>
internal static class SqlAnalysis
{
    public static CheckResult Check(string sql)
    {
        var parsed = Parser.Parse(sql);
        if (!parsed.IsSuccess)
        {
            return new CheckResult(false, new SyntaxError(parsed.Error.Message ?? "syntax error", parsed.Error.CursorPos), []);
        }

        var statements = new List<StatementCheck>();
        foreach (var statement in SplitStatements(sql))
        {
            var change = SqlSafety.Changes([statement]).FirstOrDefault();
            var summary = Parser.Summary(statement, new ParserOptions(), -1);
            if (!summary.IsSuccess)
            {
                statements.Add(new StatementCheck(statement, [], [], [], [], change?.What, change?.EveryRow ?? false));
                continue;
            }

            var s = summary.Value;
            statements.Add(new StatementCheck(
                statement,
                s.StatementTypes.Select(Kind).Distinct().ToList(),
                s.Tables.Select(t => new TableUse(t.Name, Use(t.Context.ToString()))).DistinctBy(t => (t.Name, t.Use)).ToList(),
                s.Functions.Where(f => Use(f.Context.ToString()) != "define").Select(f => f.Name).Distinct().ToList(),
                s.FilterColumns.Select(c => string.IsNullOrEmpty(c.TableName) ? c.Column : $"{c.TableName}.{c.Column}").Distinct().ToList(),
                change?.What,
                change?.EveryRow ?? false));
        }

        return new CheckResult(true, null, statements);
    }

    /// <summary>The SQL formatted, keeping comments; or the syntax error that stops it.</summary>
    public static (string? Formatted, SyntaxError? Error) Format(string sql)
    {
        var parsed = Parser.Parse(sql);
        if (!parsed.IsSuccess) return (null, new SyntaxError(parsed.Error.Message ?? "syntax error", parsed.Error.CursorPos));

        var formatted = Parser.Format(sql, new FormatOptions { KeepComments = true, TrailingNewline = true });
        return formatted.IsSuccess
            ? (Terminate(formatted.Value), null)
            : (null, new SyntaxError(formatted.Error.Message ?? "could not format", formatted.Error.CursorPos));
    }

    // every statement ends with a semicolon, the last one too (the formatter leaves a lone statement without);
    // not after a trailing line comment, which would swallow it
    private static string Terminate(string formatted)
    {
        var text = formatted.TrimEnd();
        if (text.Length == 0 || text.EndsWith(';')) return text + "\n";
        var lastLine = text[(text.LastIndexOf('\n') + 1)..].TrimStart();
        return lastLine.StartsWith("--", StringComparison.Ordinal) ? text + "\n" : text + ";\n";
    }

    private static IReadOnlyList<string> SplitStatements(string sql)
    {
        var split = Parser.SplitWithParser(sql);
        if (!split.IsSuccess) return [sql];
        var statements = split.Value.Statements.Select(s => s.Text.Trim()).Where(s => s.Length > 0).ToList();
        return statements.Count > 0 ? statements : [sql];
    }

    // the parser's node names, as SQL: SelectStmt is SELECT, CreateStmt is CREATE TABLE
    internal static string Kind(string node)
    {
        var name = node.EndsWith("Stmt", StringComparison.Ordinal) ? node[..^4] : node;
        return name switch
        {
            "Create" => "CREATE TABLE",
            "Index" => "CREATE INDEX",
            "View" => "CREATE VIEW",
            "CreateTableAs" => "CREATE TABLE AS",
            "VariableSet" => "SET",
            "VariableShow" => "SHOW",
            "Transaction" => "TRANSACTION",
            "Copy" => "COPY",
            _ => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString())).ToUpperInvariant()
        };
    }

    private static string Use(string context) => context.ToLowerInvariant() switch
    {
        "select" => "read",
        "dml" => "write",
        "ddl" => "define",
        var other => other
    };
}
