using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Mcp;
using Spectre.Console;

namespace PgEmbed.Cli.Commands;

/// <summary>
/// pgembed mcp: an MCP server over standard input and output, for AI assistants. Standard output carries only
/// protocol messages; the CLI's console and output are not used here, and logs go to standard error.
/// </summary>
internal sealed class McpCommand(CliServices services) : AsyncCommand<McpCommand.Settings>
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--read-only")]
        [Description("Refuse anything that writes, on every target")]
        public bool ReadOnly { get; set; }

        [CommandOption("--no-confirm")]
        [Description("Run changes, kills and removals without asking (for sandboxes and throwaway agents)")]
        public bool NoConfirm { get; set; }

        [CommandOption("--allow-external-writes")]
        [Description("Allow writes to servers pgembed did not start (connection-string targets); read-only otherwise")]
        public bool AllowExternalWrites { get; set; }
    }

    public const string Instructions =
        "pgembed runs real PostgreSQL servers of any version locally, with no Docker or install. " +
        "start_instance gives a named instance (it keeps running and keeps its port) and its connection string; " +
        "run_sql, describe_schema, describe_table and explain_query work against an instance name or any connection string; " +
        "check_sql and format_sql need no server. Changes to the schema or existing rows ask for confirmation.";

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        await using var tools = new PgEmbedTools(Quiet(settings, services.Error, services.Http), new McpPolicy(settings.ReadOnly, settings.NoConfirm, settings.AllowExternalWrites));
        var options = CreateOptions(tools);
        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        await server.RunAsync(ct);
        return 0;
    }

    // tool arguments and results: pgembed's generated types, and the SDK's own (CallToolResult and the like)
    private static readonly JsonSerializerOptions ToolJson = new(Core.Json.Compact)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(Core.PgEmbedJsonContext.Default, McpJsonUtilities.DefaultOptions.TypeInfoResolver!)
    };

    /// <summary>The server's options and tools; shared with the tests, which run it over in-memory streams.</summary>
    internal static McpServerOptions CreateOptions(PgEmbedTools tools)
    {
        var version = typeof(McpCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "pgembed", Version = version },
            ServerInstructions = Instructions,
            ToolCollection = []
        };

        foreach (var method in typeof(PgEmbedTools).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
            options.ToolCollection.Add(McpServerTool.Create(method, tools, new McpServerToolCreateOptions { SerializerOptions = ToolJson }));
        }

        return options;
    }

    // a context whose console and output go nowhere: standard output belongs to the protocol
    internal static CliContext Quiet(GlobalSettings settings, TextWriter error, HttpClient http)
    {
        var nowhere = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null), Interactive = InteractionSupport.No });
        return new CliContext(settings, new CliServices(nowhere, TextWriter.Null, error, http, OutputRedirected: true));
    }
}
