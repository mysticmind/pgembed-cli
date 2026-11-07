using System.Text;
using System.Text.Json;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Mcp;

namespace PgEmbed.Cli.Tests;

/// <summary>
/// The reference pages of the docs site are generated from the code: each command's help text and the MCP
/// tools with their parameters. These tests fail when the committed files are out of date; run them with
/// PGEMBED_UPDATE_DOCS=1 to rewrite the files.
/// </summary>
public class DocsTests
{
    private static readonly bool Update = Environment.GetEnvironmentVariable("PGEMBED_UPDATE_DOCS") == "1";

    public static TheoryData<string> Commands =>
    [
        "", "versions", "install", "extensions", "start", "stop", "kill", "restart", "status", "connstr", "logs", "rm",
        "sql", "schema", "format", "mcp", "doctor", "cache list", "cache clean"
    ];

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_help_in_the_docs_is_current(string command)
    {
        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append("--help").ToArray();
        var run = await TestCli.RunWithWidthAsync(100, args);
        var help = string.Join('\n', run.Out.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).Trim() + "\n";

        // the help itself, not an error: a required argument must not stop --help
        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains("Usage:", help);
        Assert.Contains("Options:", help);

        var name = command.Length == 0 ? "pgembed" : command.Replace(' ', '-');
        Check(Path.Combine("docs", "reference", "cli", name + ".txt"), help);
    }

    [Fact]
    public void Mcp_tools_in_the_docs_are_current()
    {
        var tools = McpCommand.CreateOptions(new PgEmbedTools(McpCommand.Quiet(new GlobalSettings(), TextWriter.Null, new HttpClient()),
            new McpPolicy(false, false, false))).ToolCollection!.Select(t => t.ProtocolTool).OrderBy(t => t.Name, StringComparer.Ordinal);

        var md = new StringBuilder();
        md.AppendLine("<!-- Generated from the MCP tool definitions by DocsTests; run the tests with PGEMBED_UPDATE_DOCS=1 to update. -->");
        foreach (var tool in tools)
        {
            md.AppendLine().AppendLine($"### `{tool.Name}`").AppendLine();
            var traits = new List<string>();
            if (tool.Annotations?.ReadOnlyHint == true) traits.Add("read-only");
            if (tool.Annotations?.DestructiveHint == true && tool.Annotations?.ReadOnlyHint != true) traits.Add("destructive, asks first");
            if (tool.Annotations?.IdempotentHint == true) traits.Add("idempotent");
            if (traits.Count > 0) md.AppendLine($"*{string.Join(", ", traits)}*").AppendLine();
            md.AppendLine(tool.Description).AppendLine();

            var schema = tool.InputSchema;
            var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()).ToHashSet() : [];
            if (schema.TryGetProperty("properties", out var properties) && properties.EnumerateObject().Any())
            {
                md.AppendLine("| Parameter | Type | | Description |").AppendLine("| --- | --- | --- | --- |");
                foreach (var p in properties.EnumerateObject())
                {
                    var type = p.Value.TryGetProperty("type", out var t)
                        ? t.ValueKind == JsonValueKind.Array ? string.Join(" or ", t.EnumerateArray().Select(x => x.GetString()).Where(x => x != "null")) : t.GetString()
                        : "";
                    var need = required.Contains(p.Name) ? "required"
                        : p.Value.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null ? $"default `{d.GetRawText().Trim('"')}`" : "optional";
                    var description = p.Value.TryGetProperty("description", out var desc) ? desc.GetString() : "";
                    md.AppendLine($"| `{p.Name}` | {type} | {need} | {description?.Replace("|", "\\|")} |");
                }
            }
        }

        Check(Path.Combine("docs", "reference", "generated", "mcp-tools.md"), md.ToString().TrimEnd() + "\n");
    }

    private static void Check(string relative, string expected)
    {
        var path = Path.Combine(RepoRoot(), relative);
        if (Update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
            return;
        }

        Assert.True(File.Exists(path), $"{relative} is missing; run the tests with PGEMBED_UPDATE_DOCS=1");
        Assert.True(File.ReadAllText(path).ReplaceLineEndings("\n") == expected,
            $"{relative} is out of date; run the tests with PGEMBED_UPDATE_DOCS=1 and commit the change");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pgembed.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
