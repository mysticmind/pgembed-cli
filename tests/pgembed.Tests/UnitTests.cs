using System.Net;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Sql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;

namespace PgEmbed.Cli.Tests;

public class SqlSplitTests
{
    [Fact]
    public void Splits_on_statement_boundaries_only()
    {
        const string sql = """
            CREATE FUNCTION f() RETURNS int AS $$ SELECT 1; $$ LANGUAGE sql;
            INSERT INTO t VALUES ('a;b'), ($q$c;d$q$);
            -- a comment; with a semicolon
            SELECT 1
            """;

        var statements = SqlRunner.Split(sql);

        Assert.Equal(3, statements.Count);
        Assert.StartsWith("CREATE FUNCTION", statements[0]);
        Assert.Contains("'a;b'", statements[1]);
        Assert.EndsWith("SELECT 1", statements[2]);
    }

    [Fact]
    public void Splits_a_script_the_parser_rejects_with_the_scanner()
    {
        // each part reaches the server on its own; the one the parser cannot read asks first
        Assert.Equal(["SELECT FROM WHERE", "SELECT 2"], SqlRunner.Split("SELECT FROM WHERE; SELECT 2"));
        Assert.Equal(["SELEC 'a;b'", "SELECT $$c;d$$"], SqlRunner.Split("SELEC 'a;b'; SELECT $$c;d$$"));
        // the scanner skips statements that start with a word it does not know; none is lost
        Assert.Equal(["SELECT 'a;b'", "SELEC 2"], SqlRunner.Split("SELECT 'a;b'; SELEC 2"));
        // two such statements in a row stay together, and the server refuses a command with two statements
        Assert.Equal(["SELEC 1; SELEC 'é;'", "SELECT 2"], SqlRunner.Split("SELEC 1; SELEC 'é;'; SELECT 2"));
        Assert.Contains(SqlSafety.Changes(SqlRunner.Split("SELEC 1; SELECT 2")), c => c.Statement == "SELEC 1" && c.What.Contains("could not be checked"));
    }
}

public class VersionOrderTests
{
    [Fact]
    public void Orders_numerically_with_repackaged_builds_last()
    {
        string[] versions = ["13.1.0-1", "10.23.0", "9.6.24", "13.1.0", "13.10.0"];
        Assert.Equal(["9.6.24", "10.23.0", "13.1.0", "13.1.0-1", "13.10.0"], versions.Order(VersionOrder.Comparer));
    }

    [Theory]
    [InlineData("17.6.0", "17")]
    [InlineData("9.6.24", "9.6")]
    [InlineData("13.1.0-1", "13")]
    public void Finds_the_major(string version, string major) => Assert.Equal(major, VersionOrder.Major(version));
}

public class VersionCatalogTests : IDisposable
{
    private readonly Workspace _workspace = new(Path.Combine(Path.GetTempPath(), "pgembed-cli-catalog-" + Guid.NewGuid().ToString("N")));
    private readonly PgPlatformInfo _platform = PgPlatform.Detect();

    private const string Metadata = """
        <metadata><versioning><versions>
          <version>16.9.0</version><version>17.5.0</version><version>17.6.0</version><version>not-a-version</version>
        </versions></versioning></metadata>
        """;

    public void Dispose()
    {
        if (Directory.Exists(_workspace.CacheDir)) Directory.Delete(_workspace.CacheDir, recursive: true);
    }

    [Fact]
    public async Task Fetches_once_then_serves_the_cache()
    {
        var maven = new FakeMaven(_ => Ok(Metadata, "\"v1\""));
        var catalog = new VersionCatalog(_workspace, new HttpClient(maven), "https://maven.test");

        var first = await catalog.GetAsync(_platform, false, TestContext.Current.CancellationToken);
        var second = await catalog.GetAsync(_platform, false, TestContext.Current.CancellationToken);

        Assert.Equal(["16.9.0", "17.5.0", "17.6.0"], first.Versions);
        Assert.Equal(first.Versions, second.Versions);
        Assert.Single(maven.Requests);
    }

    [Fact]
    public async Task Refresh_sends_the_etag_and_keeps_the_list_on_304()
    {
        var maven = new FakeMaven(r => r.Headers.IfNoneMatch.Count > 0
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Ok(Metadata, "\"v1\""));
        var catalog = new VersionCatalog(_workspace, new HttpClient(maven), "https://maven.test");

        await catalog.GetAsync(_platform, false, TestContext.Current.CancellationToken);
        var refreshed = await catalog.GetAsync(_platform, true, TestContext.Current.CancellationToken);

        Assert.Equal("\"v1\"", maven.Requests[1].Headers.IfNoneMatch.Single().ToString());
        Assert.Equal(3, refreshed.Versions.Count);
        Assert.False(refreshed.FromStaleCache);
    }

    [Fact]
    public async Task Falls_back_to_the_cached_list_when_offline()
    {
        var online = true;
        var maven = new FakeMaven(_ => online ? Ok(Metadata, null) : throw new HttpRequestException("offline"));
        var catalog = new VersionCatalog(_workspace, new HttpClient(maven), "https://maven.test");

        await catalog.GetAsync(_platform, false, TestContext.Current.CancellationToken);
        online = false;
        var stale = await catalog.GetAsync(_platform, true, TestContext.Current.CancellationToken);

        Assert.True(stale.FromStaleCache);
        Assert.Equal(3, stale.Versions.Count);
    }

    [Fact]
    public async Task Resolves_specs_and_passes_exact_versions_through_without_a_request()
    {
        var maven = new FakeMaven(_ => Ok(Metadata, null));
        var catalog = new VersionCatalog(_workspace, new HttpClient(maven), "https://maven.test");
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("15.2.0", await catalog.ResolveAsync("15.2.0", _platform, false, ct));
        Assert.Empty(maven.Requests);

        Assert.Equal("17.6.0", await catalog.ResolveAsync("17", _platform, false, ct));
        Assert.Equal("17.5.0", await catalog.ResolveAsync("17.5", _platform, false, ct));
        Assert.Equal("17.6.0", await catalog.ResolveAsync("latest", _platform, false, ct));

        var missing = await Assert.ThrowsAsync<PostgresEmbedException>(() => catalog.ResolveAsync("12", _platform, false, ct));
        Assert.Contains("16, 17", missing.Message);
    }

    // tests and parallel `pgembed start`s share one cache file; on Windows, replacing it while another process
    // read or replaced it failed the start with "Access to the path is denied"
    [Fact]
    public async Task Concurrent_refreshes_share_the_cache_file()
    {
        var maven = new FakeMaven(_ => Ok(Metadata, "\"v1\""));
        var ct = TestContext.Current.CancellationToken;

        var lists = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            new VersionCatalog(_workspace, new HttpClient(maven), "https://maven.test").GetAsync(_platform, true, ct), ct)));

        Assert.All(lists, list => Assert.Equal(["16.9.0", "17.5.0", "17.6.0"], list.Versions));
        Assert.Empty(Directory.EnumerateFiles(_workspace.MetadataDir, "*.tmp"));
    }

    [Fact]
    public async Task Reports_a_platform_without_binaries()
    {
        var maven = new FakeMaven(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var catalog = new VersionCatalog(_workspace, new HttpClient(maven), "https://maven.test");

        await Assert.ThrowsAsync<PostgresEmbedException>(() => catalog.GetAsync(_platform, false, TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage Ok(string body, string? etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }
}

public class InstanceRegistryTests : IDisposable
{
    private readonly Workspace _workspace = new(Path.Combine(Path.GetTempPath(), "pgembed-cli-registry-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_workspace.CacheDir)) Directory.Delete(_workspace.CacheDir, recursive: true);
    }

    [Fact]
    public void Saves_and_reads_a_record()
    {
        var registry = new InstanceRegistry(_workspace);
        var record = new InstanceRecord("app", "17.6.0", 5433, new() { ["shared_buffers"] = "64MB" }, ["https://x/vector.zip"],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        registry.Save(record);
        var read = registry.Get("app");

        Assert.NotNull(read);
        Assert.Equal(5433, read.Port);
        Assert.Equal("64MB", read.ServerParams["shared_buffers"]);
        Assert.Equal(["app"], registry.Names());
        Assert.Null(registry.Get("other"));
    }

    [Fact]
    public void Matches_wildcards_against_existing_names()
    {
        var registry = new InstanceRegistry(_workspace);
        foreach (var name in new[] { "ci-16", "ci-17", "app" })
        {
            registry.Save(new InstanceRecord(name, "17.6.0", 0, [], [], DateTimeOffset.UtcNow, null));
        }

        Assert.Equal(["ci-16", "ci-17"], registry.Match("ci-*").Order());
        Assert.Equal(["ci-17"], registry.Match("ci-?7"));
        Assert.Equal(["missing"], registry.Match("missing")); // a plain name is returned as given
    }

    // `start` saves an instance's settings while `ls`, `status` or the MCP server may be reading them; on Windows,
    // replacing the file while another process read it failed the start with "Access to the path is denied"
    [Fact]
    public async Task Saves_while_others_read_and_save_the_same_record()
    {
        var registry = new InstanceRegistry(_workspace);
        var ct = TestContext.Current.CancellationToken;
        registry.Save(new InstanceRecord("app", "17.6.0", 5000, [], [], DateTimeOffset.UtcNow, null));

        var saves = Enumerable.Range(1, 16).Select(i => Task.Run(() =>
            registry.Save(new InstanceRecord("app", "17.6.0", 5000 + i, [], [], DateTimeOffset.UtcNow, null)), ct));
        var reads = Enumerable.Range(1, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++) Assert.NotNull(registry.Get("app"));
        }, ct));
        await Task.WhenAll(saves.Concat(reads));

        Assert.InRange(registry.Get("app")!.Port, 5001, 5016);
        Assert.Empty(Directory.EnumerateFiles(_workspace.InstanceDir("app"), "*.tmp"));
    }
}

public class ExtensionArgumentTests
{
    [Fact]
    public void Reads_an_optional_checksum_after_the_url()
    {
        var plain = InstanceManager.ParseExtension("https://example.com/vector.zip");
        var pinned = InstanceManager.ParseExtension("https://example.com/vector.zip#sha256=abc123");

        Assert.Equal("https://example.com/vector.zip", plain.DownloadUrl);
        Assert.Null(plain.Sha256);
        Assert.Equal("https://example.com/vector.zip", pinned.DownloadUrl);
        Assert.Equal("abc123", pinned.Sha256);
    }
}

internal sealed class FakeMaven(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

public class LocaleTests
{
    [Fact]
    public void Keeps_the_default_when_en_US_exists() => Assert.Null(InstanceManager.ChooseLocale(["C", "C.utf8", "en_US.utf8", "POSIX"]));

    [Fact]
    public void Uses_C_UTF8_when_en_US_is_missing() => Assert.Equal("C.UTF-8", InstanceManager.ChooseLocale(["C", "C.utf8", "POSIX"]));

    [Fact]
    public void Falls_back_to_C() => Assert.Equal("C", InstanceManager.ChooseLocale(["C", "POSIX"]));

    [Fact]
    public void Keeps_the_default_when_nothing_is_listed() => Assert.Null(InstanceManager.ChooseLocale([]));
}
