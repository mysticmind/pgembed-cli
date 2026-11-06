using System.Text.Json;

namespace PgEmbed.Cli.Tests;

/// <summary>Commands that need no running server and no network.</summary>
public class CommandTests
{
    [Fact]
    public async Task Check_accepts_valid_sql()
    {
        var run = await TestCli.RunAsync("sql", "any", "--check", "SELECT 1; SELECT 2");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Valid SQL: 2 statements", run.Console);
    }

    [Fact]
    public async Task Check_points_at_the_error()
    {
        var run = await TestCli.RunAsync("sql", "any", "--check", "SELECT 1;\nSELECT FROM WHERE x");

        Assert.Equal(1, run.ExitCode);
        var lines = run.Console.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains("syntax error at or near \"WHERE\"", lines[0]);
        Assert.Equal("LINE 2: SELECT FROM WHERE x", lines[1]);
        Assert.Equal(lines[1].IndexOf("WHERE", StringComparison.Ordinal), lines[2].IndexOf('^'));
    }

    [Fact]
    public async Task Check_reports_json()
    {
        var run = await TestCli.RunAsync("sql", "any", "--check", "SELEC 1", "--json");

        using var json = JsonDocument.Parse(run.Out);
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(1, run.ExitCode);
    }

    [Fact]
    public async Task Sql_against_an_unknown_instance_fails_with_one_line()
    {
        var run = await TestCli.RunAsync("sql", TestCache.UniqueName("nope"), "SELECT 1");

        Assert.Equal(1, run.ExitCode);
        Assert.StartsWith("error: There is no instance named", run.Console.Trim());
    }

    [Theory]
    [InlineData("start")]                                 // missing NAME
    [InlineData("versions", "--platform", "amiga-68k")]   // validated option
    [InlineData("sql", "x", "--format", "xml")]
    [InlineData("cache", "clean")]                        // needs one of its options
    [InlineData("no-such-command")]
    public async Task Usage_errors_exit_with_2(params string[] args)
    {
        var run = await TestCli.RunAsync(args);
        Assert.Equal(2, run.ExitCode);
    }

    [Fact]
    public async Task Status_of_unknown_names_is_empty_json()
    {
        var run = await TestCli.RunAsync("status", TestCache.UniqueName("none"), "--json");

        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.Out);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }
}
