using Google.Protobuf;
using PgSqlParser;

namespace PgEmbed.Cli.Core;

/// <summary>A statement that changes the schema or changes or deletes existing data.</summary>
internal sealed record SqlChange(string Statement, string What, bool EveryRow);

/// <summary>
/// Finds the statements in a script that need confirming before they run: schema changes (DDL,
/// TRUNCATE, SELECT INTO) and changes to existing rows (UPDATE, DELETE, MERGE, data-modifying CTEs).
/// Reads and INSERT run without asking. A statement the parser cannot read is listed too, since
/// nothing can be said about it.
/// </summary>
internal static class SqlSafety
{
    public static IReadOnlyList<SqlChange> Changes(IEnumerable<string> statements)
    {
        var changes = new List<SqlChange>();
        foreach (var statement in statements)
        {
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
        }

        return changes;
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
        }

        if (info.HasSelectInto) return new SqlChange("", "creates a table (SELECT INTO)", false);
        if (info.HasDataModifyingCte) return new SqlChange("", "changes data in a WITH clause", false);
        if (info.RunsProgram) return new SqlChange("", "runs a program on the server (COPY PROGRAM)", false);
        // EXPLAIN ANALYZE and the like run the statement inside them
        if (info.ExecutesInner && info.Inner is { } inner) return Describe(inner);
        return null;
    }

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
