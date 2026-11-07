using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console;

namespace PgEmbed.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        StartupTrace.Mark("main");
        // Ctrl+C and SIGTERM cancel the running command (for start --foreground: stop the server cleanly)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (Interrupts.Handler?.Invoke() == true) return;
            cts.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Console.Out flushes on every write; results go through a buffer that renderers flush per statement
        await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16) { AutoFlush = false };
        var services = new CliServices(AnsiConsole.Console, stdout, Console.Error, http, Console.IsOutputRedirected);

        var app = Build(services);
        StartupTrace.Mark("configured");
        try
        {
            return await app.RunAsync(args, cts.Token);
        }
        finally
        {
            await stdout.FlushAsync(CancellationToken.None);
            StartupTrace.Mark("exit");
        }
    }

    /// <summary>The command line, shared by the real app and the tests.</summary>
    internal static CliApp Build(CliServices services)
    {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var app = new CliApp("Run PostgreSQL locally: named instances of any version, SQL, schema, and an MCP server.",
            version, services, ex => HandleError(ex, services));

        app.Add<VersionsCommand, VersionsCommand.Settings>("versions", "List the Postgres versions published for this platform",
            () => new VersionsCommand(services), [["versions"], ["versions", "--major", "17"]]);
        app.Add<InstallCommand, InstallCommand.Settings>("install", "Download and extract a Postgres version into the cache",
            () => new InstallCommand(services), [["install", "17"]]);
        app.Add<ExtensionsCommand, ExtensionsCommand.Settings>("extensions", "List the extensions CREATE EXTENSION can use in a version",
            () => new ExtensionsCommand(services), [["extensions", "17"]]);

        app.Add<StartCommand, StartCommand.Settings>("start", "Start a named instance in the background (created on first start)",
            () => new StartCommand(services), [["start", "app", "--version", "17"], ["start", "ci", "--version", "16,17,18"]]);
        app.Add<StopCommand, InstancesSettings>("stop", "Stop instances cleanly",
            () => new StopCommand(services), [["stop", "app"], ["stop", "--all"]]);
        app.Add<KillCommand, InstancesSettings>("kill", "Force-kill instances (last resort)",
            () => new KillCommand(services), [["kill", "app"]]);
        app.Add<RestartCommand, RestartCommand.Settings>("restart", "Stop and start an instance with its saved settings",
            () => new RestartCommand(services));
        app.Add<StatusCommand, StatusCommand.Settings>("status", "Show instances: version, port, process, state, uptime, data size",
            () => new StatusCommand(services), alias: "list");
        app.Add<ConnStrCommand, ConnStrCommand.Settings>("connstr", "Print an instance's connection string",
            () => new ConnStrCommand(services), [["connstr", "app", "--format", "uri"]]);
        app.Add<LogsCommand, LogsCommand.Settings>("logs", "Show an instance's server log",
            () => new LogsCommand(services), [["logs", "app", "--follow"]]);
        app.Add<RemoveCommand, RemoveCommand.Settings>("rm", "Delete instances and their data",
            () => new RemoveCommand(services), [["rm", "demo"]]);

        app.Add<SqlCommand, SqlCommand.Settings>("sql", "Run SQL against an instance and show the results",
            () => new SqlCommand(services), [["sql", "app", "SELECT version()"], ["sql", "app", "-f", "seed.sql"], ["sql", "--check", "-f", "migrate.sql"]]);
        app.Add<FormatCommand, FormatCommand.Settings>("format", "Format SQL, keeping comments; needs no server",
            () => new FormatCommand(services),
            [["format", "select * from t where id=1"], ["format", "-f", "schema.sql", "--write"], ["format", "-f", "a.sql", "-f", "b.sql", "--check"]]);
        app.Add<SchemaCommand, SchemaCommand.Settings>("schema", "List tables, or describe one: columns, keys, indexes, constraints",
            () => new SchemaCommand(services), [["schema", "app"], ["schema", "app", "books"], ["schema", "app", "orders", "--schema", "sales", "--json"]]);

        app.Add<McpCommand, McpCommand.Settings>("mcp", "Run an MCP server over standard input and output, for AI assistants",
            () => new McpCommand(services), [["mcp"], ["mcp", "--read-only"]]);

        app.Add<DoctorCommand, DoctorCommand.Settings>("doctor", "Check that Postgres can run here", () => new DoctorCommand(services));
        var cache = app.Branch("cache", "Inspect and clean the download cache");
        app.Add<CacheListCommand, GlobalSettings>("list", "Cached installs and downloads, with sizes",
            () => new CacheListCommand(services), parent: cache);
        app.Add<CacheCleanCommand, CacheCleanCommand.Settings>("clean", "Remove cached versions (never ones in use)",
            () => new CacheCleanCommand(services), [["cache", "clean", "--keep-latest"]], parent: cache);

        return app;
    }

    // Exit codes: 1 for failures, 2 for usage errors. Expected failures print one clear line.
    private static int HandleError(Exception ex, CliServices services)
    {
        services.Out.Flush(); // what was written before the error comes first
        switch (ex)
        {
            case PostgresEmbedException or Npgsql.NpgsqlException or IOException or HttpRequestException:
                services.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
                if (ex.InnerException is { } inner) services.Console.MarkupLineInterpolated($"[dim]{inner.Message}[/]");
                return 1;
            default:
                services.Console.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
        }
    }
}
