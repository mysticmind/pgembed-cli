using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PgEmbed.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
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

        var registrar = new TypeRegistrar();
        Register(registrar, services);

        var app = new CommandApp(registrar);
        app.Configure(config => Configure(config, services));
        try
        {
            return await app.RunAsync(args, cts.Token);
        }
        finally
        {
            await stdout.FlushAsync(CancellationToken.None);
        }
    }

    /// <summary>Services and the --cache-dir interceptor, shared by the real app and the tests.</summary>
    internal static void Register(ITypeRegistrar registrar, CliServices services)
    {
        registrar.RegisterInstance(typeof(CliServices), services);
        registrar.RegisterInstance(typeof(ICommandInterceptor), new CacheDirInterceptor());
    }

    internal static void Configure(IConfigurator config, CliServices services)
    {
        config.SetApplicationName("pgembed");
        config.SetApplicationVersion(typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");
        config.Settings.CancellationExitCode = 130;
        config.Settings.ExceptionHandler = (ex, _) => HandleError(ex, services);

        config.AddCommand<VersionsCommand>("versions").WithDescription("List the Postgres versions published for this platform")
            .WithExample("versions").WithExample("versions", "--major", "17");
        config.AddCommand<InstallCommand>("install").WithDescription("Download and extract a Postgres version into the cache")
            .WithExample("install", "17");
        config.AddCommand<ExtensionsCommand>("extensions").WithDescription("List the extensions CREATE EXTENSION can use in a version")
            .WithExample("extensions", "17");

        config.AddCommand<StartCommand>("start").WithDescription("Start a named instance in the background (created on first start)")
            .WithExample("start", "app", "--version", "17").WithExample("start", "ci", "--version", "16,17,18");
        config.AddCommand<StopCommand>("stop").WithDescription("Stop instances cleanly").WithExample("stop", "app").WithExample("stop", "--all");
        config.AddCommand<KillCommand>("kill").WithDescription("Force-kill instances (last resort)").WithExample("kill", "app");
        config.AddCommand<RestartCommand>("restart").WithDescription("Stop and start an instance with its saved settings");
        config.AddCommand<StatusCommand>("status").WithAlias("list").WithDescription("Show instances: version, port, process, state, uptime, data size");
        config.AddCommand<ConnStrCommand>("connstr").WithDescription("Print an instance's connection string")
            .WithExample("connstr", "app", "--format", "uri");
        config.AddCommand<LogsCommand>("logs").WithDescription("Show an instance's server log").WithExample("logs", "app", "--follow");
        config.AddCommand<RemoveCommand>("rm").WithDescription("Delete instances and their data").WithExample("rm", "demo");

        config.AddCommand<SqlCommand>("sql").WithDescription("Run SQL against an instance and show the results")
            .WithExample("sql", "app", "SELECT version()").WithExample("sql", "app", "-f", "seed.sql");

        config.AddCommand<FormatCommand>("format").WithDescription("Format SQL, keeping comments; needs no server")
            .WithExample("format", "select * from t where id=1").WithExample("format", "-f", "schema.sql", "--write")
            .WithExample("format", "-f", "a.sql", "-f", "b.sql", "--check");

        config.AddCommand<SchemaCommand>("schema").WithDescription("List tables, or describe one: columns, keys, indexes, constraints")
            .WithExample("schema", "app").WithExample("schema", "app", "books").WithExample("schema", "app", "orders", "--schema", "sales", "--json");

        config.AddCommand<McpCommand>("mcp").WithDescription("Run an MCP server over standard input and output, for AI assistants")
            .WithExample("mcp").WithExample("mcp", "--read-only");

        config.AddCommand<DoctorCommand>("doctor").WithDescription("Check that Postgres can run here");
        config.AddBranch<GlobalSettings>("cache", cache =>
        {
            cache.SetDescription("Inspect and clean the download cache");
            cache.AddCommand<CacheListCommand>("list").WithDescription("Cached installs and downloads, with sizes");
            cache.AddCommand<CacheCleanCommand>("clean").WithDescription("Remove cached versions (never ones in use)")
                .WithExample("cache", "clean", "--keep-latest");
        });
    }

    // Exit codes: 1 for failures, 2 for usage errors. Expected failures print one clear line.
    private static int HandleError(Exception ex, CliServices services)
    {
        services.Out.Flush(); // what was written before the error comes first
        switch (ex)
        {
            case CommandAppException:
                services.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
                return 2;
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
