using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using MysticMind.PostgresEmbed;

namespace PgEmbed.Cli.Core;

/// <summary>A platform's published versions, as cached.</summary>
internal sealed record VersionList(IReadOnlyList<string> Versions, DateTimeOffset FetchedAt, bool FromStaleCache);

/// <summary>
/// The versions published per platform, cached in <c>metadata/&lt;artifact&gt;.json</c>. Fresh for
/// <see cref="MaxAge"/>; after that one conditional request (<c>If-None-Match</c>), which Maven answers with
/// <c>304 Not Modified</c> when nothing changed. Offline, the cached list is used however old.
/// </summary>
internal sealed class VersionCatalog(Workspace workspace, HttpClient http, string mavenRepo)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private sealed record Cached(string? ETag, DateTimeOffset FetchedAt, List<string> Versions);

    public async Task<VersionList> GetAsync(PgPlatformInfo platform, bool refresh, CancellationToken ct)
    {
        var file = Path.Combine(workspace.MetadataDir, platform.ArtifactName + ".json");
        var cached = Read(file);

        if (cached is not null && !refresh && DateTimeOffset.UtcNow - cached.FetchedAt < MaxAge)
        {
            return new VersionList(cached.Versions, cached.FetchedAt, false);
        }

        var url = $"{mavenRepo.TrimEnd('/')}/io/zonky/test/postgres/embedded-postgres-binaries-{platform.ArtifactName}/maven-metadata.xml";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                var touched = cached with { FetchedAt = DateTimeOffset.UtcNow };
                Write(file, touched);
                return new VersionList(touched.Versions, touched.FetchedAt, false);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PostgresEmbedException($"No Postgres binaries are published for {platform.RuntimeIdentifier} ({url}).");
            }

            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var versions = XDocument.Parse(xml).Descendants("version").Select(e => e.Value.Trim())
                .Where(v => VersionOrder.TryParse(v, out _)).Distinct().OrderBy(v => v, VersionOrder.Comparer).ToList();

            var fresh = new Cached(response.Headers.ETag?.ToString(), DateTimeOffset.UtcNow, versions);
            Write(file, fresh);
            return new VersionList(versions, fresh.FetchedAt, false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
                                   && !ct.IsCancellationRequested && cached is not null)
        {
            return new VersionList(cached.Versions, cached.FetchedAt, true);
        }
    }

    /// <summary>The exact version for a spec: exact versions pass through without any lookup.</summary>
    public async Task<string> ResolveAsync(string spec, PgPlatformInfo platform, bool refresh, CancellationToken ct)
    {
        if (PgVersions.IsExact(spec)) return spec;

        var list = await GetAsync(platform, refresh, ct).ConfigureAwait(false);
        return PgVersions.Match(spec, list.Versions)
               ?? throw new PostgresEmbedException(
                   $"No Postgres version matching '{spec}' is published for {platform.RuntimeIdentifier}. " +
                   $"Available majors: {string.Join(", ", list.Versions.Select(VersionOrder.Major).Distinct())}.");
    }

    private static Cached? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Cached>(File.ReadAllText(file), Json.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null; // a damaged cache file is simply refetched
        }
    }

    private static void Write(string file, Cached cached)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cached, Json.Options));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>Orders zonky versions: 9.6.24 &lt; 10.23.0 &lt; 13.1.0 &lt; 13.1.0-1 (a repackaged build).</summary>
internal static class VersionOrder
{
    public static readonly IComparer<string> Comparer = Comparer<string>.Create((a, b) =>
    {
        TryParse(a, out var x);
        TryParse(b, out var y);
        for (var i = 0; i < Math.Max(x.Parts.Length, y.Parts.Length); i++)
        {
            var c = (i < x.Parts.Length ? x.Parts[i] : 0).CompareTo(i < y.Parts.Length ? y.Parts[i] : 0);
            if (c != 0) return c;
        }

        return x.Revision.CompareTo(y.Revision);
    });

    public static bool TryParse(string? value, out (int[] Parts, int Revision) version)
    {
        version = ([], 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        var dash = value.IndexOf('-');
        var revision = 0;
        if (dash >= 0 && !int.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out revision)) return false;

        var pieces = (dash >= 0 ? value[..dash] : value).Split('.');
        var parts = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
        }

        version = (parts, revision);
        return true;
    }

    /// <summary>The major: the first number from Postgres 10 on, the first two before (9.6).</summary>
    public static string Major(string version) =>
        TryParse(version, out var v) && v.Parts.Length > 0
            ? v.Parts[0] >= 10 || v.Parts.Length < 2 ? v.Parts[0].ToString(CultureInfo.InvariantCulture) : $"{v.Parts[0]}.{v.Parts[1]}"
            : version;
}
