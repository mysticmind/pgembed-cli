using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console;

namespace PgEmbed.Cli.Sql;

internal sealed record LineResult(bool Success, string Text);

/// <summary>Where the shell reads its input: the line editor, or a script in tests.</summary>
internal interface ILineReader : IAsyncDisposable
{
    /// <summary>The next input, or null at the end. Success is false when the line was cancelled (Ctrl+C).</summary>
    Task<LineResult?> ReadAsync(string prompt, Func<SqlCatalog> catalog);
}

/// <summary>The interactive prompt: SQL and psql-style backslash commands against one connection.</summary>
internal sealed class SqlShell(CliContext cli, NpgsqlConnectionStringBuilder target, string label, SqlOutput output, bool yes)
{
    private NpgsqlConnection? _connection;
    private SqlCatalog _catalog = SqlCatalog.Empty;
    private CancellationTokenSource? _running;
    private readonly SqlSession _session = new();

    private string Database => target.Database ?? target.Username ?? Environment.UserName;

    public async Task<int> RunAsync(ILineReader reader, CancellationToken ct)
    {
        // the parser's first use costs about 90 ms; pay it while connecting, not on the first keystroke
        _ = Task.Run(() => PgSqlParser.Parser.Parse("SELECT 1"), CancellationToken.None);
        await ConnectAsync(ct);
        cli.Console.MarkupLineInterpolated(
            $"Connected to [bold]{label}[/], database [bold]{Database}[/] (PostgreSQL {_connection!.PostgreSqlVersion}). [dim]\\? for help, \\q to quit.[/]");

        // Ctrl+C while a statement runs cancels the statement, not the shell
        Interrupts.Handler = () =>
        {
            _running?.Cancel();
            return true;
        };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadAsync($"{label}/{Database}=> ", () => _catalog);
                if (line is null) break;
                if (!line.Success) continue;

                var text = line.Text.Trim();
                if (text.Length == 0) continue;
                if (text is "exit" or "quit" or "\\q") break;

                try
                {
                    if (text.StartsWith('\\')) await MetaAsync(text, ct);
                    else await RunSqlAsync(text, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    cli.Console.MarkupLine("[yellow]Cancelled.[/]");
                }
                catch (Exception ex) when (ex is NpgsqlException or PostgresEmbedException or IOException)
                {
                    cli.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
                }
            }
        }
        finally
        {
            Interrupts.Handler = null;
            if (_connection is not null) await _connection.DisposeAsync();
        }

        return 0;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _connection = new NpgsqlConnection(target.ConnectionString);
        _session.Reset(); // a new session has no transaction open
        await _connection.OpenAsync(ct);
        await ReloadCatalogAsync(ct);
    }

    private async Task ReloadCatalogAsync(CancellationToken ct)
    {
        try
        {
            _catalog = await SqlCatalog.LoadAsync(_connection!, ct);
        }
        catch (PostgresException)
        {
            _catalog = SqlCatalog.Empty; // completion is a convenience; never fail over it
        }
    }

    private async Task<NpgsqlConnection> ConnectionAsync(CancellationToken ct)
    {
        // reconnect after the server went away or the connection broke
        if (_connection is null || _connection.FullState.HasFlag(System.Data.ConnectionState.Broken) || _connection.State == System.Data.ConnectionState.Closed)
        {
            await ConnectAsync(ct);
        }

        return _connection!;
    }

    private async Task RunSqlAsync(string sql, CancellationToken ct)
    {
        var statements = SqlRunner.Split(sql);
        var changes = SqlSafety.Changes(statements);
        if (!SqlConfirmation.Ask(cli, changes, yes))
        {
            cli.Console.MarkupLine("[dim]Not run.[/]");
            return;
        }

        var connection = await ConnectionAsync(ct);
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = running;
        try
        {
            await new SqlRunner(cli, output, _session).RunAsync(connection, statements, running.Token);
        }
        finally
        {
            _running = null;
        }

        if (changes.Count > 0) await ReloadCatalogAsync(ct);
    }

    private async Task MetaAsync(string text, CancellationToken ct)
    {
        var (command, argument) = MetaCommands.Split(text);
        var runner = new SqlRunner(cli, output, _session);
        if (await MetaCommands.TryRunAsync(await ConnectionAsync(ct), command, argument, runner, ct)) return;

        switch (command)
        {
            case "\\?":
                Help();
                break;

            case "\\c" when argument is not null:
                var previous = target.Database;
                target.Database = argument;
                try
                {
                    await ConnectAsync(ct);
                    cli.Console.MarkupLineInterpolated($"Connected to database [bold]{argument}[/].");
                }
                catch
                {
                    target.Database = previous;
                    await ConnectAsync(ct);
                    throw;
                }

                break;

            case "\\conninfo":
                cli.Console.WriteLine(Connections.Redacted(target));
                break;

            case "\\x":
                output.Expanded = argument switch { "on" => true, "off" => false, _ => !output.Expanded };
                cli.Console.MarkupLineInterpolated($"Expanded display is {(output.Expanded ? "on" : "off")}.");
                break;

            case "\\timing":
                output.Timing = argument switch { "on" => true, "off" => false, _ => !output.Timing };
                cli.Console.MarkupLineInterpolated($"Timing is {(output.Timing ? "on" : "off")}.");
                break;

            case "\\format" when argument is not null && SqlOutput.Formats.Contains(argument):
                output.Format = argument;
                cli.Console.MarkupLineInterpolated($"Output format is {argument}.");
                break;

            case "\\pager":
                var on = argument switch { "on" => true, "off" => false, _ => output.PagerCommand is null };
                output.PagerCommand = on ? Pager.Command() : null;
                if (on && output.ScreenHeight == 0) output.ScreenHeight = Pager.ScreenHeight();
                if (on && output.PagerCommand is null) cli.Console.MarkupLineInterpolated($"[yellow]No pager to use:[/] set {Pager.Variable} or PAGER.");
                else if (on) cli.Console.MarkupLineInterpolated($"Pager is on ({output.PagerCommand}).");
                else cli.Console.MarkupLine("Pager is off.");
                break;

            case "\\i" when argument is not null:
                await RunSqlAsync(await File.ReadAllTextAsync(argument, ct), ct);
                break;

            default:
                cli.Console.MarkupLineInterpolated($"[red]Unknown or incomplete command[/] {command}. [dim]\\? lists the commands.[/]");
                break;
        }
    }

    private void Help()
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn();
        void Row(string command, string what) => grid.AddRow(new Text(command), new Text(what));
        Row("\\d [name]", "Tables, views and sequences; or one table's columns, indexes and constraints");
        Row("\\dt [pattern]", "Tables (* and ? wildcards)");
        Row("\\dv [pattern]", "Views");
        Row("\\df [pattern]", "Functions");
        Row("\\dn [pattern]", "Schemas");
        Row("\\l", "Databases");
        Row("\\c <database>", "Connect to another database");
        Row("\\conninfo", "The connection string (password hidden)");
        Row("\\x [on|off]", "Expanded display: one block per row");
        Row("\\timing [on|off]", "Show how long statements take");
        Row("\\format <format>", "table, aligned (psql-style), json, csv or markdown");
        Row("\\pager [on|off]", "Open results taller than the screen in a pager (PGEMBED_PAGER, PAGER or less)");
        Row("\\i <file>", "Run a SQL file");
        Row("\\q", "Quit (also: exit, quit)");
        cli.Console.Write(grid);
        cli.Console.MarkupLine(
            "[dim]Enter runs the SQL once it ends with a semicolon; until then it starts a new line. Tab completes. " +
            "Statements that change the schema or existing rows ask first. Ctrl+C cancels a running statement.[/]");
    }
}
