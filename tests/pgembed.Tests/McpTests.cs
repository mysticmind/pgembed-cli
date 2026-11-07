using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Mcp;

namespace PgEmbed.Cli.Tests;

/// <summary>The MCP server, driven by the SDK's own client over in-memory streams, against a real instance.</summary>
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
