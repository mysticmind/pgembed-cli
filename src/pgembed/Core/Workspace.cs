using System.Text.RegularExpressions;
using MysticMind.PostgresEmbed;

namespace PgEmbed.Cli.Core;

/// <summary>
/// The folders pgembed works in. They follow the library's shared cache
/// (<see cref="PgCache.DefaultDirectory"/>), so instances started here are the same ones a test using
/// <c>InstanceName</c> attaches to. <c>--cache-dir</c> is applied by setting <c>PGEMBED_CACHE_DIR</c> for
/// this process before a command runs (see <see cref="CacheDirInterceptor"/>).
/// </summary>
internal sealed partial class Workspace
{
    public Workspace(string cacheDir)
    {
        CacheDir = Path.GetFullPath(cacheDir);
    }

    public static Workspace Current => new(PgCache.DefaultDirectory);

    public string CacheDir { get; }

    /// <summary>Downloads and extracted installs; the library's default when PGEMBED_CACHE_DIR is set.</summary>
    public string BinariesDir => Path.Combine(CacheDir, "binaries");

    /// <summary>Named instances; where the library keeps them.</summary>
    public string InstancesDir => Path.Combine(CacheDir, "instances");

    /// <summary>Version metadata, owned by the CLI.</summary>
    public string MetadataDir => Path.Combine(CacheDir, "metadata");

    /// <summary>An instance's folder. Names follow the library's rule, so a name never leaves the instances folder.</summary>
    public string InstanceDir(string name) =>
        IsValidName(name)
            ? Path.Combine(InstancesDir, name)
            : throw new PostgresEmbedException(
                $"'{name}' is not a valid instance name: use letters, digits, '.', '_' and '-', starting with a letter or digit, up to 63 characters.");

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex ValidName();
}
