namespace PgEmbed.Cli.Infrastructure;

/// <summary>
/// Files that only you should read: instance settings (which can hold a generated password) and the prompt's
/// history (which holds the SQL you typed). On Linux and macOS they are made 0600, their folders 0700. Windows
/// keeps them in the per-user profile, which other users cannot read.
/// </summary>
internal static class PrivateFiles
{
    public static void File(string path)
    {
        if (OperatingSystem.IsWindows() || !System.IO.File.Exists(path)) return;
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Folder(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Creates the file empty when it does not exist yet, private from the start.</summary>
    public static void Create(string path)
    {
        Folder(Path.GetDirectoryName(path)!);
        if (!System.IO.File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var _ = new FileStream(path, options);
        }

        File(path);
    }
}
