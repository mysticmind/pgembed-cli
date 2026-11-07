using System.Text.Json;
using PgEmbed.Cli.Core;

namespace PgEmbed.Cli.Tests;

/// <summary>The offline checks and formatting: no server needed.</summary>
public class AnalysisTests
{
    [Fact]
    public void Summarizes_each_statement_and_what_would_ask_first()
    {
        var result = SqlAnalysis.Check(
            "SELECT b.title, upper(a.name) FROM books b JOIN authors a ON a.id = b.author_id; " +
            "UPDATE books SET price = 1; CREATE TABLE x(id int); INSERT INTO log SELECT * FROM books");

        Assert.True(result.Valid);
        Assert.Equal(4, result.Statements.Count);

        var select = result.Statements[0];
        Assert.Equal(["SELECT"], select.Kinds);
        Assert.Contains(new TableUse("books", "read"), select.Tables);
        Assert.Contains(new TableUse("authors", "read"), select.Tables);
        Assert.Contains("upper", select.Functions);
        Assert.Null(select.Change);

        var update = result.Statements[1];
        Assert.Equal(["UPDATE"], update.Kinds);
        Assert.Contains(new TableUse("books", "write"), update.Tables);
        Assert.True(update.ChangesEveryRow);

        Assert.Equal(["CREATE TABLE"], result.Statements[2].Kinds);
        Assert.Equal("changes the schema", result.Statements[2].Change);

        Assert.Contains(new TableUse("log", "write"), result.Statements[3].Tables);
        Assert.Null(result.Statements[3].Change); // INSERT does not ask
    }

    [Fact]
    public void Reports_a_syntax_error_with_its_position()
    {
        var result = SqlAnalysis.Check("SELECT 1; SELEC 2");
        Assert.False(result.Valid);
        Assert.Contains("SELEC", result.Error!.Message);
        Assert.Equal(11, result.Error.Position);
    }

    [Fact]
    public void Formats_keeping_comments_and_is_stable()
    {
        var (formatted, error) = SqlAnalysis.Format("select a,b from t -- why\nwhere a>1");
        Assert.Null(error);
        Assert.Equal("SELECT a, b\nFROM t\nWHERE\n    -- why\n    a > 1;\n", formatted);

        var (again, _) = SqlAnalysis.Format(formatted!);
        Assert.Equal(formatted, again);
    }

    [Theory]
    [InlineData("SelectStmt", "SELECT")]
    [InlineData("CreateStmt", "CREATE TABLE")]
    [InlineData("CreateFunctionStmt", "CREATE FUNCTION")]
    [InlineData("AlterTableStmt", "ALTER TABLE")]
    [InlineData("VariableSetStmt", "SET")]
    public void Names_statement_kinds_as_sql(string node, string kind) => Assert.Equal(kind, SqlAnalysis.Kind(node));
}

public class FormatAndCheckCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pgembed-format-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Check_takes_its_one_argument_as_the_sql()
    {
        var run = await TestCli.RunAsync("sql", "--check", "SELECT * FROM books WHERE id = 1; DELETE FROM books", "--json");
        Assert.True(run.ExitCode == 0, run.All);

        using var json = JsonDocument.Parse(run.Out);
        var statements = json.RootElement.GetProperty("statements");
        Assert.Equal(2, statements.GetArrayLength());
        Assert.True(statements[1].GetProperty("changesEveryRow").GetBoolean());
    }

    [Fact]
    public async Task Check_reports_a_syntax_error()
    {
        var run = await TestCli.RunAsync("sql", "--check", "SELEC 1");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("syntax error at or near \"SELEC\"", run.Console);
    }

    [Fact]
    public async Task Format_prints_formatted_sql()
    {
        var run = await TestCli.RunAsync("format", "select 1 as a");
        Assert.True(run.ExitCode == 0, run.All);
        Assert.Equal("SELECT 1 AS a;", run.Out.Trim());
    }

    [Fact]
    public async Task Format_checks_and_rewrites_files()
    {
        var messy = Path.Combine(_dir, "messy.sql");
        var tidy = Path.Combine(_dir, "tidy.sql");
        await File.WriteAllTextAsync(messy, "select a from t where a>1", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tidy, "SELECT 1;\n", TestContext.Current.CancellationToken);

        var check = await TestCli.RunAsync("format", "-f", messy, "-f", tidy, "--check");
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("not formatted:", check.Console);
        Assert.Contains("messy.sql", check.Console);
        Assert.DoesNotContain("tidy.sql", check.Console);

        var write = await TestCli.RunAsync("format", "-f", messy, "-f", tidy, "--write");
        Assert.True(write.ExitCode == 0, write.All);
        Assert.Equal("SELECT a\nFROM t\nWHERE a > 1;\n", await File.ReadAllTextAsync(messy, TestContext.Current.CancellationToken));

        var clean = await TestCli.RunAsync("format", "-f", messy, "-f", tidy, "--check");
        Assert.True(clean.ExitCode == 0, clean.All);
    }

    [Fact]
    public async Task Format_reports_a_file_that_does_not_parse()
    {
        var broken = Path.Combine(_dir, "broken.sql");
        await File.WriteAllTextAsync(broken, "SELEC 1", TestContext.Current.CancellationToken);

        var run = await TestCli.RunAsync("format", "-f", broken, "--check");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("broken.sql", run.Console);
        Assert.Contains("syntax error", run.Console);
    }
}
