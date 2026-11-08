using System.Diagnostics;

namespace PgEmbed.Cli.Infrastructure;

/// <summary>
/// With PGEMBED_TRACE set, writes how long the process has been running at each mark to standard error, to see
/// where a command's time goes. Off by default, at the cost of one environment lookup.
/// </summary>
internal static class StartupTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("PGEMBED_TRACE") is { Length: > 0 };

    public static void Mark(string what)
    {
        if (!Enabled) return;
        var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
        Console.Error.WriteLine($"{elapsed.TotalMilliseconds,6:0} ms  {what}");
    }
}
