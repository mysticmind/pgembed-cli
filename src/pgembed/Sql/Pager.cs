using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace PgEmbed.Cli.Sql;

/// <summary>
/// Shows a long result in a pager, as psql does: in a terminal, a result taller than the screen opens in
/// <c>PGEMBED_PAGER</c>, else <c>PAGER</c>, else <c>less -SFX</c> (-S: long lines scroll sideways instead of
/// wrapping; -F: exit at once when it fits; -X: leave the result on screen). An empty <c>PAGER</c> turns it off.
/// Windows has no good default, so there it is used only when set.
/// </summary>
internal sealed class Pager : IDisposable
{
    public const string Variable = "PGEMBED_PAGER";
    private const string DefaultCommand = "less -SFX";

    private readonly Process _process;

    private Pager(Process process, IAnsiConsole like)
    {
        _process = process;
        process.StandardInput.AutoFlush = false;
        Writer = process.StandardInput;
        Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = like.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = (ColorSystemSupport)like.Profile.Capabilities.ColorSystem,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(Writer)
        });
        Console.Profile.Width = like.Profile.Width; // tables fit the terminal, as they would without a pager
    }

    /// <summary>Plain text goes here.</summary>
    public TextWriter Writer { get; }

    /// <summary>Spectre output goes here, with the terminal's width and colors.</summary>
    public IAnsiConsole Console { get; }

    /// <summary>The pager command from the environment; null when paging is off.</summary>
    public static string? Command()
    {
        var command = Environment.GetEnvironmentVariable(Variable) ?? Environment.GetEnvironmentVariable("PAGER");
        if (command is not null) return command.Trim().Length == 0 ? null : command;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? null : DefaultCommand;
    }

    /// <summary>Starts the pager, or returns null when it cannot start (then output goes to the terminal as usual).</summary>
    public static Pager? Start(string command, IAnsiConsole like)
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var start = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (windows)
        {
            // cmd.exe does not read \" escapes, which ArgumentList would add around a command with quotes in it
            // (PAGER="C:\Program Files\...\less.exe"); with /s it strips only the outer quotes and runs the rest as
            // written. /d skips AutoRun commands.
            start.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        // less shows colors only when asked; keep any options the user set
        if (Environment.GetEnvironmentVariable("LESS") is null) start.Environment["LESS"] = "R";

        try
        {
            return Process.Start(start) is { } process ? new Pager(process, like) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Lines the terminal shows; 0 when there is no terminal.</summary>
    public static int ScreenHeight()
    {
        try
        {
            return System.Console.IsOutputRedirected ? 0 : System.Console.WindowHeight;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Closes the pager's input and waits until the user leaves it.</summary>
    public void Dispose()
    {
        try
        {
            Writer.Flush();
            Writer.Close();
        }
        catch (IOException)
        {
            // the user quit the pager before reading everything
        }

        _process.WaitForExit();
        _process.Dispose();
    }
}
