using System.ComponentModel;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PgEmbed.Cli.Commands;

internal sealed class VersionsCommand(CliServices services) : AsyncCommand<VersionsCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--major <MAJOR>")]
        [Description("Every published version of one major, for example 17")]
        public string? Major { get; set; }

        [CommandOption("--all")]
        [Description("Every published version")]
        public bool All { get; set; }

        [CommandOption("--platform <RID>")]
        [Description("Another platform: win-x64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-x64, osx-arm64")]
        public string? Platform { get; set; }

        [CommandOption("--refresh")]
        [Description("Check for new versions now instead of using the cached list")]
        public bool Refresh { get; set; }

        public override ValidationResult Validate() =>
            Platform is not null && !PgPlatform.TryParse(Platform, out _)
                ? ValidationResult.Error($"Unknown platform '{Platform}'. Use one of: {string.Join(", ", PgPlatform.SupportedRuntimeIdentifiers)}")
                : ValidationResult.Success();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var platform = settings.Platform is null ? cli.Platform : (PgPlatform.TryParse(settings.Platform, out var p) ? p : cli.Platform);
        var list = await cli.Catalog.GetAsync(platform, settings.Refresh, ct);
        if (list.FromStaleCache) cli.Warn($"could not reach {cli.MavenRepo}; showing versions cached on {list.FetchedAt:yyyy-MM-dd}");

        var newestFirst = list.Versions.OrderByDescending(v => v, VersionOrder.Comparer).ToList();

        if (settings.Major is not null || settings.All)
        {
            var selected = settings.Major is null ? newestFirst : newestFirst.Where(v => VersionOrder.Major(v) == settings.Major).ToList();
            if (settings.Json) cli.WriteJson(selected);
            else if (selected.Count == 0) cli.Console.MarkupLineInterpolated($"No versions of Postgres {settings.Major} are published for {platform.RuntimeIdentifier}.");
            else foreach (var v in selected) cli.Console.WriteLine(v);
            return 0;
        }

        var majors = newestFirst.GroupBy(VersionOrder.Major)
            .Select(g => new { Major = g.Key, Newest = g.First(), Versions = g.ToList() })
            .ToList();

        if (settings.Json)
        {
            cli.WriteJson(majors.Select(m => new { m.Major, m.Newest, Count = m.Versions.Count }));
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumns("MAJOR", "NEWEST", "PUBLISHED");
        foreach (var m in majors) table.AddRow(m.Major, m.Newest, m.Versions.Count == 1 ? "1 version" : $"{m.Versions.Count} versions");
        cli.Console.Write(table);
        cli.Console.MarkupLineInterpolated($"[dim]{platform.RuntimeIdentifier}; pgembed versions --major <n> lists every version of a major[/]");
        return 0;
    }
}

internal sealed class InstallCommand(CliServices services) : AsyncCommand<InstallCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("Exact (17.6.0), major (17), minor (17.6) or latest")]
        public string Version { get; set; } = "";

        [CommandOption("--extension <URL>")]
        [Description("Extension zip to install with it; append #sha256=<hex> to verify it. Repeatable")]
        public string[] Extensions { get; set; } = [];

        [CommandOption("--refresh")]
        [Description("Check for new versions now")]
        public bool Refresh { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var version = await cli.Catalog.ResolveAsync(settings.Version, cli.Platform, settings.Refresh, ct);

        await using var server = new PgServer(new PgServerOptions
        {
            PgVersion = version,
            BinariesDir = cli.Workspace.BinariesDir,
            Extensions = settings.Extensions.Select(InstanceManager.ParseExtension).ToList(),
            MavenRepo = cli.MavenRepo,
            HttpClient = services.Http,
            Logger = cli.Logger
        });

        await Spin(cli, $"Installing PostgreSQL {version}", () => server.InstallAsync(ct));

        if (settings.Json) cli.WriteJson(new { version, platform = cli.Platform.RuntimeIdentifier, installDir = server.InstallDir });
        else cli.Console.MarkupLineInterpolated($"Installed PostgreSQL [bold]{version}[/] ({cli.Platform.RuntimeIdentifier}) at {server.InstallDir}");
        return 0;
    }

    /// <summary>A spinner on an interactive console; nothing in JSON mode or when output is redirected.</summary>
    internal static async Task Spin(CliContext cli, string message, Func<Task> work)
    {
        if (cli.Settings.Json || !cli.Console.Profile.Capabilities.Interactive)
        {
            await work();
            return;
        }

        await cli.Console.Status().StartAsync(message, async _ => await work());
    }
}

internal sealed class ExtensionsCommand(CliServices services) : AsyncCommand<ExtensionsCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("Exact (17.6.0), major (17), minor (17.6) or latest")]
        public string Version { get; set; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var version = await cli.Catalog.ResolveAsync(settings.Version, cli.Platform, false, ct);

        await using var server = new PgServer(new PgServerOptions
        {
            PgVersion = version,
            BinariesDir = cli.Workspace.BinariesDir,
            MavenRepo = cli.MavenRepo,
            HttpClient = services.Http,
            Logger = cli.Logger
        });

        IReadOnlyList<PgAvailableExtension> extensions = [];
        await InstallCommand.Spin(cli, $"Reading the extensions of PostgreSQL {version}",
            async () => extensions = await server.GetAvailableExtensionsAsync(ct));

        if (settings.Json)
        {
            cli.WriteJson(extensions);
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumns("NAME", "VERSION", "REQUIRES", "DESCRIPTION");
        foreach (var e in extensions)
        {
            table.AddRow(Markup.Escape(e.Name), Markup.Escape(e.DefaultVersion ?? ""),
                Markup.Escape(string.Join(", ", e.Requires)), Markup.Escape(e.Comment ?? ""));
        }

        cli.Console.Write(table);
        cli.Console.MarkupLineInterpolated($"[dim]{extensions.Count} extensions in PostgreSQL {version}[/]");
        return 0;
    }
}
