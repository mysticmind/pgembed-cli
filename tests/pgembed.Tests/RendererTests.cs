using System.Text.Json;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Sql;
using Spectre.Console.Testing;

namespace PgEmbed.Cli.Tests;

/// <summary>The renderers, fed directly: no server needed.</summary>
public class RendererTests
{
    private static readonly SqlColumn[] Columns = [new("doc", "jsonb"), new("note", "text"), new("n", "integer")];

    private static (TestConsole Console, StringWriter Out, IResultSink Sink) Sink(string format, bool stream = false, bool expanded = false)
    {
        var console = new TestConsole().Width(200);
        var output = new StringWriter();
        return (console, output, Renderers.For(console, output, new SqlOutput { Format = format, StreamJson = stream, Expanded = expanded }));
    }

    private static void Feed(IResultSink sink, params object?[][] rows)
    {
        sink.Begin("SELECT ...", null, Columns);
        foreach (var row in rows) sink.Row(row);
        sink.End(new StatementSummary("SELECT ...", rows.Length, null, false, 1.5));
    }

    [Fact]
    public void Json_nests_json_values_and_keeps_each_row_on_one_line()
    {
        var (_, output, sink) = Sink("json");
        Feed(sink, [new SqlJson("{\"a\":   1,\n \"b\": [1, 2]}"), "it's \"quoted\"", 7], [null, null, null]);
        ((JsonRenderer)sink).Complete();

        var text = output.ToString();
        Assert.Contains("[{\"a\":1,\"b\":[1,2]},\"it's \\\"quoted\\\"\",7]", text); // nested, compact, one line
        Assert.DoesNotContain("\\u0022", text);
        Assert.DoesNotContain("\\u0027", text);

        using var document = JsonDocument.Parse(text);
        var result = document.RootElement.GetProperty("results")[0];
        Assert.Equal(JsonValueKind.Object, result.GetProperty("rows")[0][0].ValueKind);
        Assert.Equal(1, result.GetProperty("rows")[0][0].GetProperty("a").GetInt32());
        Assert.Equal("jsonb", result.GetProperty("types")[0].GetString());
        Assert.Equal(2, result.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public void Json_closes_the_document_with_the_error_after_earlier_results()
    {
        var (_, output, sink) = Sink("json");
        Feed(sink, [null, "a", 1]);
        sink.Begin("SELECT 1/0", null, Columns);
        sink.Fail(new StatementError("SELECT 1/0", "22012", "division by zero", 0, null, null));
        ((JsonRenderer)sink).Complete(); // nothing more to write

        using var document = JsonDocument.Parse(output.ToString());
        var results = document.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());                         // the failed one is closed, marked incomplete
        Assert.True(results[1].GetProperty("incomplete").GetBoolean());
        Assert.Equal("22012", document.RootElement.GetProperty("error").GetProperty("sqlState").GetString());
    }

    [Fact]
    public void Json_with_no_statements_is_still_a_document()
    {
        var (_, output, sink) = Sink("json");
        ((JsonRenderer)sink).Complete();

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, document.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void Json_streamed_gives_one_document_per_statement()
    {
        var (_, output, sink) = Sink("json", stream: true);
        Feed(sink, [null, "a", 1]);
        sink.End(new StatementSummary("CREATE TABLE t()", 0, -1, false, 1));

        var documents = output.ToString().Split("\n}\n", StringSplitOptions.RemoveEmptyEntries).Select(d => d + "\n}").ToList();
        Assert.Equal(2, documents.Count);
        foreach (var d in documents) JsonDocument.Parse(d).Dispose();
    }

    [Fact]
    public void Markdown_keeps_a_value_with_line_breaks_in_its_row()
    {
        var (_, output, sink) = Sink("markdown");
        Feed(sink, [new SqlJson("{\"a\": 1,\n\"b\": 2}"), "x|y", 1]);

        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("| {\"a\": 1,<br>\"b\": 2} | x\\|y | 1 |", lines[2]);
    }

    [Fact]
    public void Aligned_streams_past_its_window_with_the_measured_widths()
    {
        var (_, output, sink) = Sink("aligned");
        var rows = Enumerable.Range(1, AlignedRenderer.Window + 5).Select(i => new object?[] { null, "x", i }).ToArray();
        Feed(sink, rows);

        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(" doc | note |  n", lines[0]);
        Assert.Equal("     | x    | 1005", lines[2 + AlignedRenderer.Window + 4]); // wider than measured: runs longer
        Assert.Contains($"({AlignedRenderer.Window + 5} rows)", output.ToString());
    }

    [Fact]
    public void Table_says_when_rows_were_left_unread_and_pretty_prints_json_when_expanded()
    {
        var (console, _, sink) = Sink("table", expanded: true);
        sink.Begin("SELECT ...", null, Columns);
        sink.Row([new SqlJson("{\"a\":1}"), "x", 1]);
        sink.End(new StatementSummary("SELECT ...", 1, null, Truncated: true, 1));

        Assert.Contains("\"a\": 1", console.Output);         // indented, not {"a":1}
        Assert.Contains("first 1 row; more not shown", console.Output);
    }
}

public class SqlSessionTests
{
    [Theory]
    [InlineData("BEGIN", true)]
    [InlineData("START TRANSACTION", true)]
    [InlineData("BEGIN; SELECT 1", true)]
    [InlineData("BEGIN; COMMIT", false)]
    [InlineData("BEGIN; ROLLBACK", false)]
    [InlineData("BEGIN; SAVEPOINT a; ROLLBACK TO a", true)]
    [InlineData("SELECT 1", false)]
    public void Tracks_whether_a_transaction_is_open(string script, bool open)
    {
        var session = new SqlSession();
        foreach (var statement in SqlRunner.Split(script)) session.Observe(statement);
        Assert.Equal(open, session.InTransaction);
    }
}
