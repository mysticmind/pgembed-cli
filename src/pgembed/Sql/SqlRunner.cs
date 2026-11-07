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

    /// <summary>
    /// Output goes to a terminal: control characters in data are shown, not sent, so a value cannot retitle the
    /// window, write the clipboard (OSC 52) or hide text. Files and pipes get the data byte for byte.
    /// </summary>
    public bool Terminal { get; set; }

    /// <summary>The pager for results taller than the screen; null when output is not a terminal or paging is off.</summary>
    public string? PagerCommand { get; set; }

    /// <summary>Lines the terminal shows; a result taller than this opens in the pager.</summary>
    public int ScreenHeight { get; set; }

    /// <summary>The pager command when a result of this many lines should be paged; otherwise null.</summary>
    public string? Paging(int lines) => PagerCommand is not null && ScreenHeight > 0 && lines > ScreenHeight ? PagerCommand : null;
}

/// <summary>
/// Runs statements one at a time through the executor and shows each result, in the chosen format, as it
/// arrives. The executor knows nothing of output; the renderer knows nothing of connections.
/// </summary>
internal sealed class SqlRunner
{
    private readonly SqlOutput _output;
    private readonly SqlSession? _session;
    private readonly IResultSink _sink;

    public SqlRunner(CliContext cli, SqlOutput output, SqlSession? session = null)
        : this(cli.Console, cli.Services.Out, output, session)
    {
    }

    public SqlRunner(IAnsiConsole console, TextWriter writer, SqlOutput output, SqlSession? session = null)
    {
        _output = output;
        _session = session;
        _sink = new ErrorTracker(Renderers.For(console, writer, output), this);
    }

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
            if (!await SqlExecutor.RunAsync(connection, statement, [], null, Cap, _sink, _session, ct)) return false;
        }

        return true;
    }

    /// <summary>A catalog query for the describing commands (\d and friends), with parameters.</summary>
    public Task<bool> QueryAsync(NpgsqlConnection connection, string sql, object[] parameters, string? title, CancellationToken ct) =>
        SqlExecutor.RunAsync(connection, sql, parameters, title, Cap, _sink, _session, ct);

    /// <summary>Rows built in memory (catalog descriptions), shown like any query result.</summary>
    public void Show(string? title, IReadOnlyList<string> columns, IEnumerable<object?[]> rows) =>
        SqlExecutor.Show(_sink, title, columns, rows);

    /// <summary>Ends the output: closes the JSON document of a one-shot run.</summary>
    public void Complete()
    {
        if (_sink is ErrorTracker { Inner: JsonRenderer json }) json.Complete();
    }

    // only the table view caps rows; the other formats stream everything
    private long Cap => _output.Format == "table" ? _output.MaxRows : long.MaxValue;

    private sealed class ErrorTracker(IResultSink inner, SqlRunner runner) : IResultSink
    {
        public IResultSink Inner => inner;
        public void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns) => inner.Begin(statement, title, columns);
        public void Row(object?[] values) => inner.Row(values);
        public void End(StatementSummary summary) => inner.End(summary);

        public void Fail(StatementError error)
        {
            runner.Error = error;
            inner.Fail(error);
        }

        public bool Abandoned => inner.Abandoned;
    }
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
