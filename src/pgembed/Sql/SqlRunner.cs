using System.Diagnostics;
using System.Globalization;
using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using PgSqlParser;
using Spectre.Console;

namespace PgEmbed.Cli.Sql;

/// <summary>How results are shown; the interactive prompt changes it with \x, \timing and \format.</summary>
internal sealed class SqlOutput
{
    public static readonly string[] Formats = ["table", "aligned", "json", "csv", "markdown"];

    public string Format { get; set; } = "table";
    public int MaxRows { get; set; } = 500;
    public bool Timing { get; set; }
    public bool Expanded { get; set; }

    /// <summary>JSON per statement as it completes (interactive) instead of one document at the end.</summary>
    public bool StreamJson { get; set; }
}

internal sealed record StatementResult(
    string Statement, List<string>? Columns, List<object?[]>? Rows, int? RowsAffected, bool Truncated, double Milliseconds);

internal sealed record StatementError(string Statement, string? SqlState, string Message, int Position, string? Detail, string? Hint);

/// <summary>Runs statements one at a time and shows each result as it completes.</summary>
internal sealed class SqlRunner(CliContext cli, SqlOutput output)
{
    public List<StatementResult> Results { get; } = [];
    public StatementError? Error { get; private set; }

    /// <summary>The PostgreSQL parser splits scripts correctly (dollar-quoted bodies, semicolons in strings).
    /// The server stays the authority: a script the parser rejects is sent as one batch.</summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        var split = Parser.SplitWithParser(sql);
        if (!split.IsSuccess) return [sql];

        var statements = split.Value.Statements.Select(s => s.Text.Trim()).Where(s => s.Length > 0).ToList();
        return statements.Count > 0 ? statements : [sql];
    }

    /// <summary>False when a statement failed; the error has been shown and later statements were not run.</summary>
    public async Task<bool> RunAsync(NpgsqlConnection connection, IEnumerable<string> statements, CancellationToken ct)
    {
        foreach (var statement in statements)
        {
            if (!await RunOneAsync(connection, statement, [], null, ct)) return false;
        }

        return true;
    }

    /// <summary>A catalog query for the interactive commands (\d and friends), with parameters.</summary>
    public Task<bool> QueryAsync(NpgsqlConnection connection, string sql, object[] parameters, string? title, CancellationToken ct) =>
        RunOneAsync(connection, sql, parameters, title, ct);

    /// <summary>Rows built in memory (catalog descriptions), shown like any query result.</summary>
    public void Show(string? title, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        var result = new StatementResult(title ?? "", columns.ToList(), rows.ToList(), null, false, 0);
        Results.Add(result);
        Title(title);
        Render(result);
    }

    private void Title(string? title)
    {
        if (title is null) return;
        if (output.Format == "table") cli.Console.MarkupLineInterpolated($"[bold]{title}[/]");
        else if (output.Format == "aligned") cli.Services.Out.WriteLine(title);
    }

    private async Task<bool> RunOneAsync(NpgsqlConnection connection, string statement, object[] parameters, string? title, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var command = new NpgsqlCommand(statement, connection);
            foreach (var p in parameters) command.Parameters.Add(new NpgsqlParameter { Value = p });
            await using var reader = await command.ExecuteReaderAsync(ct);

            StatementResult result;
            if (reader.FieldCount > 0)
            {
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<object?[]>();
                var limit = output.Format == "table" ? output.MaxRows : int.MaxValue;
                var truncated = false;
                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count >= limit) { truncated = true; continue; }
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : ToPlain(reader.GetValue(i));
                    rows.Add(row);
                }

                result = new StatementResult(statement, columns, rows, null, truncated, watch.Elapsed.TotalMilliseconds);
            }
            else
            {
                await reader.CloseAsync();
                result = new StatementResult(statement, null, null, reader.RecordsAffected, false, watch.Elapsed.TotalMilliseconds);
            }

            Results.Add(result);
            Title(title);
            Render(result);
            return true;
        }
        catch (PostgresException ex)
        {
            Error = new StatementError(statement, ex.SqlState, ex.MessageText, ex.Position, ex.Detail, ex.Hint);
            if (output.Format != "json" || output.StreamJson) ReportError(cli, statement, ex.MessageText, ex.Position, ex.Detail, ex.Hint);
            return false;
        }
    }

    /// <summary>The error, then its line with a caret under the position (1-based, in code points, as Postgres reports it).</summary>
    public static void ReportError(CliContext cli, string sql, string message, int position, string? detail = null, string? hint = null)
    {
        cli.Console.MarkupLineInterpolated($"[red]ERROR:[/] {message}");
        if (position > 0 && Connections.CodePointToIndex(sql, position) is var index && index <= sql.Length)
        {
            var lineStart = index == 0 ? 0 : sql.LastIndexOf('\n', index - 1) + 1;
            var lineEnd = sql.IndexOf('\n', index);
            var line = sql[lineStart..(lineEnd < 0 ? sql.Length : lineEnd)].TrimEnd('\r');
            var lineNumber = sql[..lineStart].Count(c => c == '\n') + 1;
            // the caret column counts what the terminal shows: code points, not UTF-16 units
            var column = Connections.CountCodePoints(sql[lineStart..index]);

            var prefix = $"LINE {lineNumber}: ";
            cli.Console.WriteLine(prefix + line);
            cli.Console.WriteLine(new string(' ', prefix.Length + column) + "^");
        }

        if (detail is not null) cli.Console.MarkupLineInterpolated($"[dim]DETAIL:[/] {detail}");
        if (hint is not null) cli.Console.MarkupLineInterpolated($"[dim]HINT:[/] {hint}");
    }

    private void Render(StatementResult result)
    {
        var timing = output.Timing ? $" ({result.Milliseconds:0.0} ms)" : "";

        if (output.Format == "json")
        {
            if (output.StreamJson) cli.WriteJson(result);
            return;
        }

        if (result.Columns is null)
        {
            var affected = result.RowsAffected is >= 0 ? $"{result.RowsAffected} row{(result.RowsAffected == 1 ? "" : "s")} affected" : "OK";
            if (output.Format == "table") cli.Console.MarkupLineInterpolated($"[dim]{affected}{timing}[/]");
            else if (output.Format == "aligned") cli.Services.Out.WriteLine(affected + timing);
            return;
        }

        var o = cli.Services.Out;
        switch (output.Format)
        {
            case "csv":
                o.WriteLine(string.Join(",", result.Columns.Select(Csv)));
                foreach (var row in result.Rows!) o.WriteLine(string.Join(",", row.Select(v => Csv(Text(v)))));
                o.Flush();
                break;

            case "aligned":
                Aligned.Write(o, result.Columns, result.Rows!, output.Expanded, Text);
                var rows = result.Rows!.Count;
                o.WriteLine($"({rows} row{(rows == 1 ? "" : "s")}{timing})");
                o.WriteLine();
                o.Flush();
                break;

            case "markdown":
                o.WriteLine("| " + string.Join(" | ", result.Columns) + " |");
                o.WriteLine("|" + string.Concat(result.Columns.Select(_ => " --- |")));
                foreach (var row in result.Rows!) o.WriteLine("| " + string.Join(" | ", row.Select(v => Text(v).Replace("|", "\\|"))) + " |");
                o.WriteLine();
                o.Flush();
                break;

            default:
                if (output.Expanded) RenderExpanded(result);
                else
                {
                    var table = new Table().Border(TableBorder.Simple);
                    foreach (var column in result.Columns) table.AddColumn(Markup.Escape(column));
                    foreach (var row in result.Rows!) table.AddRow(row.Select(v => v is null ? "[dim]NULL[/]" : Markup.Escape(Text(v))).ToArray());
                    cli.Console.Write(table);
                }

                var count = result.Rows!.Count;
                cli.Console.MarkupLineInterpolated(
                    $"[dim]({count} row{(count == 1 ? "" : "s")}{(result.Truncated ? ", more not shown: use --max-rows or --format csv" : "")}{timing})[/]");
                break;
        }
    }

    // one block per row, like psql's \x
    private void RenderExpanded(StatementResult result)
    {
        for (var r = 0; r < result.Rows!.Count; r++)
        {
            cli.Console.MarkupLineInterpolated($"[dim]-[[ RECORD {r + 1} ]]-[/]");
            var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
            for (var c = 0; c < result.Columns!.Count; c++)
            {
                var value = result.Rows[r][c];
                grid.AddRow(new Text(result.Columns[c]), value is null ? new Markup("[dim]NULL[/]") : new Text(Text(value)));
            }

            cli.Console.Write(grid);
        }
    }

    // values JSON can carry as they are; everything else as text
    private static object? ToPlain(object value) => value switch
    {
        string or bool or int or long or short or decimal or double or float or Guid or DateTime or DateTimeOffset => value,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string Text(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

/// <summary>Asks before statements that change the schema or existing data.</summary>
internal static class SqlConfirmation
{
    /// <summary>True to go ahead. Without a terminal to ask on, such statements need --yes.</summary>
    public static bool Ask(CliContext cli, IReadOnlyList<SqlChange> changes, bool yes)
    {
        if (changes.Count == 0 || yes) return true;

        if (!cli.Console.Profile.Capabilities.Interactive || cli.Settings.Json)
        {
            throw new PostgresEmbedException(
                $"{Describe(changes)} Nothing was run. Pass --yes to run it without asking.");
        }

        cli.Console.MarkupLine(changes.Count == 1 ? "[yellow]This statement changes the database:[/]" : "[yellow]These statements change the database:[/]");
        foreach (var change in changes)
        {
            var color = change.EveryRow ? "red" : "yellow";
            cli.Console.MarkupLineInterpolated($"  [{color}]![/] {Abbreviate(change.Statement)}  [{color}]{change.What}[/]");
        }

        return cli.Console.Confirm("Run it?", defaultValue: false);
    }

    private static string Describe(IReadOnlyList<SqlChange> changes) =>
        changes.Count == 1
            ? $"'{Abbreviate(changes[0].Statement)}' {changes[0].What}."
            : $"{changes.Count} statements change the database, starting with '{Abbreviate(changes[0].Statement)}' ({changes[0].What}).";

    private static string Abbreviate(string statement)
    {
        var line = string.Join(' ', statement.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 70 ? line : line[..67] + "...";
    }
}
