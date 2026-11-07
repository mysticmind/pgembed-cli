using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console;

namespace PgEmbed.Cli.Commands;

internal sealed class DoctorCommand(CliServices services) : AsyncCommand<DoctorCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--dir <PATH>")]
        [Description("Check that Postgres can use this folder (default: the cache folder)")]
        public string? Dir { get; set; }

        [CommandOption("--port <PORT>")]
        [Description("Check that this port is free (default: 5432)")]
        public int Port { get; set; } = 5432;
    }

    internal enum Outcome { Ok, Warning, Failed }

    internal sealed record Check(string Name, Outcome Outcome, string Detail);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var checks = await RunChecksAsync(cli, settings.Dir, settings.Port, ct);

        if (settings.Json)
        {
            cli.WriteJson(checks.Select(c => new { c.Name, outcome = c.Outcome.ToString().ToLowerInvariant(), c.Detail }));
        }
        else
        {
            var table = new Table().Border(TableBorder.None).HideHeaders().AddColumns("", "", "");
            foreach (var c in checks)
            {
                var mark = c.Outcome switch { Outcome.Ok => "[green]✓[/]", Outcome.Warning => "[yellow]![/]", _ => "[red]✗[/]" };
                table.AddRow(mark, Markup.Escape(c.Name), Markup.Escape(c.Detail));
            }

            cli.Console.Write(table);
        }

        return checks.Any(c => c.Outcome == Outcome.Failed) ? 1 : 0;
    }

    /// <summary>Every check, for the command and the MCP doctor tool.</summary>
    internal static async Task<List<Check>> RunChecksAsync(CliContext cli, string? dir, int port, CancellationToken ct) =>
    [
        new("Platform", Outcome.Ok, $"{cli.Platform.RuntimeIdentifier} (binaries: {cli.Platform.ArtifactName})"),
        !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess
            ? new Check("User", Outcome.Failed, "running as root: Postgres refuses to run as root; use a regular account")
            : new Check("User", Outcome.Ok, $"{Environment.UserName}, not root"),
        await MavenAsync(cli, ct),
        CacheFolder(cli),
        PostgresCanUse(dir ?? cli.Workspace.CacheDir),
        InstanceManager.IsPortFree(port)
            ? new Check($"Port {port}", Outcome.Ok, "free")
            : new Check($"Port {port}", Outcome.Warning, "in use; pgembed start picks another port, or pass --port")
    ];

    private static async Task<Check> MavenAsync(CliContext cli, CancellationToken ct)
    {
        try
        {
            var list = await cli.Catalog.GetAsync(cli.Platform, refresh: true, ct);
            return list.FromStaleCache
                ? new Check("Maven repository", Outcome.Failed, $"{cli.MavenRepo} unreachable; using versions cached on {list.FetchedAt:yyyy-MM-dd}")
                : new Check("Maven repository", Outcome.Ok, $"reachable; newest is {list.Versions.OrderBy(v => v, VersionOrder.Comparer).LastOrDefault()}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PostgresEmbedException)
        {
            return new Check("Maven repository", Outcome.Failed, $"{cli.MavenRepo}: {ex.Message}");
        }
    }

    private static Check CacheFolder(CliContext cli)
    {
        try
        {
            Directory.CreateDirectory(cli.Workspace.CacheDir);
            var probe = Path.Combine(cli.Workspace.CacheDir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return new Check("Cache folder", Outcome.Ok, $"{cli.Workspace.CacheDir}, writable, {Format.Bytes(InstanceManager.DirectorySize(cli.Workspace.CacheDir))}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Check("Cache folder", Outcome.Failed, $"{cli.Workspace.CacheDir}: {ex.Message}");
        }
    }

    // Postgres on Windows cannot use non-ASCII paths; the library maps them to the 8.3 short name, which only
    // exists where the drive creates short names (issue #29 of MysticMind.PostgresEmbed).
    private static Check PostgresCanUse(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!OperatingSystem.IsWindows() || full.All(char.IsAscii)) return new Check("Folder for Postgres", Outcome.Ok, $"{full}: usable");

        Directory.CreateDirectory(full);
        var buffer = new char[32767];
        var length = GetShortPathNameW(full, buffer, (uint)buffer.Length);
        var shortPath = length > 0 && length <= buffer.Length ? new string(buffer, 0, (int)length) : null;

        return shortPath is not null && shortPath.All(char.IsAscii)
            ? new Check("Folder for Postgres", Outcome.Ok, $"{full}: usable through its short name")
            : new Check("Folder for Postgres", Outcome.Failed,
                $"{full} has non-ASCII characters and its drive has no 8.3 short names; use an ASCII-only folder (--cache-dir)");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);
}

internal sealed record CacheEntry(string Path, string Kind, string Version, string Artifact, long Bytes, bool InUse);

internal static class CacheScanner
{
    private const string Artifact = @"(?<artifact>(windows|linux|darwin)-(amd64|arm64v8|i386|arm32v6|arm32v7|ppc64le)(-alpine(-lite)?)?)";
    private const string Version = @"(?<version>\d+(\.\d+)*(-\d+)?)";
    private static readonly Regex Install = new($"^postgres-{Version}-{Artifact}(-ext-[0-9a-f]+)?$");
    private static readonly Regex Download = new($"^embedded-postgres-binaries-{Artifact}-{Version}\\.(jar|txz)$");

    /// <summary>Installs and downloads in the cache. An install of a version a running instance uses is in use.</summary>
    public static async Task<List<CacheEntry>> ScanAsync(CliContext cli, CancellationToken ct)
    {
        var running = new HashSet<string>();
        foreach (var name in cli.Registry.Names())
        {
            var view = await cli.Instances.ViewAsync(name, ct);
            if (view.Status.State != PgInstanceState.Stopped && view.Version is { } v) running.Add(v);
        }

        var entries = new List<CacheEntry>();
        if (!Directory.Exists(cli.Workspace.BinariesDir)) return entries;

        foreach (var path in Directory.EnumerateFileSystemEntries(cli.Workspace.BinariesDir))
        {
            var name = System.IO.Path.GetFileName(path);
            if (Install.Match(name) is { Success: true } i && Directory.Exists(path))
            {
                entries.Add(new CacheEntry(path, "install", i.Groups["version"].Value, i.Groups["artifact"].Value,
                    InstanceManager.DirectorySize(path), running.Contains(i.Groups["version"].Value)));
            }
            else if (Download.Match(name) is { Success: true } d)
            {
                // the .jar is what Maven serves; the .txz is the archive extracted from it
                var kind = name.EndsWith(".jar", StringComparison.Ordinal) ? "download (.jar)" : "archive (.txz)";
                entries.Add(new CacheEntry(path, kind, d.Groups["version"].Value, d.Groups["artifact"].Value,
                    new FileInfo(path).Length, false));
            }
        }

        return entries.OrderBy(e => e.Artifact).ThenByDescending(e => e.Version, VersionOrder.Comparer).ThenBy(e => e.Kind).ToList();
    }
}

internal sealed class CacheListCommand(CliServices services) : AsyncCommand<GlobalSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var entries = await CacheScanner.ScanAsync(cli, ct);

        if (settings.Json)
        {
            cli.WriteJson(new { cacheDir = cli.Workspace.CacheDir, entries });
            return 0;
        }

        if (entries.Count == 0)
        {
            cli.Console.MarkupLineInterpolated($"[dim]Nothing cached in {cli.Workspace.BinariesDir}. Install a version with: pgembed install 17[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumns("VERSION", "PLATFORM", "KIND", "SIZE", "IN USE");
        foreach (var e in entries) table.AddRow(e.Version, e.Artifact, e.Kind, Format.Bytes(e.Bytes), e.InUse ? "[green]yes[/]" : "");
        cli.Console.Write(table);
        cli.Console.MarkupLineInterpolated($"[dim]{cli.Workspace.BinariesDir}: {Format.Bytes(entries.Sum(e => e.Bytes))}[/]");
        return 0;
    }
}

internal sealed class CacheCleanCommand(CliServices services) : AsyncCommand<CacheCleanCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--keep-latest")]
        [Description("Keep only the newest version of each major")]
        public bool KeepLatest { get; set; }

        [CommandOption("--version <SPEC>")]
        [Description("Remove one version or major, for example 15 or 16.4.0")]
        public string? Version { get; set; }

        [CommandOption("--all")]
        [Description("Remove everything that is not in use")]
        public bool All { get; set; }

        [CommandOption("--dry-run")]
        [Description("Only show what would be removed")]
        public bool DryRun { get; set; }

        public override ValidationResult Validate() =>
            new[] { KeepLatest, Version is not null, All }.Count(x => x) == 1
                ? ValidationResult.Success()
                : ValidationResult.Error("Choose one of --keep-latest, --version <spec> or --all");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cli = new CliContext(settings, services);
        var entries = await CacheScanner.ScanAsync(cli, ct);

        IEnumerable<CacheEntry> selected = settings switch
        {
            { All: true } => entries,
            { Version: { } spec } => entries.Where(e => PgVersions.Match(spec, [e.Version]) is not null),
            _ => entries.GroupBy(e => (e.Artifact, Major: VersionOrder.Major(e.Version)))
                .SelectMany(g =>
                {
                    var newest = g.Select(e => e.Version).OrderBy(v => v, VersionOrder.Comparer).Last();
                    return g.Where(e => e.Version != newest);
                })
        };

        var targets = selected.Where(e => !e.InUse).ToList();
        var skipped = selected.Count(e => e.InUse);

        foreach (var entry in targets.Where(_ => !settings.DryRun))
        {
            if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, recursive: true);
            else File.Delete(entry.Path);
            var lockFile = entry.Path + ".lock";
            if (File.Exists(lockFile)) File.Delete(lockFile);
        }

        var freed = targets.Sum(e => e.Bytes);
        if (settings.Json)
        {
            cli.WriteJson(new { dryRun = settings.DryRun, removed = targets, freedBytes = freed, skippedInUse = skipped });
            return 0;
        }

        if (targets.Count == 0)
        {
            cli.Console.MarkupLineInterpolated($"Nothing to remove{(skipped > 0 ? $"; {skipped} in use by running instances" : "")}");
            return 0;
        }

        foreach (var e in targets) cli.Console.MarkupLineInterpolated($"{(settings.DryRun ? "would remove" : "removed")} {e.Kind} {e.Version} ({e.Artifact}, {Format.Bytes(e.Bytes)})");
        cli.Console.MarkupLineInterpolated($"{(settings.DryRun ? "Would free" : "Freed")} {Format.Bytes(freed)}{(skipped > 0 ? $"; kept {skipped} in use by running instances" : "")}");
        return 0;
    }
}
