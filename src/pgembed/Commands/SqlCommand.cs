using System.ComponentModel;
using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Sql;
using PgSqlParser;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PgEmbed.Cli.Commands;

internal sealed class SqlCommand(CliServices services) : AsyncCommand<SqlCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [Description("An instance name, or a connection string: Npgsql, postgresql:// URI, libpq key=value or JDBC")]
        public string Target { get; set; } = "";

        [CommandArgument(1, "[SQL]")]
        [Description("SQL to run; or use -f, or pipe it in. Without any, opens an interactive prompt")]
        public string? Sql { get; set; }

        [CommandOption("-f|--file <PATH>")]
        [Description("Run a SQL file")]
        public string? File { get; set; }

        [CommandOption("--db <DATABASE>")]
        [Description("Database (default: postgres for an instance, else the connection string's)")]
        public string? Database { get; set; }

        [CommandOption("--schema <SCHEMA>")]
        [Description("Schema for unqualified names (sets the search path)")]
        public string? Schema { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("table (fits the terminal; the default there), aligned (psql-style, one row per line; the default when output is redirected), json, csv or markdown")]
        public string? Format { get; set; }

        [CommandOption("--max-rows <N>")]
        [Description("Rows shown per result in table format (default: 500); other formats return all")]
        public int MaxRows { get; set; } = 500;

        [CommandOption("--timing")]
        [Description("Show how long each statement took")]
        public bool Timing { get; set; }

        [CommandOption("--check")]
        [Description("Only check the syntax, with the PostgreSQL parser; needs no server")]
        public bool Check { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Run statements that change the schema or existing rows without asking")]
        public bool Yes { get; set; }

        public override ValidationResult Validate() =>
            Format is null || SqlOutput.Formats.Contains(Format) ? ValidationResult.Success()
            : ValidationResult.Error($"--format is one of: {string.Join(", ", SqlOutput.Formats)}");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Json) settings.Format = "json"; // --json is shorthand for --format json here
        var cli = new CliContext(settings, services);
        // a terminal gets a table that fits it; files and pipes get one row per line
        var format = settings.Format ?? (services.OutputRedirected ? "aligned" : "table");
        var output = new SqlOutput { Format = format, MaxRows = settings.MaxRows, Timing = settings.Timing };
        var sql = await ReadSqlAsync(settings, ct);

        if (settings.Check)
        {
            return sql is null ? throw new PostgresEmbedException("Give SQL to check: as an argument, with -f <file>, or piped in.") : Check(cli, sql);
        }

        var (target, label) = await ResolveTargetAsync(cli, settings.Target, settings.Database, settings.Schema, ct);

        if (sql is null)
        {
            if (!cli.Console.Profile.Capabilities.Interactive || Console.IsOutputRedirected)
            {
                throw new PostgresEmbedException("Give SQL to run: as an argument, with -f <file>, or piped in.");
            }

            output.StreamJson = true;
            await using var reader = PlainReader.For(Path.Combine(cli.Workspace.CacheDir, "history", "sql"));
            return await new SqlShell(cli, target, label, output, settings.Yes).RunAsync(reader, ct);
        }

        var statements = SqlRunner.Split(sql);
        if (!SqlConfirmation.Ask(cli, SqlSafety.Changes(statements), settings.Yes))
        {
            cli.Console.MarkupLine("Nothing was run.");
            return 1;
        }

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(ct);

        var runner = new SqlRunner(cli, output);
        var ok = await runner.RunAsync(connection, statements, ct);
        if (output.Format == "json")
        {
            cli.WriteJson(runner.Error is { } e
                ? new { results = runner.Results, error = new { e.SqlState, e.Message, e.Position, e.Detail, e.Hint, e.Statement } }
                : (object)new { results = runner.Results });
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// An instance by name, or any connection string. --db and --schema apply to either. The connection
    /// is never pooled: each run is one session.
    /// </summary>
    internal static async Task<(NpgsqlConnectionStringBuilder Target, string Label)> ResolveTargetAsync(
        CliContext cli, string target, string? database, string? schema, CancellationToken ct)
    {
        NpgsqlConnectionStringBuilder builder;
        string label;

        if (Connections.IsConnectionString(target))
        {
            builder = Connections.Parse(target);
            label = $"{builder.Username ?? Environment.UserName}@{builder.Host ?? "localhost"}";
        }
        else
        {
            if (!cli.Instances.Exists(target)) throw new PostgresEmbedException($"There is no instance named '{target}'.");
            var status = await cli.Instances.StatusAsync(target, ct);
            if (status.State == PgInstanceState.Stopped || status.Port is null)
            {
                throw new PostgresEmbedException($"'{target}' is not running. Start it with: pgembed start {target}");
            }

            builder = new NpgsqlConnectionStringBuilder(InstanceManager.ConnectionString(status.Port.Value));
            label = target;
        }

        if (database is not null) builder.Database = database;
        if (schema is not null) builder.SearchPath = schema;
        builder.Pooling = false;
        return (builder, label);
    }

    private static async Task<string?> ReadSqlAsync(Settings settings, CancellationToken ct)
    {
        if (settings.Sql is { Length: > 0 } sql) return sql;
        if (settings.File is { } file) return await File.ReadAllTextAsync(file, ct);
        if (Console.IsInputRedirected) return await Console.In.ReadToEndAsync(ct);
        return null;
    }

    private static int Check(CliContext cli, string sql)
    {
        var parsed = Parser.Parse(sql);
        if (parsed.IsSuccess)
        {
            var count = SqlRunner.Split(sql).Count;
            if (cli.Settings.Json) cli.WriteJson(new { valid = true, statements = count });
            else cli.Console.MarkupLineInterpolated($"[green]✓[/] Valid SQL: {count} statement{(count == 1 ? "" : "s")}");
            return 0;
        }

        if (cli.Settings.Json) cli.WriteJson(new { valid = false, error = parsed.Error.Message, position = parsed.Error.CursorPos });
        else SqlRunner.ReportError(cli, sql, parsed.Error.Message ?? "syntax error", parsed.Error.CursorPos);
        return 1;
    }
}
