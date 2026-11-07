using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PgEmbed.Cli.Core;
using Spectre.Console;

namespace PgEmbed.Cli.Sql;

/// <summary>Turns result values into text the same way in every text format.</summary>
internal static class Values
{
    public static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    public static bool IsNumber(object? value) => value is int or long or short or decimal or double or float;

    /// <summary>A json or jsonb value indented for reading; the text as it is when it does not parse.</summary>
    public static string Pretty(SqlJson json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.Text);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return json.Text;
        }
    }

    public static string Affected(int? rowsAffected) =>
        rowsAffected is >= 0 ? $"{rowsAffected} row{(rowsAffected == 1 ? "" : "s")} affected" : "OK";

    public static string RowCount(long rows) => $"{rows} row{(rows == 1 ? "" : "s")}";
}

/// <summary>Picks the renderer for a format. Every renderer writes each result as it completes.</summary>
internal static class Renderers
{
    public static IResultSink For(IAnsiConsole console, TextWriter output, SqlOutput options) => options.Format switch
    {
        "aligned" => new AlignedRenderer(console, output, options),
        "csv" => new CsvRenderer(console, output),
        "markdown" => new MarkdownRenderer(console, output),
        "json" => new JsonRenderer(console, output, options.StreamJson),
        _ => new TableRenderer(console, options)
    };

    /// <summary>The error, then its line with a caret under the position (1-based, in code points, as Postgres reports it).</summary>
    public static void ReportError(IAnsiConsole console, string sql, string message, int position, string? detail = null, string? hint = null)
    {
        console.MarkupLineInterpolated($"[red]ERROR:[/] {message}");
        if (position > 0 && Connections.CodePointToIndex(sql, position) is var index && index <= sql.Length)
        {
            var lineStart = index == 0 ? 0 : sql.LastIndexOf('\n', index - 1) + 1;
            var lineEnd = sql.IndexOf('\n', index);
            var line = sql[lineStart..(lineEnd < 0 ? sql.Length : lineEnd)].TrimEnd('\r');
            var lineNumber = sql[..lineStart].Count(c => c == '\n') + 1;
            // the caret column counts what the terminal shows: code points, not UTF-16 units
            var column = Connections.CountCodePoints(sql[lineStart..index]);

            var prefix = $"LINE {lineNumber}: ";
            console.WriteLine(prefix + line);
            console.WriteLine(new string(' ', prefix.Length + column) + "^");
        }

        if (detail is not null) console.MarkupLineInterpolated($"[dim]DETAIL:[/] {detail}");
        if (hint is not null) console.MarkupLineInterpolated($"[dim]HINT:[/] {hint}");
    }

    public static void ReportError(IAnsiConsole console, StatementError e) =>
        ReportError(console, e.Statement, e.Message, e.Position, e.Detail, e.Hint);
}

/// <summary>Writes to the plain output; errors go to the console after what was written so far.</summary>
internal abstract class TextRenderer(IAnsiConsole console, TextWriter output) : IResultSink
{
    protected TextWriter Output { get; } = output;
    protected IAnsiConsole Console { get; } = console;

    public abstract void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns);
    public abstract void Row(object?[] values);
    public abstract void End(StatementSummary summary);

    public void Fail(StatementError error)
    {
        Output.Flush();
        Renderers.ReportError(Console, error);
    }
}

internal sealed class CsvRenderer(IAnsiConsole console, TextWriter output) : TextRenderer(console, output)
{
    public override void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) Output.Write(',');
            Output.Write(Csv(columns[i].Name));
        }

        Output.WriteLine();
    }

    public override void Row(object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) Output.Write(',');
            Output.Write(Csv(Values.Text(values[i])));
        }

        Output.WriteLine();
    }

    public override void End(StatementSummary summary) => Output.Flush();

    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

internal sealed class MarkdownRenderer(IAnsiConsole console, TextWriter output) : TextRenderer(console, output)
{
    public override void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns)
    {
        if (title is not null) Output.WriteLine($"**{Cell(title)}**\n");
        Output.WriteLine("| " + string.Join(" | ", columns.Select(c => Cell(c.Name))) + " |");
        Output.WriteLine("|" + string.Concat(columns.Select(_ => " --- |")));
    }

    public override void Row(object?[] values) =>
        Output.WriteLine("| " + string.Join(" | ", values.Select(v => Cell(Values.Text(v)))) + " |");

    public override void End(StatementSummary summary)
    {
        if (summary.HasResultSet) Output.WriteLine();
        Output.Flush();
    }

    // a line break would end the table row
    private static string Cell(string value) => value.Replace("|", "\\|").ReplaceLineEndings("<br>");
}

/// <summary>
/// psql-style text, never wrapped. Column widths come from the first rows (a window), as psql does; when
/// a result is longer, the rest streams with those widths, so memory stays bounded. In a terminal, a result
/// taller than the screen opens in the pager.
/// </summary>
internal sealed class AlignedRenderer(IAnsiConsole terminal, TextWriter output, SqlOutput options) : TextRenderer(terminal, output)
{
    public const int Window = 1000;

    private IReadOnlyList<SqlColumn> _columns = [];
    private readonly List<object?[]> _pending = [];
    private bool[]? _numeric;
    private int[]? _widths;
    private string? _title;
    private Pager? _pager;
    private bool _pagerClosed;

    private TextWriter Target => _pager?.Writer ?? Output;

    public bool Abandoned => _pagerClosed;

    public override void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns)
    {
        _title = title;
        _columns = columns;
        _pending.Clear();
        _widths = null;
        _numeric = null;
    }

    public override void Row(object?[] values)
    {
        if (_widths is not null)
        {
            Write(w => Aligned.WriteRow(w, values, _widths, _numeric!, Values.Text));
            return;
        }

        _pending.Add(values);
        if (_pending.Count >= Window && !options.Expanded) Flush();
    }

    public override void End(StatementSummary summary)
    {
        var timing = options.Timing ? $" ({summary.Milliseconds:0.0} ms)" : "";
        if (!summary.HasResultSet)
        {
            Output.WriteLine(Values.Affected(summary.RowsAffected) + timing);
            Output.Flush();
            return;
        }

        if (options.Expanded)
        {
            OpenPager(_pending.Count * (_columns.Count + 1) + 2);
            Write(w =>
            {
                if (_title is not null) w.WriteLine(_title);
                Aligned.Write(w, _columns.Select(c => c.Name).ToList(), _pending, true, Values.Text);
            });
        }
        else if (_widths is null) Flush();

        Write(w =>
        {
            w.WriteLine($"({Values.RowCount(summary.Rows)}{timing})");
            w.WriteLine();
        });
        Output.Flush();
        _pending.Clear();
        ClosePager();
    }

    private void Flush()
    {
        // a full window means the result is at least that tall
        OpenPager(_pending.Count + 4);
        var names = _columns.Select(c => c.Name).ToList();
        (_widths, _numeric) = Aligned.Measure(names, _pending, Values.Text);
        Write(w =>
        {
            if (_title is not null) w.WriteLine(_title);
            Aligned.WriteHeader(w, names, _widths);
            foreach (var row in _pending) Aligned.WriteRow(w, row, _widths, _numeric, Values.Text);
        });
        _pending.Clear();
    }

    private void OpenPager(int lines)
    {
        if (_pager is null && options.Paging(lines) is { } command) _pager = Pager.Start(command, Console);
        _pagerClosed = false;
    }

    private void ClosePager()
    {
        _pager?.Dispose();
        _pager = null;
    }

    // once the user leaves the pager, the rest of the result is dropped
    private void Write(Action<TextWriter> write)
    {
        if (_pagerClosed) return;
        try
        {
            write(Target);
        }
        catch (IOException) when (_pager is not null)
        {
            _pagerClosed = true;
        }
    }
}

/// <summary>
/// The Spectre table that fits the terminal. It holds at most <see cref="SqlOutput.MaxRows"/> rows, since the
/// executor stops reading there. A result taller than the screen opens in the pager.
/// </summary>
internal sealed class TableRenderer(IAnsiConsole console, SqlOutput options) : IResultSink
{
    private string? _title;
    private IReadOnlyList<SqlColumn> _columns = [];
    private readonly List<object?[]> _rows = [];

    public void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns)
    {
        _title = title;
        _columns = columns;
        _rows.Clear();
    }

    public void Row(object?[] values) => _rows.Add(values);

    public void End(StatementSummary summary)
    {
        var timing = options.Timing ? $" ({summary.Milliseconds:0.0} ms)" : "";
        if (!summary.HasResultSet)
        {
            console.MarkupLineInterpolated($"[dim]{Values.Affected(summary.RowsAffected)}{timing}[/]");
            return;
        }

        using var pager = options.Paging(EstimatedLines()) is { } command ? Pager.Start(command, console) : null;
        try
        {
            Render(pager?.Console ?? console, summary, timing);
        }
        catch (IOException) when (pager is not null)
        {
            // the user left the pager before the end
        }

        _rows.Clear();
    }

    public void Fail(StatementError error) => Renderers.ReportError(console, error);

    private void Render(IAnsiConsole target, StatementSummary summary, string timing)
    {
        if (_title is not null) target.MarkupLineInterpolated($"[bold]{_title}[/]");
        if (options.Expanded) WriteExpanded(target);
        else
        {
            var table = new Table().Border(TableBorder.Simple);
            foreach (var column in _columns) table.AddColumn(Markup.Escape(column.Name));
            foreach (var row in _rows) table.AddRow(row.Select(v => v is null ? "[dim]NULL[/]" : Markup.Escape(Values.Text(v))).ToArray());
            target.Write(table);
        }

        var count = summary.Truncated
            ? $"first {Values.RowCount(summary.Rows)}; more not shown: use --max-rows, or --format csv or json for all"
            : Values.RowCount(summary.Rows);
        target.MarkupLineInterpolated($"[dim]({count}{timing})[/]");
    }

    // header, rule, padding and footer; a row with line breaks takes several lines
    private int EstimatedLines() => options.Expanded
        ? _rows.Count * (_columns.Count + 1) + 1
        : _rows.Sum(r => r.Max(v => v is string text ? text.AsSpan().Count('\n') + 1 : 1)) + 6;

    // one block per row, like psql's \x; json values indented
    private void WriteExpanded(IAnsiConsole target)
    {
        for (var r = 0; r < _rows.Count; r++)
        {
            target.MarkupLineInterpolated($"[dim]-[[ RECORD {r + 1} ]]-[/]");
            var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
            for (var c = 0; c < _columns.Count; c++)
            {
                var value = _rows[r][c];
                grid.AddRow(new Text(_columns[c].Name), value switch
                {
                    null => new Markup("[dim]NULL[/]"),
                    SqlJson json => new Text(Values.Pretty(json)),
                    _ => new Text(Values.Text(value))
                });
            }

            target.Write(grid);
        }
    }
}

/// <summary>
/// JSON written as rows arrive: one document <c>{ "results": [...], "error": ... }</c>, or with <c>stream</c> (the
/// interactive prompt) one document per statement. json and jsonb values are nested, not strings. The layout
/// is written by hand so each row is one compact line: a long result stays readable, small and greppable.
/// </summary>
internal sealed class JsonRenderer : IResultSink, IDisposable
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private readonly IAnsiConsole _console;
    private readonly TextWriter _output;
    private readonly bool _stream;
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly Utf8JsonWriter _writer;
    private bool _documentOpen;
    private bool _finished;
    private bool _inResult;
    private int _statements;
    private long _rows;
    private string _indent = "";

    public JsonRenderer(IAnsiConsole console, TextWriter output, bool stream)
    {
        _console = console;
        _output = output;
        _stream = stream;
        _writer = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Encoder = Encoder });
    }

    public void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns)
    {
        StartStatement(statement);
        if (title is not null) Property("title", Encode(title));
        Property("columns", Encode(columns.Select(c => c.Name)));
        Property("types", Encode(columns.Select(c => c.DataType)));
        _output.Write($",\n{_indent}\"rows\": [");
        _rows = 0;
        _inResult = true;
    }

    public void Row(object?[] values)
    {
        _output.Write(_rows++ == 0 ? "\n" : ",\n");
        _output.Write(_indent);
        _output.Write("  ");
        _output.Write(Encode(values));
    }

    public void End(StatementSummary summary)
    {
        if (!summary.HasResultSet)
        {
            StartStatement(summary.Statement);
            Property("rowsAffected", (summary.RowsAffected ?? -1).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            CloseRows();
            Property("rowCount", summary.Rows.ToString(CultureInfo.InvariantCulture));
            Property("truncated", summary.Truncated ? "true" : "false");
        }

        Property("milliseconds", Math.Round(summary.Milliseconds, 3).ToString(CultureInfo.InvariantCulture));
        EndStatement();
    }

    public void Fail(StatementError error)
    {
        if (_inResult)
        {
            // failed after some rows were written: close that result, marked incomplete
            CloseRows();
            Property("rowCount", _rows.ToString(CultureInfo.InvariantCulture));
            Property("incomplete", "true");
            EndStatement();
        }

        if (_stream)
        {
            Renderers.ReportError(_console, error);
            return;
        }

        OpenDocument();
        _output.Write(_statements == 0 ? "],\n" : "\n  ],\n");
        _output.Write("  \"error\": ");
        _output.Write(EncodeError(error));
        _output.WriteLine("\n}");
        _output.Flush();
        _finished = true;
    }

    /// <summary>Closes the document of a one-shot run. Nothing to do after a failure, which closed it.</summary>
    public void Complete()
    {
        if (_stream || _finished) return;
        _finished = true;
        OpenDocument();
        _output.WriteLine(_statements == 0 ? "]\n}" : "\n  ]\n}");
        _output.Flush();
    }

    public void Dispose() => _writer.Dispose();

    private void StartStatement(string statement)
    {
        if (_stream)
        {
            _indent = "  ";
            _output.Write("{");
        }
        else
        {
            OpenDocument();
            _indent = "      ";
            _output.Write(_statements == 0 ? "\n    {" : ",\n    {");
        }

        _output.Write($"\n{_indent}\"statement\": {Encode(statement)}");
    }

    private void EndStatement()
    {
        _statements++;
        if (_stream)
        {
            _output.WriteLine("\n}");
            _output.Flush();
        }
        else
        {
            _output.Write("\n    }");
            _output.Flush();
        }
    }

    private void CloseRows()
    {
        _output.Write(_rows == 0 ? "]" : $"\n{_indent}]");
        _inResult = false;
    }

    private void Property(string name, string json) => _output.Write($",\n{_indent}\"{name}\": {json}");

    private void OpenDocument()
    {
        if (_documentOpen || _stream) return;
        _output.Write("{\n  \"results\": [");
        _documentOpen = true;
    }

    private string Encode(string value) => Write(w => w.WriteStringValue(value));

    private string Encode(IEnumerable<string> values) => Write(w =>
    {
        w.WriteStartArray();
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    });

    private string Encode(object?[] values) => Write(w =>
    {
        w.WriteStartArray();
        foreach (var value in values) WriteValue(w, value);
        w.WriteEndArray();
    });

    private string EncodeError(StatementError error)
    {
        using var document = JsonDocument.Parse(Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("sqlState", error.SqlState);
            w.WriteString("message", error.Message);
            w.WriteNumber("position", error.Position);
            if (error.Detail is not null) w.WriteString("detail", error.Detail);
            if (error.Hint is not null) w.WriteString("hint", error.Hint);
            w.WriteString("statement", error.Statement);
            w.WriteEndObject();
        }));
        return Json.Serialize(document.RootElement).ReplaceLineEndings("\n  ");
    }

    private string Write(Action<Utf8JsonWriter> write)
    {
        _buffer.ResetWrittenCount();
        _writer.Reset();
        write(_writer);
        _writer.Flush();
        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }

    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case SqlJson json: WriteJson(writer, json); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case short s16: writer.WriteNumberValue(s16); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case double d when double.IsFinite(d): writer.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): writer.WriteNumberValue(f); break;
            case Guid g: writer.WriteStringValue(g); break;
            case DateTime dt: writer.WriteStringValue(dt); break;
            case DateTimeOffset dto: writer.WriteStringValue(dto); break;
            default: writer.WriteStringValue(Values.Text(value)); break; // NaN, Infinity and anything else as text
        }
    }

    // re-encoded compactly: a json value keeps no line breaks of its own, so a row stays one line
    private static void WriteJson(Utf8JsonWriter writer, SqlJson json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.Text);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json.Text);
        }
    }
}
