using System.Diagnostics;
using System.Globalization;
using Npgsql;
using PgSqlParser;

namespace PgEmbed.Cli.Core;

/// <summary>A result column: its name and its Postgres type name (for example <c>jsonb</c>, <c>integer</c>).</summary>
internal sealed record SqlColumn(string Name, string DataType);

/// <summary>A <c>json</c> or <c>jsonb</c> value, kept as the server's text so renderers can nest or pretty-print it.</summary>
internal sealed record SqlJson(string Text)
{
    public override string ToString() => Text;
}

/// <summary>How a statement ended: a result set (rows read, whether more were left unread) or affected rows.</summary>
internal sealed record StatementSummary(string Statement, long Rows, int? RowsAffected, bool Truncated, double Milliseconds)
{
    public bool HasResultSet => RowsAffected is null;
}

internal sealed record StatementError(string Statement, string? SqlState, string Message, int Position, string? Detail, string? Hint);

/// <summary>
/// Receives results as they arrive: the columns of a result set, then its rows one at a time, then how it ended.
/// A statement without a result set (INSERT, DDL) goes straight to <see cref="End"/>.
/// </summary>
internal interface IResultSink
{
    void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns);
    void Row(object?[] values);
    void End(StatementSummary summary);
    void Fail(StatementError error);
}

/// <summary>
/// What a run knows about its session across statements and calls. Today: whether an explicit transaction is
/// open, so a capped read never cancels a query inside one (a cancel would abort the transaction).
/// </summary>
internal sealed class SqlSession
{
    public bool InTransaction { get; private set; }

    public void Observe(string statement)
    {
        // the tokenizer is enough, and avoids the full parse on every statement
        var scanned = Parser.Scan(statement);
        if (!scanned.IsSuccess || scanned.Value.Tokens.Count == 0) return;

        var tokens = scanned.Value.Tokens.Select(t => t.Token).ToList();
        Token? Next(int i) => i < tokens.Count ? tokens[i] : null;
        var chain = tokens.Contains(Token.Chain) && !tokens.Contains(Token.No); // COMMIT AND CHAIN starts the next one

        switch (tokens[0])
        {
            case Token.BeginP or Token.Start when !(tokens[0] == Token.Start && Next(1) != Token.Transaction):
                InTransaction = true;
                break;
            case Token.Commit or Token.EndP or Token.AbortP:
                InTransaction = chain;
                break;
            case Token.Rollback when Next(1) != Token.To && !(Next(1) == Token.Transaction && Next(2) == Token.To) && !(Next(1) == Token.Work && Next(2) == Token.To):
                InTransaction = chain;
                break;
            case Token.Prepare when Next(1) == Token.Transaction:
                InTransaction = false;
                break;
        }
    }

    /// <summary>When a script fails outside the shell, the server ends the session anyway; the shell keeps it.</summary>
    public void Reset() => InTransaction = false;
}

/// <summary>
/// Runs one statement and streams its result into a sink. Rows past <c>maxRows</c> are not read: once one
/// more row shows the result is longer, the query is cancelled on the server (unless a transaction is open,
/// where a cancel would abort it; then the rest is skipped without being decoded).
/// </summary>
internal static class SqlExecutor
{
    private const string QueryCanceled = "57014";

    public static async Task<bool> RunAsync(
        NpgsqlConnection connection, string statement, object[] parameters, string? title, long maxRows,
        IResultSink sink, SqlSession? session, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var cancelled = false;
        long rows = 0;
        var truncated = false;
        var begun = false;

        await using var command = new NpgsqlCommand(statement, connection);
        foreach (var p in parameters) command.Parameters.Add(new NpgsqlParameter { Value = p });

        try
        {
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (reader.FieldCount > 0)
                {
                    var columns = new SqlColumn[reader.FieldCount];
                    var json = new bool[reader.FieldCount];
                    for (var i = 0; i < columns.Length; i++)
                    {
                        var type = reader.GetDataTypeName(i);
                        columns[i] = new SqlColumn(reader.GetName(i), type);
                        json[i] = type is "json" or "jsonb";
                    }

                    sink.Begin(statement, title, columns);
                    begun = true;

                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        if (rows >= maxRows)
                        {
                            truncated = true;
                            if (session is not { InTransaction: true })
                            {
                                cancelled = true;
                                command.Cancel();
                            }

                            break;
                        }

                        var row = new object?[columns.Length];
                        for (var i = 0; i < row.Length; i++)
                        {
                            row[i] = reader.IsDBNull(i) ? null : json[i] ? new SqlJson(reader.GetString(i)) : ToPlain(reader.GetValue(i));
                        }

                        sink.Row(row);
                        rows++;
                    }
                }

                // disposing reads to the end of the result (or to the cancel's error)
                try
                {
                    await reader.CloseAsync().ConfigureAwait(false);
                }
                catch (PostgresException ex) when (cancelled && ex.SqlState == QueryCanceled)
                {
                    // the cancel we asked for
                }

                if (!begun)
                {
                    sink.End(new StatementSummary(statement, 0, reader.RecordsAffected, false, watch.Elapsed.TotalMilliseconds));
                    session?.Observe(statement);
                    return true;
                }
            }
        }
        catch (PostgresException ex) when (cancelled && ex.SqlState == QueryCanceled)
        {
            // the cancel arrived while the reader was being disposed
        }
        catch (PostgresException ex)
        {
            sink.Fail(new StatementError(statement, ex.SqlState, ex.MessageText, ex.Position, ex.Detail, ex.Hint));
            return false;
        }

        sink.End(new StatementSummary(statement, rows, null, truncated, watch.Elapsed.TotalMilliseconds));
        session?.Observe(statement);
        return true;
    }

    /// <summary>Rows built in memory (catalog descriptions), sent through the same sink as a query result.</summary>
    public static void Show(IResultSink sink, string? title, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        sink.Begin(title ?? "", title, columns.Select(c => new SqlColumn(c, "text")).ToList());
        long count = 0;
        foreach (var row in rows)
        {
            sink.Row(row);
            count++;
        }

        sink.End(new StatementSummary(title ?? "", count, null, false, 0));
    }

    // values JSON can carry as they are; everything else as text
    internal static object? ToPlain(object value) => value switch
    {
        string or bool or int or long or short or decimal or double or float or Guid or DateTime or DateTimeOffset => value,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
