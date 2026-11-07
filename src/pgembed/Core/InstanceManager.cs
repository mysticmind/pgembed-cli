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
        IReadOnlyList<string> extensions, bool keepRunningAfterExit, bool refresh, CancellationToken ct, string? locale = null,
        bool requirePassword = false)
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

        var isNew = record is null && dataMajor is null;

        // only you can connect through the Unix socket (Postgres makes it 0777 by default, so any local user could
        // connect as the superuser without a password); kept out of the saved settings, applied on every start
        var effective = new Dictionary<string, string>(parameters);
        if (!OperatingSystem.IsWindows()) effective.TryAdd("unix_socket_permissions", "0700");

        var options = new PgServerOptions
        {
            PgVersion = version,
            InstanceName = name,
            Port = chosenPort,
            ServerParams = effective,
            Extensions = extensionList.Select(ParseExtension).ToList(),
            KeepRunningAfterExit = keepRunningAfterExit,
            MavenRepo = mavenRepo,
            HttpClient = http,
            Logger = logger
        };

        // the locale only matters to initdb, on the first start; set only when chosen, to keep the library's default
        if (isNew && (locale ?? AvailableLocale()) is { } chosen) options = options with { Locale = chosen };
        var server = new PgServer(options);

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

        // once set, a password stays: the instance's pg_hba.conf asks for it
        var password = record?.Password;
        if (requirePassword && password is null) password = await RequirePasswordAsync(name, server.PgPort, ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var saved = new InstanceRecord(name, version, server.PgPort, parameters, extensionList,
            record?.CreatedAt ?? now, server.IsAttached ? record?.LastStartedAt ?? now : now, password);
        registry.Save(saved);

        return new StartResult(server, server.IsAttached, saved);
    }

    /// <summary>
    /// The port of a running instance from Postgres's own <c>postmaster.pid</c>, when its process is alive and is
    /// Postgres; null otherwise (then <see cref="StatusAsync"/> gives the full answer). Reading the file directly
    /// saves the library's general status check on every SQL command.
    /// </summary>
    public int? RunningPort(string name)
    {
        try
        {
            var file = Path.Combine(workspace.InstanceDir(name), "data", "postmaster.pid");
            if (!File.Exists(file)) return null;

            // line 1: the postmaster's process id; line 4: its port
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var pidLine = reader.ReadLine();
            reader.ReadLine();
            reader.ReadLine();
            var portLine = reader.ReadLine();
            if (!int.TryParse(pidLine, out var pid) || !int.TryParse(portLine, out var port) || port <= 0) return null;

            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Contains("postgres", StringComparison.OrdinalIgnoreCase) ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null; // no such process, or the file is being rewritten: ask the library
        }
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
                                       (registry.DataMajor(name) is not null || registry.Get(name) is not null); // the data first: a cheap read

    public string LogFile(string name) => Path.Combine(workspace.InstanceDir(name), "postgres.log");

    public static string ConnectionString(int port, string database = DefaultDatabase, string? schema = null, string? password = null) =>
        Connections.Format("npgsql", port, database, schema, password: password);

    /// <summary>
    /// For shared machines: a generated password for the superuser, and TCP connections (127.0.0.1, ::1) that need
    /// it, with SCRAM. The Unix socket stays passwordless, but only you can open it (it is 0700). Returns the
    /// password, which the caller keeps in the instance's private settings.
    /// </summary>
    private async Task<string> RequirePasswordAsync(string name, int port, CancellationToken ct)
    {
        // 32 characters from a URL-safe alphabet: nothing to quote or escape in any connection string format
        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_');

        await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString(port) + ";Pooling=false"))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var alter = new Npgsql.NpgsqlCommand($"ALTER ROLE {DefaultUser} PASSWORD '{password}'", connection);
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var hba = Path.Combine(workspace.InstanceDir(name), "data", "pg_hba.conf");
            var lines = (await File.ReadAllLinesAsync(hba, ct).ConfigureAwait(false))
                .Select(line => line.TrimStart().StartsWith("host", StringComparison.Ordinal) && line.TrimEnd().EndsWith("trust", StringComparison.Ordinal)
                    ? line.TrimEnd()[..^"trust".Length] + "scram-sha-256"
                    : line);
            await File.WriteAllLinesAsync(hba, lines, ct).ConfigureAwait(false);

            await using var reload = new Npgsql.NpgsqlCommand("SELECT pg_reload_conf()", connection);
            await reload.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return password;
    }

    /// <summary>
    /// A locale initdb can use. The library's default, en_US.UTF-8, is missing from many Linux images (the .NET SDK
    /// images among them), where initdb then fails; there C.UTF-8, else C, is used instead. Null keeps the default.
    /// </summary>
    internal static string? AvailableLocale()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("locale", "-a")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return ChooseLocale(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // no locale command (some minimal images): keep the default
        }
    }

    /// <summary>From the names <c>locale -a</c> lists: null when en_US.UTF-8 exists, else C.UTF-8 when it exists, else C.</summary>
    internal static string? ChooseLocale(IEnumerable<string> available)
    {
        var names = available.Select(n => n.ToLowerInvariant().Replace("-", "")).ToHashSet();
        if (names.Count == 0 || names.Contains("en_us.utf8")) return null;
        return names.Contains("c.utf8") ? "C.UTF-8" : "C";
    }

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
    // An extension is native code Postgres loads, so where it comes from matters: https, this machine, or a
    // checksum that proves it. Over plain http, without #sha256=, anyone on the way could replace it.
    internal static PgExtensionConfig ParseExtension(string value)
    {
        var hash = value.IndexOf("#sha256=", StringComparison.OrdinalIgnoreCase);
        var url = hash < 0 ? value : value[..hash];
        if (hash < 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "http" && !uri.IsLoopback)
        {
            throw new PostgresEmbedException(
                $"The extension {url} would be downloaded over plain http, where it could be replaced on the way. " +
                "Use https, or add its checksum: --extension <url>#sha256=<hex>.");
        }

        return hash < 0
            ? new PgExtensionConfig(url)
            : new PgExtensionConfig(url) { Sha256 = value[(hash + "#sha256=".Length)..] };
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
