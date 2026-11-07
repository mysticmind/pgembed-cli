using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MysticMind.PostgresEmbed;

namespace PgEmbed.Cli.Core;

internal sealed record InstanceView(
    string Name, string? Version, string? DataMajor, PgInstanceStatus Status, int? Port, long DataBytes);

internal sealed record StartResult(PgServer Server, bool Attached, InstanceRecord Record);

/// <summary>
/// Starts, stops and inspects named instances through the library, and keeps the registry: the exact
/// version (pinned to the data's major), the port (remembered, so connection strings never change),
/// server parameters and extensions.
/// </summary>
internal sealed class InstanceManager(
    Workspace workspace, InstanceRegistry registry, VersionCatalog catalog, PgPlatformInfo platform,
    HttpClient http, string mavenRepo, ILogger logger)
{
    public const string DefaultUser = "postgres";
    public const string DefaultDatabase = "postgres";
    private const int PreferredPort = 5432;

    public InstanceRegistry Registry => registry;

    public async Task<StartResult> StartAsync(
        string name, string? versionSpec, int? port, IReadOnlyDictionary<string, string> serverParams,
        IReadOnlyList<string> extensions, bool keepRunningAfterExit, bool refresh, CancellationToken ct)
    {
        var record = registry.Get(name);
        var dataMajor = registry.DataMajor(name);

        string version;
        if (versionSpec is not null)
        {
            version = await catalog.ResolveAsync(versionSpec, platform, refresh, ct).ConfigureAwait(false);
            if (dataMajor is not null && VersionOrder.Major(version) != dataMajor)
            {
                throw new PostgresEmbedException(
                    $"Instance '{name}' holds Postgres {dataMajor} data, which Postgres {version} cannot open. " +
                    $"Use --version {dataMajor}, or remove it first with: pgembed rm {name}");
            }
        }
        else if (record is not null)
        {
            version = record.Version;
        }
        else if (dataMajor is not null)
        {
            version = await catalog.ResolveAsync(dataMajor, platform, refresh, ct).ConfigureAwait(false);
        }
        else
        {
            throw new PostgresEmbedException($"Instance '{name}' does not exist yet: give it a version, for example --version 17");
        }

        var parameters = serverParams.Count > 0 ? new Dictionary<string, string>(serverParams) : record?.ServerParams ?? [];
        var extensionList = extensions.Count > 0 ? extensions.ToList() : record?.Extensions ?? [];

        var running = await StatusAsync(name, ct).ConfigureAwait(false);
        var chosenPort = port ?? record?.Port ?? (IsPortFree(PreferredPort) ? PreferredPort : 0);
        if (running.State == PgInstanceState.Stopped && chosenPort != 0 && !IsPortFree(chosenPort))
        {
            throw new PostgresEmbedException(
                $"Port {chosenPort} is in use by another program. Start '{name}' on another port with --port <n>.");
        }

        var server = new PgServer(new PgServerOptions
        {
            PgVersion = version,
            InstanceName = name,
            Port = chosenPort,
            ServerParams = parameters,
            Extensions = extensionList.Select(ParseExtension).ToList(),
            KeepRunningAfterExit = keepRunningAfterExit,
            MavenRepo = mavenRepo,
            HttpClient = http,
            Logger = logger
        });

        var isNew = record is null && dataMajor is null;
        try
        {
            await server.StartAsync(ct).ConfigureAwait(false);
        }
        catch when (isNew)
        {
            // a first start that failed (initdb, port, shared memory) leaves no instance behind
            await server.DisposeAsync().ConfigureAwait(false);
            TryDelete(workspace.InstanceDir(name));
            throw;
        }

        var now = DateTimeOffset.UtcNow;
        var saved = new InstanceRecord(name, version, server.PgPort, parameters, extensionList,
            record?.CreatedAt ?? now, server.IsAttached ? record?.LastStartedAt ?? now : now);
        registry.Save(saved);

        return new StartResult(server, server.IsAttached, saved);
    }

    public async Task<PgInstanceStatus> StatusAsync(string name, CancellationToken ct)
    {
        await using var server = Server(name);
        return await server.GetStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task<InstanceView> ViewAsync(string name, CancellationToken ct)
    {
        var status = await StatusAsync(name, ct).ConfigureAwait(false);
        var record = registry.Get(name);
        return new InstanceView(name, record?.Version, registry.DataMajor(name), status,
            status.Port ?? record?.Port, DirectorySize(Path.Combine(workspace.InstanceDir(name), "data")));
    }

    /// <summary>Clean shutdown, whichever process started the server. False when it was not running.</summary>
    public async Task<bool> StopAsync(string name, CancellationToken ct)
    {
        await using var server = Server(name);
        if ((await server.GetStatusAsync(ct).ConfigureAwait(false)).State == PgInstanceState.Stopped) return false;

        await server.StopAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Force-kill. False when it was not running.</summary>
    public async Task<bool> KillAsync(string name, CancellationToken ct)
    {
        await using var server = Server(name);
        if ((await server.GetStatusAsync(ct).ConfigureAwait(false)).State == PgInstanceState.Stopped) return false;

        await server.KillAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes an instance and its data. A running one needs <paramref name="force"/>, which kills it first.</summary>
    public async Task RemoveAsync(string name, bool force, CancellationToken ct)
    {
        if (!Exists(name)) throw new PostgresEmbedException($"There is no instance named '{name}'.");

        if ((await StatusAsync(name, ct).ConfigureAwait(false)).State != PgInstanceState.Stopped)
        {
            if (!force) throw new PostgresEmbedException($"Instance '{name}' is running. Stop it first, or pass --force to stop and remove it.");

            // a clean stop releases the server's shared memory; a kill would leave it behind until reboot
            try
            {
                await StopAsync(name, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PostgresEmbedException or IOException or TimeoutException)
            {
                await KillAsync(name, ct).ConfigureAwait(false);
            }
        }

        var dir = workspace.InstanceDir(name);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(200, ct).ConfigureAwait(false); // Windows can hold files briefly after a kill
            }
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: the original failure is what matters
        }
    }

    public bool Exists(string name) => Workspace.IsValidName(name) && Directory.Exists(workspace.InstanceDir(name)) &&
                                       (registry.Get(name) is not null || registry.DataMajor(name) is not null);

    public string LogFile(string name) => Path.Combine(workspace.InstanceDir(name), "postgres.log");

    public static string ConnectionString(int port, string database = DefaultDatabase, string? schema = null) =>
        Connections.Format("npgsql", port, database, schema);

    // A PgServer for inspecting or controlling an instance. The version only matters for finding binaries.
    private PgServer Server(string name) => new(new PgServerOptions
    {
        PgVersion = registry.Get(name)?.Version ?? registry.DataMajor(name) ?? PgVersions.Latest,
        InstanceName = name,
        MavenRepo = mavenRepo,
        HttpClient = http,
        Logger = logger
    });

    // "<url>" or "<url>#sha256=<hex>"
    internal static PgExtensionConfig ParseExtension(string value)
    {
        var hash = value.IndexOf("#sha256=", StringComparison.OrdinalIgnoreCase);
        return hash < 0
            ? new PgExtensionConfig(value)
            : new PgExtensionConfig(value[..hash]) { Sha256 = value[(hash + "#sha256=".Length)..] };
    }

    internal static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static long DirectorySize(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f =>
            {
                try { return f.Length; }
                catch (IOException) { return 0L; }
            })
            : 0;
}
