using System.Diagnostics;

namespace PgEmbed.Samples;

/// <summary>
/// Runs the pgembed CLI for the samples that use what it prints. PGEMBED is the command (for example
/// "dotnet src/pgembed/bin/Release/net10.0/pgembed.dll"); "pgembed" on PATH otherwise.
/// </summary>
internal static class Cli
{
    public static async Task<string> RunAsync(params string[] args)
    {
        var command = (Environment.GetEnvironmentVariable("PGEMBED") ?? "pgembed").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = new ProcessStartInfo(command[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var part in command.Skip(1).Concat(args)) start.ArgumentList.Add(part);

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"pgembed {string.Join(' ', args)} failed: {error}{output}");
    }
}
