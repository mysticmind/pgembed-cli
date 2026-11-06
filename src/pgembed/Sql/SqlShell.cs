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

    private string Database => target.Database ?? target.Username ?? Environment.UserName;

    public async Task<int> RunAsync(ILineReader reader, CancellationToken ct)
    {
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
            await new SqlRunner(cli, output).RunAsync(connection, statements, running.Token);
        }
        finally
        {
            _running = null;
        }

        if (changes.Count > 0) await ReloadCatalogAsync(ct);
    }

    private const string Relations = """
        SELECT n.nspname AS schema, c.relname AS name,
               CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'partitioned table' WHEN 'v' THEN 'view'
                              WHEN 'm' THEN 'materialized view' WHEN 'S' THEN 'sequence' WHEN 'f' THEN 'foreign table' END AS type,
               pg_size_pretty(pg_total_relation_size(c.oid)) AS size
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind::text = ANY($1) AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
          AND c.relname ILIKE $2
        ORDER BY 1, 2
        """;

    private async Task MetaAsync(string text, CancellationToken ct)
    {
        var parts = text.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0];
        var argument = parts.Length > 1 ? parts[1].TrimEnd(';') : null;
        var runner = new SqlRunner(cli, output);
        var pattern = argument is null ? "%" : argument.Replace('*', '%').Replace('?', '_');

        switch (command)
        {
            case "\\?":
                Help();
                break;

            case "\\l":
                await runner.QueryAsync(await ConnectionAsync(ct), """
                    SELECT datname AS name, pg_get_userbyid(datdba) AS owner, pg_encoding_to_char(encoding) AS encoding,
                           pg_size_pretty(pg_database_size(datname)) AS size
                    FROM pg_database WHERE NOT datistemplate ORDER BY 1
                    """, [], null, ct);
                break;

            case "\\dn":
                await runner.QueryAsync(await ConnectionAsync(ct), """
                    SELECT nspname AS name, pg_get_userbyid(nspowner) AS owner
                    FROM pg_namespace WHERE nspname <> 'information_schema' AND nspname NOT LIKE 'pg\_%' AND nspname ILIKE $1 ORDER BY 1
                    """, [pattern], null, ct);
                break;

            case "\\dt":
                await runner.QueryAsync(await ConnectionAsync(ct), Relations, [new[] { "r", "p" }, pattern], null, ct);
                break;

            case "\\dv":
                await runner.QueryAsync(await ConnectionAsync(ct), Relations, [new[] { "v", "m" }, pattern], null, ct);
                break;

            case "\\df":
                await runner.QueryAsync(await ConnectionAsync(ct), """
                    SELECT n.nspname AS schema, p.proname AS name, pg_get_function_result(p.oid) AS result,
                           pg_get_function_arguments(p.oid) AS arguments
                    FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%' AND p.proname ILIKE $1
                    ORDER BY 1, 2
                    """, [pattern], null, ct);
                break;

            case "\\d" when argument is null:
                await runner.QueryAsync(await ConnectionAsync(ct), Relations, [new[] { "r", "p", "v", "m", "S", "f" }, "%"], null, ct);
                break;

            case "\\d":
                await DescribeAsync(runner, argument, ct);
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

            case "\\i" when argument is not null:
                await RunSqlAsync(await File.ReadAllTextAsync(argument, ct), ct);
                break;

            default:
                cli.Console.MarkupLineInterpolated($"[red]Unknown or incomplete command[/] {command}. [dim]\\? lists the commands.[/]");
                break;
        }
    }

    private async Task DescribeAsync(SqlRunner runner, string name, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct);
        var ok = await runner.QueryAsync(connection, """
            SELECT a.attname AS column, format_type(a.atttypid, a.atttypmod) AS type,
                   CASE WHEN a.attnotnull THEN 'not null' ELSE '' END AS nullable,
                   coalesce(pg_get_expr(d.adbin, d.adrelid), '') AS default
            FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = $1::regclass AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, [name], name, ct);
        if (!ok) return;

        await runner.QueryAsync(connection, """
            SELECT c.relname AS index, pg_get_indexdef(i.indexrelid) AS definition
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE i.indrelid = $1::regclass ORDER BY 1
            """, [name], "Indexes", ct);

        await runner.QueryAsync(connection, """
            SELECT conname AS constraint, pg_get_constraintdef(oid) AS definition
            FROM pg_constraint WHERE conrelid = $1::regclass AND contype IN ('p', 'u', 'f', 'c', 'x') ORDER BY 1
            """, [name], "Constraints", ct);
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
        Row("\\i <file>", "Run a SQL file");
        Row("\\q", "Quit (also: exit, quit)");
        cli.Console.Write(grid);
        cli.Console.MarkupLine(
            "[dim]Enter runs the SQL once it ends with a semicolon; until then it starts a new line. Tab completes. " +
            "Statements that change the schema or existing rows ask first. Ctrl+C cancels a running statement.[/]");
    }
}
