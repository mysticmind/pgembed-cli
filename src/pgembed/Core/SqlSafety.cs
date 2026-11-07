using Google.Protobuf;
using PgSqlParser;

namespace PgEmbed.Cli.Core;

/// <summary>A statement that changes the schema or changes or deletes existing data.</summary>
internal sealed record SqlChange(string Statement, string What, bool EveryRow);

/// <summary>
/// Finds the statements in a script that need confirming before they run: schema changes (DDL, TRUNCATE,
/// SELECT INTO), changes to existing rows (UPDATE, DELETE, MERGE, data-modifying CTEs), and anything that
/// reaches outside the database or cannot be checked: code (DO, CALL, EXECUTE of a prepared statement),
/// libraries (LOAD), programs and files on this machine (COPY PROGRAM, COPY to or from a file, and the
/// built-in functions that read or write files, connect elsewhere or control the server). An instance runs
/// as you, with a superuser, so these act with your permissions. Reads and INSERT run without asking. A
/// statement the parser cannot read is listed too, since nothing can be said about it.
/// </summary>
internal static class SqlSafety
{
    public static IReadOnlyList<SqlChange> Changes(IEnumerable<string> statements)
    {
        var changes = new List<SqlChange>();
        foreach (var statement in statements)
        {
            if (IsPlainRead(statement)) continue;
            var classified = Parser.Classify(statement);
            if (!classified.IsSuccess)
            {
                changes.Add(new SqlChange(statement, "could not be checked (the parser does not recognize it)", false));
                continue;
            }

            foreach (var info in classified.Value)
            {
                if (Describe(info) is { } change) changes.Add(change with { Statement = statement });
            }

            foreach (var function in FunctionsIn(statement))
            {
                if (ServerAccess.TryGetValue(function, out var what)) changes.Add(new SqlChange(statement, $"{what} ({function})", false));
            }
        }

        return changes;
    }

    /// <summary>
    /// Built-in functions that reach outside the database or run SQL that cannot be checked, by what they do.
    /// Functions you define are not looked into; a superuser can do anything through them.
    /// </summary>
    internal static readonly Dictionary<string, string> ServerAccess = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pg_read_file"] = "reads files on this machine", ["pg_read_binary_file"] = "reads files on this machine",
        ["pg_ls_dir"] = "lists folders on this machine", ["pg_stat_file"] = "reads files on this machine",
        ["pg_ls_logdir"] = "lists folders on this machine", ["pg_ls_waldir"] = "lists folders on this machine",
        ["pg_ls_tmpdir"] = "lists folders on this machine", ["pg_ls_archive_statusdir"] = "lists folders on this machine",
        ["pg_ls_logicalsnapdir"] = "lists folders on this machine", ["pg_ls_logicalmapdir"] = "lists folders on this machine",
        ["pg_ls_replslotdir"] = "lists folders on this machine", ["pg_logdir_ls"] = "lists folders on this machine",
        ["lo_import"] = "reads files on this machine", ["lo_export"] = "writes files on this machine",
        ["pg_file_write"] = "writes files on this machine", ["pg_file_rename"] = "writes files on this machine",
        ["pg_file_unlink"] = "deletes files on this machine", ["pg_file_sync"] = "writes files on this machine",
        ["dblink"] = "connects to another server", ["dblink_exec"] = "connects to another server",
        ["dblink_connect"] = "connects to another server", ["dblink_connect_u"] = "connects to another server",
        ["dblink_send_query"] = "connects to another server", ["dblink_open"] = "connects to another server",
        ["pg_terminate_backend"] = "controls the server", ["pg_cancel_backend"] = "controls the server",
        ["pg_signal_backend"] = "controls the server", ["pg_reload_conf"] = "controls the server",
        ["pg_rotate_logfile"] = "controls the server", ["pg_promote"] = "controls the server",
        ["pg_switch_wal"] = "controls the server", ["pg_create_restore_point"] = "controls the server",
        ["query_to_xml"] = "runs SQL given as text, which cannot be checked",
        ["query_to_xmlschema"] = "runs SQL given as text, which cannot be checked",
        ["query_to_xml_and_xmlschema"] = "runs SQL given as text, which cannot be checked",
        ["ts_stat"] = "runs SQL given as text, which cannot be checked",
    };

    private static IEnumerable<string> FunctionsIn(string statement)
    {
        var summary = Parser.Summary(statement, new ParserOptions(), -1);
        return summary.IsSuccess ? summary.Value.Functions.Select(f => f.FunctionName is { Length: > 0 } name ? name : f.Name).Distinct() : [];
    }

    // keywords that can start a statement which changes nothing (INSERT adds rows but does not ask)
    private static readonly HashSet<Token> ReadStarts = [Token.Select, Token.With, Token.Values, Token.Table, Token.Show, Token.Explain, Token.Insert];

    // any of these anywhere sends the statement to the full classification
    private static readonly HashSet<Token> Risky =
    [
        Token.Into, Token.Update, Token.DeleteP, Token.Merge, Token.Truncate, Token.Copy, Token.Program, Token.Create, Token.Alter,
        Token.Drop, Token.Analyze, Token.Grant, Token.Revoke, Token.Comment, Token.Security, Token.Refresh, Token.LockP
    ];

    /// <summary>
    /// A fast, conservative check with the tokenizer only: true when the statement starts like a read (or INSERT)
    /// and has no keyword that could make it change anything. Then the full parse, whose first use costs about
    /// 90 ms, is not needed. Anything else goes to the full classification.
    /// </summary>
    internal static bool IsPlainRead(string statement)
    {
        var scanned = Parser.Scan(statement);
        if (!scanned.IsSuccess || scanned.Value.Tokens.Count == 0) return false;

        var tokens = scanned.Value.Tokens;
        var first = tokens[0].Token;
        if (!ReadStarts.Contains(first)) return false;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i].Token;
            if (token == Token.Into && first == Token.Insert && i == 1) continue; // INSERT INTO
            if (Risky.Contains(token)) return false;

            // a name of a function that reaches outside the database: let the full check describe it
            if (token is Token.Ident or Token.Sconst && ServerAccess.ContainsKey(statement[tokens[i].Start..tokens[i].End].Trim('"'))) return false;
        }

        return true;
    }

    /// <summary>
    /// Why a script cannot run in read-only mode; empty when it can. Writes of any kind (INSERT too) are refused,
    /// and so is transaction control, since <c>COMMIT; DELETE ...</c> would end the read-only transaction and run
    /// the delete; so are settings that change it. A statement the parser cannot read is refused too.
    /// </summary>
    public static IReadOnlyList<SqlChange> ReadOnlyViolations(IEnumerable<string> statements)
    {
        var violations = new List<SqlChange>();
        foreach (var statement in statements)
        {
            var classified = Parser.Classify(statement);
            if (!classified.IsSuccess)
            {
                violations.Add(new SqlChange(statement, "could not be checked (the parser does not recognize it)", false));
                continue;
            }

            // everything that would ask first in normal mode is refused here
            if (Changes([statement]).FirstOrDefault() is { } change)
            {
                violations.Add(change);
                continue;
            }

            foreach (var info in classified.Value)
            {
                var reason = info.Kind switch
                {
                    StatementKind.Transaction => "controls the transaction",
                    _ when SetsTransactionMode(info.Node) => "changes the transaction or session mode",
                    StatementKind.Session => null, // SET search_path and the like last only for this call
                    _ when !info.IsReadOnly => Describe(info)?.What ?? $"writes ({info.Kind.ToString().ToLowerInvariant()})",
                    _ => null
                };

                if (reason is not null)
                {
                    violations.Add(new SqlChange(statement, reason, false));
                    break;
                }
            }

            if (violations.LastOrDefault()?.Statement != statement && FunctionsIn(statement).FirstOrDefault(f => f.Equals("set_config", StringComparison.OrdinalIgnoreCase)) is { } setter)
            {
                violations.Add(new SqlChange(statement, $"changes a setting ({setter})", false));
            }
        }

        return violations;
    }

    // SET TRANSACTION, SET SESSION CHARACTERISTICS, SET default_transaction_read_only and the like, and roles
    private static bool SetsTransactionMode(IMessage? node)
    {
        var set = node switch
        {
            VariableSetStmt direct => direct,
            Node { VariableSetStmt: { } wrapped } => wrapped,
            _ => null
        };

        if (set is null) return false;
        if (set.Kind == VariableSetKind.VarResetAll) return true; // RESET ALL resets the role too
        var name = set.Name ?? "";
        return name.Contains("transaction", StringComparison.OrdinalIgnoreCase)
               || name.Contains("session", StringComparison.OrdinalIgnoreCase)
               || name.Equals("role", StringComparison.OrdinalIgnoreCase);
    }

    private static SqlChange? Describe(StatementInfo info)
    {
        switch (info.Kind)
        {
            case StatementKind.Ddl:
                return new SqlChange("", "changes the schema", false);
            case StatementKind.Truncate:
                return new SqlChange("", "deletes every row (TRUNCATE)", true);
            case StatementKind.Delete:
                var delete = Unwrap<DeleteStmt>(info.Node);
                return delete?.WhereClause is null
                    ? new SqlChange("", $"deletes every row of {Table(delete?.Relation)} (no WHERE)", true)
                    : new SqlChange("", $"deletes rows of {Table(delete.Relation)}", false);
            case StatementKind.Update:
                var update = Unwrap<UpdateStmt>(info.Node);
                return update?.WhereClause is null
                    ? new SqlChange("", $"updates every row of {Table(update?.Relation)} (no WHERE)", true)
                    : new SqlChange("", $"updates rows of {Table(update.Relation)}", false);
            case StatementKind.Merge:
                return new SqlChange("", "changes rows (MERGE)", false);
            case StatementKind.Call:
                return Is(info.Node, n => n.DoStmt) is not null || info.Node is DoStmt
                    ? new SqlChange("", "runs code on the server (DO), which cannot be checked", false)
                    : new SqlChange("", "calls a procedure, which cannot be checked", false);
            case StatementKind.Prepared when info.Node is ExecuteStmt || Is(info.Node, n => n.ExecuteStmt) is not null:
                return new SqlChange("", "runs a prepared statement, which cannot be checked", false);
            case StatementKind.Session when info.Node is LoadStmt || Is(info.Node, n => n.LoadStmt) is not null:
                return new SqlChange("", "loads a library into the server (LOAD)", false);
            case StatementKind.Copy when !info.RunsProgram:
                var copy = info.Node as CopyStmt ?? Is(info.Node, n => n.CopyStmt);
                if (copy is { Filename.Length: > 0 })
                {
                    return copy.IsFrom
                        ? new SqlChange("", "reads a file on this machine (COPY FROM)", false)
                        : new SqlChange("", "writes a file on this machine (COPY TO)", false);
                }

                break;
        }

        if (info.HasSelectInto) return new SqlChange("", "creates a table (SELECT INTO)", false);
        if (info.HasDataModifyingCte) return new SqlChange("", "changes data in a WITH clause", false);
        if (info.RunsProgram) return new SqlChange("", "runs a program on the server (COPY PROGRAM)", false);
        // EXPLAIN ANALYZE and the like run the statement inside them
        if (info.ExecutesInner && info.Inner is { } inner) return Describe(inner);
        return null;
    }

    private static T? Is<T>(IMessage? node, Func<Node, T?> pick) where T : class => node is Node wrapper ? pick(wrapper) : null;

    private static T? Unwrap<T>(IMessage? node) where T : class, IMessage => node switch
    {
        T direct => direct,
        Node wrapper when typeof(T) == typeof(DeleteStmt) => wrapper.DeleteStmt as T,
        Node wrapper when typeof(T) == typeof(UpdateStmt) => wrapper.UpdateStmt as T,
        _ => null
    };

    private static string Table(RangeVar? relation) =>
        relation is null ? "the table" : string.IsNullOrEmpty(relation.Schemaname) ? relation.Relname : $"{relation.Schemaname}.{relation.Relname}";
}
