using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Mcp;

namespace PgEmbed.Cli.Tests;

/// <summary>Tests that build the MCP tool definitions run one at a time: the docs compare their generated schemas.</summary>
[CollectionDefinition(Collection)]
public sealed class McpDefinitions
{
    public const string Collection = "mcp definitions";
}

/// <summary>The MCP server, driven by the SDK's own client over in-memory streams, against a real instance.</summary>
[Collection(McpDefinitions.Collection)]
public class McpTests : IAsyncLifetime
{
    private readonly string _name = TestCache.UniqueName("mcp");
    private readonly List<IAsyncDisposable> _cleanup = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) await d.DisposeAsync();
        await TestCli.RunAsync("rm", _name, "--force", "--yes");
    }

    private async Task<McpClient> ConnectAsync(McpPolicy policy, Func<ElicitRequestParams?, ElicitResult>? elicit = null)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var tools = new PgEmbedTools(McpCommand.Quiet(new GlobalSettings(), TextWriter.Null, new HttpClient()), policy);
        var options = McpCommand.CreateOptions(tools);
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "pgembed"), options);
        var running = server.RunAsync(TestContext.Current.CancellationToken);

        var clientOptions = new McpClientOptions();
        if (elicit is not null)
        {
            clientOptions.Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() };
            clientOptions.Handlers = new McpClientHandlers { ElicitationHandler = (request, _) => ValueTask.FromResult(elicit(request)) };
        }

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), clientOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        _cleanup.Add(tools);
        _cleanup.Add(server);
        _cleanup.Add(client);
        return client;
    }

    private static async Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Lists_every_tool()
    {
        var client = await ConnectAsync(new McpPolicy(false, false, false));
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        string[] expected =
        [
            "list_versions", "install_version", "list_extensions", "start_instance", "list_instances", "stop_instance",
            "restart_instance", "kill_instance", "remove_instance", "get_connection_string", "get_logs", "run_sql",
            "check_sql", "format_sql", "explain_query", "describe_schema", "describe_table", "doctor"
        ];
        Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Formats_and_checks_without_a_server()
    {
        var client = await ConnectAsync(new McpPolicy(false, false, false));

        var formatted = await Call(client, "format_sql", new() { ["sql"] = "select 1 as a" });
        Assert.Contains("SELECT 1 AS a;", Text(formatted));

        var checkedSql = await Call(client, "check_sql", new() { ["sql"] = "DELETE FROM t" });
        Assert.Contains("deletes every row of t (no WHERE)", Text(checkedSql));

        var broken = await Call(client, "format_sql", new() { ["sql"] = "SELEC 1" });
        Assert.True(broken.IsError);
        Assert.Contains("syntax error", Text(broken));
    }

    [Fact]
    public async Task Runs_sql_with_confirmation_read_only_mode_and_row_limits()
    {
        var client = await ConnectAsync(new McpPolicy(false, false, false));

        var started = await Call(client, "start_instance", new() { ["name"] = _name, ["version"] = "17", ["port"] = 0 });
        Assert.True(started.IsError != true, Text(started));
        Assert.Contains("connectionString", Text(started));

        // a schema change asks first; without elicitation, it needs confirm: true
        var asked = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "CREATE TABLE books(id int, doc jsonb)" });
        Assert.True(asked.IsError);
        Assert.Contains("confirm: true", Text(asked));

        var created = await Call(client, "run_sql", new()
        {
            ["target"] = _name, ["confirm"] = true,
            ["sql"] = "CREATE TABLE books(id int, doc jsonb); INSERT INTO books SELECT g, jsonb_build_object('n', g) FROM generate_series(1, 300) g"
        });
        Assert.True(created.IsError != true, Text(created));

        // rows are capped, json values are nested, and the result says how to page
        var read = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT id, doc FROM books ORDER BY id", ["max_rows"] = 5 });
        var result = read.StructuredContent!.Value.GetProperty("results")[0];
        Assert.Equal(5, result.GetProperty("rows").GetArrayLength());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Contains("keyset", result.GetProperty("hint").GetString());
        Assert.Equal(1, result.GetProperty("rows")[0][1].GetProperty("n").GetInt32());

        // read-only refuses writes, including COMMIT used to escape the read-only transaction
        var refused = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "COMMIT; DELETE FROM books", ["read_only"] = true });
        Assert.True(refused.IsError);
        Assert.Contains("controls the transaction", Text(refused));

        // errors carry the position, so the assistant can correct the SQL
        var failed = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT titel FROM books" });
        Assert.True(failed.IsError);
        Assert.Contains("[42703]", Text(failed));
        Assert.Contains("LINE 1: SELECT titel FROM books", Text(failed));

        // explain with analyze runs the statement but always rolls it back
        var explained = await Call(client, "explain_query", new() { ["target"] = _name, ["sql"] = "DELETE FROM books", ["analyze"] = true });
        Assert.True(explained.IsError != true, Text(explained));
        Assert.Contains("Actual Rows", Text(explained));
        var count = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT count(*) AS n FROM books" });
        Assert.Equal(300, count.StructuredContent!.Value.GetProperty("results")[0].GetProperty("rows")[0][0].GetInt32());

        var table = await Call(client, "describe_table", new() { ["target"] = _name, ["table"] = "books" });
        Assert.Contains("\"jsonb\"", Text(table));
    }

    [Fact]
    public async Task Asks_the_user_when_the_client_can_and_honours_the_answer()
    {
        string? question = null;
        var agree = false;
        var client = await ConnectAsync(new McpPolicy(false, false, false), request =>
        {
            question = request?.Message;
            return new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(agree) }
            };
        });

        Assert.True((await Call(client, "start_instance", new() { ["name"] = _name, ["version"] = "17", ["port"] = 0 })).IsError != true);

        var declined = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "CREATE TABLE t(a int)", ["confirm"] = true });
        Assert.True(declined.IsError);                       // confirm: true does not bypass asking the user
        Assert.Contains("changes the schema", question);

        agree = true;
        var accepted = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "CREATE TABLE t(a int)" });
        Assert.True(accepted.IsError != true, Text(accepted));
    }

    [Fact]
    public async Task A_named_connection_is_used_by_name_read_only_and_never_shown()
    {
        var started = await TestCli.RunAsync("start", _name, "--version", "17", "--port", "0");
        Assert.True(started.ExitCode == 0, started.All);
        var uri = (await TestCli.RunAsync("connstr", _name, "--format", "uri")).Out.Trim();
        var connectionString = uri.Replace("postgresql://postgres@", "postgresql://postgres:S3cretPw@"); // trust auth ignores it

        var client = await ConnectAsync(new McpPolicy(false, false, false, new Dictionary<string, string> { ["shop"] = connectionString }));

        var listed = await Call(client, "list_instances", []);
        Assert.Contains("\"shop\"", Text(listed));
        Assert.DoesNotContain("S3cretPw", Text(listed));

        var read = await Call(client, "run_sql", new() { ["target"] = "shop", ["sql"] = "SELECT 42 AS answer" });
        Assert.True(read.IsError != true, Text(read));
        Assert.Contains("42", Text(read));
        Assert.DoesNotContain("S3cretPw", Text(read) + read.StructuredContent);

        // a server pgembed did not start: read-only unless --allow-external-writes
        var write = await Call(client, "run_sql", new() { ["target"] = "shop", ["sql"] = "CREATE TABLE t(a int)" });
        Assert.True(write.IsError);
        Assert.Contains("did not start", Text(write));

        var failed = await Call(client, "run_sql", new() { ["target"] = "shop", ["sql"] = "SELECT * FROM missing" });
        Assert.DoesNotContain("S3cretPw", Text(failed));

        var unrecognized = await Call(client, "run_sql", new() { ["target"] = "me:S3cretPw@db/x", ["sql"] = "SELECT 1" });
        Assert.True(unrecognized.IsError);
        Assert.DoesNotContain("S3cretPw", Text(unrecognized));
    }

    [Fact]
    public async Task Read_only_and_explain_cannot_reach_files_or_escape()
    {
        var client = await ConnectAsync(new McpPolicy(false, NoConfirm: true, false));
        Assert.True((await Call(client, "start_instance", new() { ["name"] = _name, ["version"] = "17", ["port"] = 0 })).IsError != true);
        var setup = await Call(client, "run_sql", new()
        {
            ["target"] = _name,
            ["sql"] = "CREATE TABLE books(id int); CREATE FUNCTION config() RETURNS text AS $$ SELECT pg_read_file('postgresql.conf') $$ LANGUAGE sql"
        });
        Assert.True(setup.IsError != true, Text(setup));

        // the statement check refuses file access in read-only mode
        var direct = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT pg_read_file('postgresql.conf')", ["read_only"] = true });
        Assert.True(direct.IsError);
        Assert.Contains("reads files on this machine", Text(direct));

        // a function hides it from the check; the server still refuses, since read-only runs as pg_read_all_data
        var hidden = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT config()", ["read_only"] = true });
        Assert.True(hidden.IsError);
        Assert.Contains("permission denied", Text(hidden));
        var tables = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT count(*) FROM books", ["read_only"] = true });
        Assert.True(tables.IsError != true, Text(tables)); // and it still reads every table

        // SQL the parser cannot read never reaches EXPLAIN as a batch: the COMMIT would end the rolled-back transaction
        var escape = await Call(client, "explain_query", new() { ["target"] = _name, ["sql"] = "SELECT 1; COMMIT; DROP TABLE books; SELEC" });
        Assert.True(escape.IsError);
        var still = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT count(*) FROM books" });
        Assert.True(still.IsError != true, Text(still));
    }

    // From the security review: SQL the parser and the server would read differently must not smuggle a statement
    [Fact]
    public async Task A_string_mode_change_cannot_smuggle_a_program_past_the_checks()
    {
        var client = await ConnectAsync(new McpPolicy(false, false, false));
        Assert.True((await Call(client, "start_instance", new() { ["name"] = _name, ["version"] = "17", ["port"] = 0 })).IsError != true);

        var marker = Path.Combine(Path.GetTempPath(), $"pgembed-smuggled-{Guid.NewGuid():N}");
        var payload = $"SELECT 'a\\' || ' ; COPY (SELECT 1) TO PROGRAM $$touch {marker}$$; --'";

        var normal = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SET standard_conforming_strings = off; " + payload });
        var readOnly = await Call(client, "run_sql", new()
        {
            ["target"] = _name, ["read_only"] = true,
            ["sql"] = "SET standard_conforming_strings = off; " + payload.Replace(" ; COPY", " ; COMMIT; COPY")
        });

        // the program never ran, either way
        Assert.False(File.Exists(marker), $"smuggled COPY TO PROGRAM ran; normal: {Text(normal)} read-only: {Text(readOnly)}");
        Assert.True(normal.IsError);
        Assert.True(readOnly.IsError);

        // even with the setting changed by a separate, confirmed call, a smuggled statement does not run
        var confirmedSet = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SET standard_conforming_strings = off", ["confirm"] = true });
        Assert.True(confirmedSet.IsError); // refused outright in MCP
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_unicode_escaped_function_name_still_asks_first()
    {
        var client = await ConnectAsync(new McpPolicy(false, false, false));
        Assert.True((await Call(client, "start_instance", new() { ["name"] = _name, ["version"] = "17", ["port"] = 0 })).IsError != true);

        foreach (var sql in new[] { "SELECT U&\"pg_read_file\"('postgresql.conf')", "SELECT U&\"pg_read_fil\\0065\"('postgresql.conf')" })
        {
            var result = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = sql });
            Assert.True(result.IsError, sql);
            Assert.Contains("confirm: true", Text(result));
        }
    }

    [Fact]
    public async Task Least_privilege_keeps_the_assistant_off_files_and_programs_even_unconfirmed()
    {
        var started = await TestCli.RunAsync("start", _name, "--version", "17", "--port", "0");
        Assert.True(started.ExitCode == 0, started.All);
        var setup = await TestCli.RunAsync("sql", _name, "--yes",
            "CREATE TABLE books(id int); CREATE FUNCTION config() RETURNS text AS $$ SELECT pg_read_file('postgresql.conf') $$ LANGUAGE sql");
        Assert.True(setup.ExitCode == 0, setup.All);

        // no confirmation at all, so only the server's privileges stand in the way
        var client = await ConnectAsync(new McpPolicy(false, NoConfirm: true, false, LeastPrivilege: true));
        var marker = Path.Combine(Path.GetTempPath(), $"pgembed-least-{Guid.NewGuid():N}");

        var hidden = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT config()" });
        Assert.True(hidden.IsError);
        Assert.Contains("permission denied", Text(hidden));

        var program = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = $"COPY (SELECT 1) TO PROGRAM 'touch {marker}'" });
        Assert.True(program.IsError);
        Assert.False(File.Exists(marker));

        // ordinary work still works: reading and writing the superuser's tables, creating its own
        var work = await Call(client, "run_sql", new()
        {
            ["target"] = _name,
            ["sql"] = "INSERT INTO books VALUES (1); CREATE TABLE notes(id int); INSERT INTO notes VALUES (2); SELECT current_user AS who"
        });
        Assert.True(work.IsError != true, Text(work));
        Assert.Contains(PgEmbedTools.AssistantRole, Text(work));
    }

    [Fact]
    public void Relaxed_protections_are_named()
    {
        Assert.Empty(new McpPolicy(false, false, false).Relaxations());
        var relaxed = new McpPolicy(false, NoConfirm: true, AllowExternalWrites: true).Relaxations().ToList();
        Assert.Contains(relaxed, r => r.StartsWith("--no-confirm"));
        Assert.Contains(relaxed, r => r.StartsWith("--allow-external-writes"));
    }

    [Fact]
    public async Task A_read_only_server_refuses_every_write()
    {
        var client = await ConnectAsync(new McpPolicy(ReadOnly: true, false, false));
        Assert.True((await Call(client, "start_instance", new() { ["name"] = _name, ["version"] = "17", ["port"] = 0 })).IsError != true);

        var insert = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "CREATE TABLE t(a int)" });
        Assert.True(insert.IsError);
        Assert.Contains("this server runs read-only", Text(insert));

        var read = await Call(client, "run_sql", new() { ["target"] = _name, ["sql"] = "SELECT 1 AS one" });
        Assert.True(read.IsError != true, Text(read));
    }
}

/// <summary>The MCP Registry listing in the package stays in step with the package.</summary>
public class McpListingTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pgembed.slnx"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void Server_json_matches_the_package_version_and_the_registry_limits()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(RepoFile("src", "pgembed", ".mcp", "server.json")));
        var root = json.RootElement;
        var version = typeof(McpCommand).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];

        Assert.Equal(version, root.GetProperty("version").GetString());
        Assert.Equal(version, root.GetProperty("packages")[0].GetProperty("version").GetString());
        Assert.InRange(root.GetProperty("description").GetString()!.Length, 1, 100);

        // the registry checks that the package README names the server
        var name = root.GetProperty("name").GetString();
        Assert.Contains($"<!-- mcp-name: {name} -->", File.ReadAllText(RepoFile("README.md")));
    }
}
