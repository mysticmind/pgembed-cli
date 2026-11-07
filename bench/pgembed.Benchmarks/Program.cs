using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Sql;
using PgSqlParser;
using Spectre.Console;

namespace PgEmbed.Cli.Benchmarks;

/// <summary>
/// The core's hot paths, without a server: what the prompt does per keystroke, what sql does before running, and
/// rendering. Run with: dotnet run -c Release --project bench/pgembed.Benchmarks -- --filter '*'
/// </summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

internal static class Samples
{
    /// <summary>A 200-line statement, the size the prompt's per-keystroke budget is set for.</summary>
    public static readonly string LongStatement = string.Join("\n",
        ["SELECT o.id,", .. Enumerable.Range(1, 196).Select(i => $"       coalesce(o.c{i}, 0) + {i} AS c{i},"), "       o.total", "FROM sales.orders o JOIN sales.customers c ON c.id = o.customer_id", "WHERE o.created_at > now() - interval '7 days' ORDER BY o.id;"]);

    /// <summary>The same, with a syntax error in the middle, as while typing.</summary>
    public static readonly string LongStatementWithError = LongStatement.Replace("coalesce(o.c100, 0) + 100", "coalesce(o.c100, 0) + + FROM");

    public static readonly string Script = string.Join("\n",
        Enumerable.Range(1, 50).Select(i => $"INSERT INTO t VALUES ({i}, 'x;y'); UPDATE t SET a = {i} WHERE id = {i}; SELECT * FROM t WHERE id = {i};"));
}

[MemoryDiagnoser]
public class PromptBenchmarks
{
    [Benchmark(Description = "highlight, 200 lines")]
    public object Highlight() => SqlEditor.Highlight(Samples.LongStatement);

    [Benchmark(Description = "highlight, 200 lines with an error")]
    public object HighlightWithError() => SqlEditor.Highlight(Samples.LongStatementWithError);

    [Benchmark(Description = "is complete, 200 lines")]
    public bool IsComplete() => SqlEditor.IsComplete(Samples.LongStatement);

    [Benchmark(Description = "parser: Parse (full tree)")]
    public object Parse() => Parser.Parse(Samples.LongStatement);

    [Benchmark(Description = "parser: Fingerprint (no tree)")]
    public object Fingerprint() => Parser.Fingerprint(Samples.LongStatement);
}

[MemoryDiagnoser]
public class ScriptBenchmarks
{
    [Benchmark(Description = "split, 150 statements")]
    public object Split() => SqlRunner.Split(Samples.Script);

    [Benchmark(Description = "changes, 150 statements")]
    public object Changes() => SqlSafety.Changes(SqlRunner.Split(Samples.Script));

    [Benchmark(Description = "plain-read fast path, one statement")]
    public bool FastPath() => SqlSafety.IsPlainRead("SELECT * FROM t WHERE id = 1");

    [Benchmark(Description = "check, 150 statements")]
    public object Check() => SqlAnalysis.Check(Samples.Script);

    [Benchmark(Description = "format, 200 lines")]
    public object Format() => SqlAnalysis.Format(Samples.LongStatement);
}

[MemoryDiagnoser]
public class RenderBenchmarks
{
    private static readonly SqlColumn[] Columns = [new("id", "integer"), new("name", "text"), new("doc", "jsonb"), new("at", "timestamptz")];
    private object?[][] _rows = [];
    private readonly IAnsiConsole _console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) });

    [Params(10_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _rows = Enumerable.Range(1, Rows)
        .Select(i => new object?[] { i, $"name {i}", new SqlJson($"{{\"n\": {i}, \"tags\": [\"a\", \"b\"]}}"), DateTimeOffset.UnixEpoch.AddSeconds(i) })
        .ToArray();

    [Benchmark] public void Csv() => Render("csv");
    [Benchmark] public void Json() => Render("json");
    [Benchmark] public void Aligned() => Render("aligned");
    [Benchmark] public void Markdown() => Render("markdown");

    private void Render(string format)
    {
        var sink = Renderers.For(_console, TextWriter.Null, new SqlOutput { Format = format });
        sink.Begin("SELECT ...", null, Columns);
        foreach (var row in _rows) sink.Row(row);
        sink.End(new StatementSummary("SELECT ...", _rows.Length, null, false, 1));
        (sink as JsonRenderer)?.Complete();
    }
}
