using System.ComponentModel;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PgEmbed.Cli.Commands;

internal sealed class StartCommand(CliServices services) : AsyncCommand<StartCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Instance name; with several versions, the prefix: <name>-<major>")]
        public string Name { get; set; } = "";

        [CommandOption("-v|--version <VERSION>")]
        [Description("Version for a new instance (17, 17.6, 17.6.0, latest), or several: 16,17,18. Kept for later starts")]
        public string? Version { get; set; }

        [CommandOption("-p|--port <PORT>")]
        [Description("Port (default: the instance's remembered port, else 5432 if free, else a free port)")]
        public int? Port { get; set; }

        [CommandOption("--param <KEY=VALUE>")]
        [Description("Postgres setting, for example --param max_connections=300. Repeatable")]
        public string[] Params { get; set; } = [];

        [CommandOption("--extension <URL>")]
        [Description("Extension zip to install; append #sha256=<hex> to verify it. Repeatable")]
        public string[] Extensions { get; set; } = [];

        [CommandOption("--locale <LOCALE>")]
        [Description("Locale for a new instance's data (default: en_US.UTF-8; on Linux without it, C.UTF-8)")]
        public string? Locale { get; set; }

        [CommandOption("--foreground")]
        [Description("Stay attached and stream the log; Ctrl+C stops the server")]
        public bool Foreground { get; set; }

        [CommandOption("--refresh")]
        [Description("Check for new versions now")]
        public bool Refresh { get; set; }

        public override ValidationResult Validate()
        {
            var versions = Version?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (versions.Length > 1 && (Foreground || Port is not null))
                return ValidationResult.Error("--foreground and --port need a single version");
            if (Params.Any(p => !p.Contains('=')))
                return ValidationResult.Error("--param takes KEY=VALUE");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var parameters = settings.Params.Select(p => p.Split('=', 2)).ToDictionary(kv => kv[0].Trim(), kv => kv[1]);
        var versions = settings.Version?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        // several versions: one instance each, named <name>-<major>
        var targets = versions.Length > 1
            ? versions.Select(v => ($"{settings.Name}-{VersionOrder.Major(v)}", (string?)v)).ToList()
            : [(settings.Name, versions.FirstOrDefault())];

        var results = new List<object>();
        foreach (var (name, version) in targets)
        {
            StartResult result = null!;
            await InstallCommand.Spin(cli, $"Starting {name}", async () => result = await cli.Instances.StartAsync(
                name, version, settings.Port, parameters, settings.Extensions, keepRunningAfterExit: !settings.Foreground,
                settings.Refresh, ct, settings.Locale));

            if (settings.Foreground && result.Attached)
            {
                throw new PostgresEmbedException(
                    $"'{name}' is already running in the background on port {result.Server.PgPort}. " +
                    $"Stop it first, or follow it with: pgembed logs {name} --follow");
            }

            var port = result.Server.PgPort;
            results.Add(new { name, version = result.Record.Version, port, attached = result.Attached, connectionString = InstanceManager.ConnectionString(port) });

            if (!settings.Json)
            {
                cli.Console.MarkupLineInterpolated(result.Attached
                    ? (FormattableString)$"[green]●[/] [bold]{name}[/] is already running: PostgreSQL {result.Record.Version} on port {port}"
                    : $"[green]●[/] Started [bold]{name}[/]: PostgreSQL {result.Record.Version} on port {port}");
                cli.Console.MarkupLineInterpolated($"  [dim]{InstanceManager.ConnectionString(port)}[/]");
            }

            if (settings.Foreground)
            {
                if (settings.Json) cli.WriteJson(results[0]);
                return await FollowUntilStoppedAsync(cli, name, result.Server, ct);
            }
        }

        if (settings.Json) cli.WriteJson(results.Count == 1 ? results[0] : results);
        return 0;
    }

    // --foreground: stream the log until Ctrl+C or SIGTERM, then stop the server cleanly
    private static async Task<int> FollowUntilStoppedAsync(CliContext cli, string name, PgServer server, CancellationToken ct)
    {
        cli.Console.MarkupLine("[dim]Streaming the server log; press Ctrl+C to stop the server.[/]");
        try
        {
            await LogsCommand.FollowAsync(cli.Instances.LogFile(name), cli.Services.Out, fromStart: true, ct);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or SIGTERM
        }

        await server.StopAsync(CancellationToken.None);
        cli.Console.MarkupLineInterpolated($"Stopped [bold]{name}[/]");
        return 0;
    }
}

/// <summary>Settings for commands that act on one or more instances by name or pattern.</summary>
internal class InstancesSettings : GlobalSettings
{
    [CommandArgument(0, "[NAMES]")]
    [Description("Instance names; * and ? match several, for example ci-*")]
    public string[] Names { get; set; } = [];

    [CommandOption("--all")]
    [Description("Every instance")]
    public bool All { get; set; }

    public override ValidationResult Validate() =>
        Names.Length == 0 && !All ? ValidationResult.Error("Give instance names, or --all")
        : Names.Length > 0 && All ? ValidationResult.Error("Give instance names or --all, not both")
        : ValidationResult.Success();

    public IReadOnlyList<string> Resolve(InstanceRegistry registry) =>
        All ? registry.Names() : Names.SelectMany(registry.Match).Distinct().ToList();
}

internal sealed class StopCommand(CliServices services) : AsyncCommand<InstancesSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, InstancesSettings settings, CancellationToken ct) =>
        ForEach(services, settings, "Stopped", "was not running", (cli, name) => cli.Instances.StopAsync(name, ct));

    internal static async Task<int> ForEach(
        CliServices services, InstancesSettings settings, string doneVerb, string notRunning, Func<CliContext, string, Task<bool>> act)
    {
        var cli = new CliContext(settings, services);
        var names = settings.Resolve(cli.Registry);
        var results = new List<object>();
        var exit = 0;

        foreach (var name in names)
        {
            if (!cli.Instances.Exists(name))
            {
                if (!settings.Json) cli.Console.MarkupLineInterpolated($"[red]✗[/] There is no instance named [bold]{name}[/]");
                results.Add(new { name, result = "not found" });
                exit = 1;
                continue;
            }

            var acted = await act(cli, name);
            results.Add(new { name, result = acted ? doneVerb.ToLowerInvariant() : "not running" });
            if (!settings.Json)
            {
                cli.Console.MarkupLineInterpolated(acted
                    ? (FormattableString)$"[green]✓[/] {doneVerb} [bold]{name}[/]"
                    : $"[dim]·[/] [bold]{name}[/] {notRunning}");
            }
        }

        if (names.Count == 0 && !settings.Json) cli.Console.MarkupLine("[dim]No matching instances.[/]");
        if (settings.Json) cli.WriteJson(results);
        return exit;
    }
}

internal sealed class KillCommand(CliServices services) : AsyncCommand<InstancesSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, InstancesSettings settings, CancellationToken ct) =>
        StopCommand.ForEach(services, settings, "Killed", "was not running", (cli, name) => cli.Instances.KillAsync(name, ct));
}

internal sealed class RestartCommand(CliServices services) : AsyncCommand<RestartCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; set; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        if (!cli.Instances.Exists(settings.Name)) throw new PostgresEmbedException($"There is no instance named '{settings.Name}'.");

        await cli.Instances.StopAsync(settings.Name, ct);
        StartResult result = null!;
        await InstallCommand.Spin(cli, $"Restarting {settings.Name}", async () => result = await cli.Instances.StartAsync(
            settings.Name, null, null, new Dictionary<string, string>(), [], keepRunningAfterExit: true, false, ct));

        if (settings.Json) cli.WriteJson(new { name = settings.Name, version = result.Record.Version, port = result.Server.PgPort });
        else cli.Console.MarkupLineInterpolated($"[green]●[/] Restarted [bold]{settings.Name}[/]: PostgreSQL {result.Record.Version} on port {result.Server.PgPort}");
        return 0;
    }
}

internal sealed class StatusCommand(CliServices services) : AsyncCommand<StatusCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[NAMES]")]
        [Description("Instance names or patterns (default: all)")]
        public string[] Names { get; set; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var names = settings.Names.Length == 0 ? cli.Registry.Names() : settings.Names.SelectMany(cli.Registry.Match).Distinct().ToList();
        var views = new List<InstanceView>();
        foreach (var name in names.Where(cli.Instances.Exists)) views.Add(await cli.Instances.ViewAsync(name, ct));

        if (settings.Json)
        {
            cli.WriteJson(views.Select(v => new
            {
                v.Name, v.Version, v.DataMajor, state = v.Status.State, v.Port, processId = v.Status.ProcessId,
                startedAt = v.Status.StartedAt, dataBytes = v.DataBytes
            }));
            return 0;
        }

        if (views.Count == 0)
        {
            cli.Console.MarkupLine("[dim]No instances. Create one with: pgembed start <name> --version 17[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumns("NAME", "VERSION", "PORT", "PID", "STATE", "UPTIME", "DATA");
        foreach (var v in views)
        {
            var running = v.Status.State != PgInstanceState.Stopped;
            table.AddRow(
                Markup.Escape(v.Name),
                Markup.Escape(v.Version ?? (v.DataMajor is null ? "" : v.DataMajor + ".x")),
                v.Port?.ToString() ?? "",
                v.Status.ProcessId?.ToString() ?? "",
                running ? $"[green]{v.Status.State.ToString().ToLowerInvariant()}[/]" : "[dim]stopped[/]",
                running && v.Status.StartedAt is { } at ? Format.Duration(DateTimeOffset.UtcNow - at) : "",
                Format.Bytes(v.DataBytes));
        }

        cli.Console.Write(table);
        return 0;
    }
}

internal sealed class ConnStrCommand(CliServices services) : AsyncCommand<ConnStrCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; set; } = "";

        [CommandOption("--db <DATABASE>")]
        [Description("Database (default: postgres)")]
        public string Database { get; set; } = InstanceManager.DefaultDatabase;

        [CommandOption("--schema <SCHEMA>")]
        [Description("Schema to put first on the search path")]
        public string? Schema { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("Only this format, unlabelled: npgsql, uri, libpq, jdbc or env. Default: all of them")]
        public string? Format { get; set; }

        public override ValidationResult Validate() =>
            Format is null || Connections.FormatNames.Contains(Format)
                ? ValidationResult.Success()
                : ValidationResult.Error($"--format is one of: {string.Join(", ", Connections.FormatNames)}");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        if (!cli.Instances.Exists(settings.Name)) throw new PostgresEmbedException($"There is no instance named '{settings.Name}'.");

        // the port from postmaster.pid when running; otherwise the full status, or the remembered port
        var runningPort = cli.Instances.RunningPort(settings.Name);
        var running = runningPort is not null;
        if (!running)
        {
            var status = await cli.Instances.StatusAsync(settings.Name, ct);
            running = status.State != PgInstanceState.Stopped;
            runningPort = status.Port;
        }

        var port = runningPort ?? cli.Registry.Get(settings.Name)?.Port
                   ?? throw new PostgresEmbedException($"'{settings.Name}' has no known port yet: start it once with pgembed start {settings.Name}");
        string Format(string format) => Connections.Format(format, port, settings.Database, settings.Schema);

        if (settings.Json)
        {
            var formats = Connections.FormatNames.ToDictionary(f => f, Format);
            cli.WriteJson(new { name = settings.Name, port, running, formats });
            return 0;
        }

        if (settings.Format is { } only)
        {
            services.Out.WriteLine(Format(only)); // plain, for scripts: $(pgembed connstr app --format uri)
            return 0;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        foreach (var format in Connections.FormatNames) grid.AddRow(new Markup($"[dim]{format}[/]"), new Text(Format(format)));
        cli.Console.Write(grid);
        if (!running)
        {
            cli.Console.MarkupLineInterpolated($"[dim]{settings.Name} is stopped; start it with: pgembed start {settings.Name}[/]");
        }

        return 0;
    }
}

internal sealed class LogsCommand(CliServices services) : AsyncCommand<LogsCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; set; } = "";

        [CommandOption("-n|--tail <LINES>")]
        [Description("Lines from the end (default: 50)")]
        public int Tail { get; set; } = 50;

        [CommandOption("-f|--follow")]
        [Description("Keep printing new lines until Ctrl+C")]
        public bool Follow { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        if (!cli.Instances.Exists(settings.Name)) throw new PostgresEmbedException($"There is no instance named '{settings.Name}'.");

        var file = cli.Instances.LogFile(settings.Name);
        if (File.Exists(file))
        {
            foreach (var line in ReadShared(file).TakeLast(Math.Max(0, settings.Tail))) services.Out.WriteLine(line);
            await services.Out.FlushAsync(ct);
        }

        if (settings.Follow)
        {
            try
            {
                await FollowAsync(file, services.Out, fromStart: false, ct);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C
            }
        }

        return 0;
    }

    internal static List<string> ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>Prints lines appended to a log (Postgres keeps writing it) until cancelled.</summary>
    internal static async Task FollowAsync(string file, TextWriter output, bool fromStart, CancellationToken ct)
    {
        while (!File.Exists(file)) await Task.Delay(200, ct);

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!fromStart) stream.Seek(0, SeekOrigin.End);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                await output.FlushAsync(ct); // a burst of lines goes out at once
                await Task.Delay(250, ct);
                continue;
            }

            await output.WriteLineAsync(line);
        }
    }
}

internal sealed class RemoveCommand(CliServices services) : AsyncCommand<RemoveCommand.Settings>
{
    internal sealed class Settings : InstancesSettings
    {
        [CommandOption("--force")]
        [Description("Stop a running instance first")]
        public bool Force { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Delete without asking")]
        public bool Yes { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        // deleting an instance deletes its data: confirm first, unless told not to
        var cli = new CliContext(settings, services);
        var names = settings.Resolve(cli.Registry).Where(cli.Instances.Exists).ToList();
        if (names.Count > 0 && !settings.Yes)
        {
            if (!cli.Console.Profile.Capabilities.Interactive || settings.Json)
            {
                throw new PostgresEmbedException($"Removing {string.Join(", ", names)} deletes its data. Pass --yes to remove without asking.");
            }

            var question = names.Count == 1
                ? $"Delete instance {names[0]} and all its data?"
                : $"Delete {names.Count} instances ({string.Join(", ", names)}) and all their data?";
            if (!cli.Console.Confirm(question, defaultValue: false))
            {
                cli.Console.MarkupLine("Nothing was removed.");
                return 1;
            }
        }

        return await StopCommand.ForEach(services, settings, "Removed", "was not removed", async (c, name) =>
        {
            await c.Instances.RemoveAsync(name, settings.Force, ct);
            return true;
        });
    }
}
