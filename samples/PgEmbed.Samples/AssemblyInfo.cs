using System.Runtime.CompilerServices;

// The samples share the "app" instance, so they run one after another.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace PgEmbed.Samples;

internal static class SampleCache
{
    /// <summary>
    /// The samples use their own cache folder, so running them never touches your own "app" instance. CI can
    /// point PGEMBED_CACHE_DIR at a cached folder instead.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("PGEMBED_CACHE_DIR") is null)
        {
            Environment.SetEnvironmentVariable("PGEMBED_CACHE_DIR", Path.Combine(Path.GetTempPath(), "pgembed-samples"));
        }
    }
}
