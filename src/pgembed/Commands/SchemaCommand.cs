using System.ComponentModel;
using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Sql;
using Spectre.Console;

namespace PgEmbed.Cli.Commands;

/// <summary>
/// Tables and their structure. The text is what \d shows; --json gives one structured document per
/// table, for scripts and tools.
/// </summary>
internal sealed class SchemaCommand(CliServices services) : AsyncCommand<SchemaCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [Description("An instance name, or a connection string: Npgsql, postgresql:// URI, libpq key=value or JDBC")]
        public string Target { get; set; } = "";

        [CommandArgument(1, "[TABLE]")]
        [Description("A table or view to describe; * and ? describe every match. Without it, lists the tables and views")]
        public string? Table { get; set; }

        [CommandOption("--db <DATABASE>")]
        [Description("Database (default: postgres for an instance, else the connection string's)")]
        public string? Database { get; set; }

        [CommandOption("--schema <SCHEMA>")]
        [Description("Only this schema; unqualified table names are looked up in it")]
        public string? Schema { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("table (the default in a terminal), aligned (the default when redirected), csv or markdown; --json for structured output")]
        public string? Format { get; set; }

        public override ValidationResult Validate() =>
            Format is null or "table" or "aligned" or "csv" or "markdown" ? ValidationResult.Success()
            : ValidationResult.Error("--format is table, aligned, csv or markdown (use --json for JSON)");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var (target, _) = await SqlCommand.ResolveTargetAsync(cli, settings.Target, settings.Database, settings.Schema, ct);
        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(ct);

        var output = new SqlOutput { Format = settings.Format ?? (services.OutputRedirected ? "aligned" : "table"), MaxRows = int.MaxValue };
        var runner = new SqlRunner(cli, output);
        string[] kinds = [.. SchemaReader.Tables, .. SchemaReader.Views, "f"];

        if (settings.Table is null)
        {
            var relations = await SchemaReader.ListAsync(connection, kinds, null, settings.Schema, ct);
            if (settings.Json) cli.WriteJson(relations);
            else if (relations.Count == 0) cli.Console.MarkupLine("[dim]No tables or views.[/]");
            else MetaCommands.ShowRelations(runner, relations);
            return 0;
        }

        // a pattern describes every match; a plain name, the one table Postgres resolves it to
        var wildcard = settings.Table.IndexOfAny(['*', '?']) >= 0;
        var names = wildcard
            ? (await SchemaReader.ListAsync(connection, kinds, settings.Table, settings.Schema, ct)).Select(r => Quote(r.Schema) + "." + Quote(r.Name)).ToList()
            : [settings.Table];

        var tables = new List<TableSchema>();
        foreach (var name in names)
        {
            tables.Add(await SchemaReader.DescribeAsync(connection, name, ct)
                       ?? throw new PostgresEmbedException($"There is no table or view named '{name}'."));
        }

        if (settings.Json)
        {
            if (wildcard) cli.WriteJson(tables);
            else cli.WriteJson(tables[0]);
            return 0;
        }

        if (tables.Count == 0) cli.Console.MarkupLineInterpolated($"[dim]No tables or views match {settings.Table}.[/]");
        foreach (var table in tables)
        {
            MetaCommands.ShowTable(runner, table);
            if (output.Format == "table") cli.Console.WriteLine();
        }

        return 0;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
