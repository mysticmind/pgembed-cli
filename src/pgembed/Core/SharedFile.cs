namespace PgEmbed.Cli.Core;

/// <summary>
/// Small files that several pgembed processes read and replace at once: the version cache and each instance's
/// settings. A replace writes a temporary file and moves it over the old one, so a reader never sees half a
/// file. On Windows that move fails while another process has the file open, unless it opened the file with
/// <see cref="FileShare.Delete"/>, or is replacing it at the same moment; reads here always share delete, and a
/// replace retries briefly.
/// </summary>
internal static class SharedFile
{
    private const int Attempts = 10; // about half a second in all

    /// <summary>Reads the file, retrying briefly when it is being replaced (on Windows, opening it then is denied).</summary>
    public static string ReadAllText(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       && ex is not (FileNotFoundException or DirectoryNotFoundException)
                                       && attempt < Attempts)
            {
                Thread.Sleep(attempt * 10);
            }
        }
    }

    /// <summary>Moves <paramref name="temp"/> over <paramref name="path"/>; on failure the temporary file is removed.</summary>
    public static void Replace(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts)
                {
                    Thread.Sleep(attempt * 10);
                    continue;
                }

                try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }
}
