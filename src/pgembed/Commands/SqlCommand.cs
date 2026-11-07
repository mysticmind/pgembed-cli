using System.ComponentModel;
using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Sql;
using PgSqlParser;
using Spectre.Console;

namespace PgEmbed.Cli.Commands;

internal sealed class SqlCommand(CliServices services) : AsyncCommand<SqlCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[TARGET]")]
        [Description("An instance name, or a connection string: Npgsql, postgresql:// URI, libpq key=value or JDBC. Not needed with --check")]
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
        [Description("Check the syntax and summarize each statement (tables, functions, whether it asks first); needs no server")]
        public bool Check { get; set; }

        [CommandOption("--no-pager")]
        [Description("Never open a pager (default: a result taller than the terminal opens in PGEMBED_PAGER, PAGER or less -SFX)")]
        public bool NoPager { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Run statements that change the schema or existing rows without asking")]
        public bool Yes { get; set; }

        public override ValidationResult Validate()
        {
            if (Format is not null && !SqlOutput.Formats.Contains(Format))
            {
                return ValidationResult.Error($"--format is one of: {string.Join(", ", SqlOutput.Formats)}");
            }

            return Target.Length == 0 && !Check
                ? ValidationResult.Error("Give a target: an instance name or a connection string, for example: pgembed sql app \"SELECT 1\"")
                : ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        StartupTrace.Mark("sql command");
        if (settings.Json) settings.Format = "json"; // --json is shorthand for --format json here
        var cli = new CliContext(settings, services);
        // a terminal gets a table that fits it; files and pipes get one row per line
        var format = settings.Format ?? (services.OutputRedirected ? "aligned" : "table");
        var output = new SqlOutput { Format = format, MaxRows = settings.MaxRows, Timing = settings.Timing, Terminal = !services.OutputRedirected };
        // a pager only for a person at a terminal
        if (!settings.NoPager && !services.OutputRedirected && cli.Console.Profile.Capabilities.Interactive)
        {
            output.PagerCommand = Pager.Command();
            output.ScreenHeight = Pager.ScreenHeight();
        }
        if (settings.Check)
        {
            // --check needs no server: in "sql --check '<sql>'" the one argument is the SQL, not a target
            var checkedSql = settings.Sql is null && settings.File is null && settings.Target.Length > 0 && IsSqlNotTarget(cli, settings.Target)
                ? settings.Target
                : await ReadSqlAsync(settings, ct);
            return string.IsNullOrWhiteSpace(checkedSql)
                ? throw new PostgresEmbedException("Give SQL to check: as an argument, with -f <file>, or piped in.")
                : Check(cli, checkedSql);
        }

        var sql = await ReadSqlAsync(settings, ct);

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

        if (MetaCommands.IsCommand(sql)) return await RunMetaAsync(cli, target, output, sql, ct);

        StartupTrace.Mark("target resolved");
        var statements = SqlRunner.Split(sql);
        if (!SqlConfirmation.Ask(cli, SqlSafety.Changes(statements), settings.Yes))
        {
            cli.Console.MarkupLine("Nothing was run.");
            return 1;
        }

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        StartupTrace.Mark("checked");
        await connection.OpenAsync(ct);
        StartupTrace.Mark("connected");

        var runner = new SqlRunner(cli, output, new SqlSession());
        var ok = await runner.RunAsync(connection, statements, ct);
        runner.Complete();
        StartupTrace.Mark("done");
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
            if (!cli.Instances.Exists(target))
            {
                // the target may be a connection string pgembed did not recognize: never repeat a password
                throw new PostgresEmbedException(Workspace.IsValidName(target)
                    ? $"There is no instance named '{target}'."
                    : $"'{Connections.Mask(target)}' is neither an instance name nor a connection string pgembed recognizes. " +
                      "A URI starts with postgresql://; libpq strings are key=value pairs, Npgsql ones are separated by ';'.");
            }
            var port = cli.Instances.RunningPort(target);
            if (port is null)
            {
                var status = await cli.Instances.StatusAsync(target, ct);
                port = status.State == PgInstanceState.Stopped ? null : status.Port;
            }

            if (port is null) throw new PostgresEmbedException($"'{target}' is not running. Start it with: pgembed start {target}");
            builder = new NpgsqlConnectionStringBuilder(InstanceManager.ConnectionString(port.Value, password: cli.Registry.Get(target)?.Password));
            // instances trust local connections: no Kerberos probe (on Alpine it prints a missing-library error)
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
            label = target;
        }

        if (database is not null) builder.Database = database;
        if (schema is not null) builder.SearchPath = schema;
        builder.Pooling = false;
        // no timeout from the command line (Npgsql's default is 30 seconds): a long migration runs; Ctrl+C cancels
        builder.CommandTimeout = 0;
        return (builder, label);
    }

    // pgembed sql app "\d books": the describing commands work outside the prompt too
    private static async Task<int> RunMetaAsync(CliContext cli, NpgsqlConnectionStringBuilder target, SqlOutput output, string text, CancellationToken ct)
    {
        var (command, argument) = MetaCommands.Split(text);
        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(ct);

        var runner = new SqlRunner(cli, output);
        if (!await MetaCommands.TryRunAsync(connection, command, argument, runner, ct))
        {
            throw new PostgresEmbedException(
                $"{command} works only in the interactive prompt. Here, use one of: {string.Join(", ", MetaCommands.Names)}.");
        }

        runner.Complete();
        return 0;
    }

    // an instance is a target; text that parses is SQL; then a connection string; anything else is SQL to report on
    private static bool IsSqlNotTarget(CliContext cli, string argument) =>
        !cli.Instances.Exists(argument) && (Parser.Parse(argument).IsSuccess || !Connections.IsConnectionString(argument));

    private static async Task<string?> ReadSqlAsync(Settings settings, CancellationToken ct)
    {
        if (settings.Sql is { Length: > 0 } sql) return sql;
        if (settings.File is { } file) return await File.ReadAllTextAsync(file, ct);
        if (Console.IsInputRedirected) return await Console.In.ReadToEndAsync(ct);
        return null;
    }

    private static int Check(CliContext cli, string sql)
    {
        var result = SqlAnalysis.Check(sql);
        if (cli.Settings.Json)
        {
            cli.WriteJson(result);
            return result.Valid ? 0 : 1;
        }

        if (!result.Valid)
        {
            Renderers.ReportError(cli.Console, sql, result.Error!.Message, result.Error.Position);
            return 1;
        }

        var count = result.Statements.Count;
        cli.Console.MarkupLineInterpolated($"[green]✓[/] Valid SQL: {count} statement{(count == 1 ? "" : "s")}");
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        for (var i = 0; i < count; i++)
        {
            var statement = result.Statements[i];
            var touches = string.Join("; ", new[]
            {
                Uses(statement, "read", "reads"), Uses(statement, "write", "writes"), Uses(statement, "define", "defines"),
                statement.Functions.Count > 0 ? "calls " + string.Join(", ", statement.Functions) : null
            }.Where(t => t is not null));
            var kind = statement.Kinds.Count > 0 ? string.Join(", ", statement.Kinds) : "?";
            var change = statement.Change is null ? ""
                : $"\n[{(statement.ChangesEveryRow ? "red" : "yellow")}]! {Markup.Escape(statement.Change)}, asks first[/]";
            grid.AddRow(new Markup($"[dim]{i + 1}[/]"), new Text(kind), new Markup(Markup.Escape(touches) + change));
        }

        cli.Console.Write(grid);
        return 0;
    }

    private static string? Uses(StatementCheck statement, string use, string verb)
    {
        var tables = statement.Tables.Where(t => t.Use == use).Select(t => t.Name).ToList();
        return tables.Count > 0 ? $"{verb} {string.Join(", ", tables)}" : null;
    }
}
