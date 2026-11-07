using System.ComponentModel;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Sql;
using Spectre.Console;

namespace PgEmbed.Cli.Commands;

/// <summary>
/// Formats SQL with the PostgreSQL parser, keeping comments; needs no server. Prints the result, rewrites
/// files with --write, or with --check lists the files that are not formatted and exits 1 (a CI lint).
/// </summary>
internal sealed class FormatCommand(CliServices services) : AsyncCommand<FormatCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[SQL]")]
        [Description("SQL to format; or use -f, or pipe it in")]
        public string? Sql { get; set; }

        [CommandOption("-f|--file <PATH>")]
        [Description("A SQL file to format; repeat for several")]
        public string[] Files { get; set; } = [];

        [CommandOption("--write")]
        [Description("Rewrite the files in place")]
        public bool Write { get; set; }

        [CommandOption("--check")]
        [Description("Only list the files that are not formatted; exit 1 if any")]
        public bool Check { get; set; }

        public override ValidationResult Validate()
        {
            if (Write && Check) return ValidationResult.Error("--write and --check do not go together");
            if ((Write || Check) && Files.Length == 0) return ValidationResult.Error("--write and --check work on files: give them with -f");
            if (Sql is not null && Files.Length > 0) return ValidationResult.Error("give SQL or files, not both");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);

        if (settings.Files.Length == 0)
        {
            var sql = settings.Sql ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync(ct) : null)
                      ?? throw new PostgresEmbedException("Give SQL to format: as an argument, with -f <file>, or piped in.");
            var (formatted, error) = SqlAnalysis.Format(sql);
            if (error is not null) return Fail(cli, sql, null, error);

            if (settings.Json) cli.WriteJson(new FormattedSql(formatted));
            else
            {
                services.Out.Write(formatted);
                services.Out.Flush();
            }

            return 0;
        }

        var results = new List<FormatFileResult>();
        var failed = false;
        var unformatted = 0;
        foreach (var file in settings.Files)
        {
            var sql = await File.ReadAllTextAsync(file, ct);
            var (formatted, error) = SqlAnalysis.Format(sql);
            if (error is not null)
            {
                Fail(cli, sql, file, error);
                results.Add(new FormatFileResult(file, Error: error.Message, Position: error.Position));
                failed = true;
                continue;
            }

            var changed = !string.Equals(Normalize(sql), Normalize(formatted!), StringComparison.Ordinal);
            if (changed) unformatted++;

            if (settings.Check)
            {
                if (changed && !settings.Json) cli.Console.MarkupLineInterpolated($"[yellow]not formatted:[/] {file}");
            }
            else if (settings.Write)
            {
                if (changed) await File.WriteAllTextAsync(file, formatted, ct);
                if (settings.Json) { }
                else if (changed) cli.Console.MarkupLineInterpolated($"formatted {file}");
                else cli.Console.MarkupLineInterpolated($"[dim]unchanged {file}[/]");
            }
            else if (!settings.Json)
            {
                services.Out.Write(formatted);
            }

            results.Add(new FormatFileResult(file, settings.Write || settings.Check ? !changed : formatted));
        }

        services.Out.Flush();
        if (settings.Json) cli.WriteJson(results);
        else if (settings.Check && unformatted == 0 && !failed) cli.Console.MarkupLine("[green]✓[/] All files are formatted.");

        return failed || (settings.Check && unformatted > 0) ? 1 : 0;
    }

    private static int Fail(CliContext cli, string sql, string? file, SyntaxError error)
    {
        if (cli.Settings.Json && file is null)
        {
            cli.WriteJson(new SyntaxErrorOutput(error.Message, error.Position));
            return 1;
        }

        if (file is not null) cli.Console.MarkupLineInterpolated($"[bold]{file}[/]");
        Renderers.ReportError(cli.Console, sql, error.Message, error.Position);
        return 1;
    }

    // line endings and trailing space are not formatting
    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n").TrimEnd();
}
