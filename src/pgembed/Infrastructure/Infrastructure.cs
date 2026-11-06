using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PgEmbed.Cli.Infrastructure;

/// <summary>
/// What commands write to and talk through; tests swap in a test console and a fake HTTP handler.
/// OutputRedirected is true when standard output goes to a file or pipe rather than a terminal.
/// </summary>
internal sealed record CliServices(IAnsiConsole Console, TextWriter Out, TextWriter Error, HttpClient Http, bool OutputRedirected = false);

/// <summary>Options every command accepts.</summary>
internal class GlobalSettings : CommandSettings
{
    [CommandOption("--cache-dir <PATH>")]
    [Description("Cache folder for downloads and instances (default: PGEMBED_CACHE_DIR, else the per-user cache folder)")]
    public string? CacheDir { get; set; }

    [CommandOption("--maven-repo <URL>")]
    [Description("Maven repository or mirror with the Postgres binaries (default: Maven Central, or PGEMBED_MAVEN_REPO)")]
    public string? MavenRepo { get; set; }

    [CommandOption("--json")]
    [Description("Machine-readable output")]
    public bool Json { get; set; }

    [CommandOption("--verbose")]
    [Description("Log details to standard error")]
    public bool Verbose { get; set; }
}

/// <summary>Everything a command needs, built from its settings.</summary>
internal sealed class CliContext
{
    public CliContext(GlobalSettings settings, CliServices services)
    {
        Settings = settings;
        Services = services;
        Workspace = Workspace.Current;
        MavenRepo = settings.MavenRepo
                    ?? Environment.GetEnvironmentVariable("PGEMBED_MAVEN_REPO")
                    ?? PgServerOptions.DefaultMavenRepo;
        Logger = settings.Verbose ? new StderrLogger(services.Error) : NullLogger.Instance;
        Platform = PgPlatform.Detect();
        Catalog = new VersionCatalog(Workspace, services.Http, MavenRepo);
        Registry = new InstanceRegistry(Workspace);
        Instances = new InstanceManager(Workspace, Registry, Catalog, Platform, services.Http, MavenRepo, Logger);
    }

    public GlobalSettings Settings { get; }
    public CliServices Services { get; }
    public IAnsiConsole Console => Services.Console;
    public Workspace Workspace { get; }
    public string MavenRepo { get; }
    public ILogger Logger { get; }
    public PgPlatformInfo Platform { get; }
    public VersionCatalog Catalog { get; }
    public InstanceRegistry Registry { get; }
    public InstanceManager Instances { get; }

    /// <summary>Writes a value as JSON, bypassing the console renderer so it is never wrapped or colored.</summary>
    public void WriteJson<T>(T value)
    {
        Services.Out.WriteLine(JsonSerializer.Serialize(value, Json.Options));
        Services.Out.Flush();
    }

    public void Warn(string message)
    {
        if (Settings.Json) Services.Error.WriteLine($"warning: {message}");
        else Console.MarkupLineInterpolated($"[yellow]warning:[/] {message}");
    }
}

/// <summary>Applies --cache-dir before a command runs: the library follows PGEMBED_CACHE_DIR.</summary>
internal sealed class CacheDirInterceptor : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (settings is GlobalSettings { CacheDir: { Length: > 0 } dir })
        {
            Environment.SetEnvironmentVariable(PgCache.DirectoryVariable, Path.GetFullPath(dir));
        }
    }
}

/// <summary>
/// Who handles Ctrl+C. By default it cancels the command; the interactive SQL prompt takes it over so
/// Ctrl+C cancels the running statement instead of ending the session.
/// </summary>
internal static class Interrupts
{
    /// <summary>Returns true when it handled the interrupt.</summary>
    public static Func<bool>? Handler { get; set; }
}

internal sealed class StderrLogger(TextWriter error) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        error.WriteLine($"{logLevel.ToString().ToLowerInvariant()}: {formatter(state, exception)}");
        if (exception is not null) error.WriteLine(exception.Message);
    }
}

/// <summary>
/// A minimal container for Spectre.Console.Cli. Following its contract, a service may have several
/// registrations: resolving the service gives the last one, resolving <c>IEnumerable&lt;T&gt;</c> gives all
/// of them (possibly none). Unregistered concrete types, such as commands, are built by their constructor.
/// </summary>
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly Dictionary<Type, List<Func<object>>> _factories = [];

    public void Register(Type service, Type implementation) => Add(service, () => Create(implementation));
    public void RegisterInstance(Type service, object implementation) => Add(service, () => implementation);
    public void RegisterLazy(Type service, Func<object> factory) => Add(service, factory);
    public ITypeResolver Build() => new Resolver(this);

    private void Add(Type service, Func<object> factory)
    {
        if (!_factories.TryGetValue(service, out var list)) _factories[service] = list = [];
        list.Add(factory);
    }

    private object Create(Type type)
    {
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray());
    }

    private object? Resolve(Type? type)
    {
        if (type is null) return null;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var element = type.GetGenericArguments()[0];
            var all = _factories.TryGetValue(element, out var registered) ? registered.Select(f => f()).ToArray() : [];
            var array = Array.CreateInstance(element, all.Length);
            Array.Copy(all, array, all.Length);
            return array;
        }

        if (_factories.TryGetValue(type, out var factories) && factories.Count > 0) return factories[^1]();
        return type.IsAbstract || type.IsInterface ? null : Create(type);
    }

    private sealed class Resolver(TypeRegistrar registrar) : ITypeResolver
    {
        public object? Resolve(Type? type) => registrar.Resolve(type);
    }
}

internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };

    public static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes}m",
        _ => $"{(int)span.TotalSeconds}s"
    };
}
