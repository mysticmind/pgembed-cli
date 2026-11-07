using System.Runtime.CompilerServices;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Infrastructure;
using Spectre.Console.Testing;

namespace PgEmbed.Cli.Tests;

internal static class TestCache
{
    /// <summary>
    /// One cache folder for the whole run, kept between runs so Postgres is downloaded once. Tests
    /// that create instances give them unique names and remove them.
    /// </summary>
    public static readonly string Directory = Path.Combine(Path.GetTempPath(), "pgembed-cli-tests");

    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable(PgCache.DirectoryVariable, Directory);

    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
}

internal sealed record CliRun(int ExitCode, string Console, string Out, string Error)
{
    /// <summary>Everything the command wrote, in the order it reached each stream.</summary>
    public string All => Console + Out + Error;
}

/// <summary>Runs pgembed in-process with the real commands and configuration, capturing every output.</summary>
internal static class TestCli
{
    public static async Task<CliRun> RunAsync(params string[] args) => await RunAsync(null, args);

    public static Task<CliRun> RunAsync(HttpMessageHandler? handler, params string[] args) => RunAsync(handler, false, args);

    /// <summary>As if standard output went to a file or pipe.</summary>
    public static Task<CliRun> RunRedirectedAsync(params string[] args) => RunAsync(null, true, args);

    /// <summary>With a console of the given width, as for the help text the docs show.</summary>
    public static Task<CliRun> RunWithWidthAsync(int width, params string[] args) => RunAsync(null, false, args, width);

    private static async Task<CliRun> RunAsync(HttpMessageHandler? handler, bool redirected, string[] args, int width = 250)
    {
        var console = new TestConsole().Width(width);
        var output = new StringWriter();
        var error = new StringWriter();
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        var services = new CliServices(console, output, error, http, redirected);

        var app = Program.Build(services);
        app.HelpWidth = width;
        var exitCode = await app.RunAsync(args, TestContext.Current.CancellationToken);
        return new CliRun(exitCode, console.Output, output.ToString(), error.ToString());
    }
}
