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

public class ReadOnlyTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("SELECT 1; SHOW search_path")]
    [InlineData("SET search_path = sales; SELECT 1")]
    [InlineData("EXPLAIN DELETE FROM t")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    public void Allows_reads(string sql) => Assert.Empty(SqlSafety.ReadOnlyViolations(Split(sql)));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", "")]
    [InlineData("UPDATE t SET a = 1 WHERE b = 2", "updates rows")]
    [InlineData("COMMIT; DELETE FROM t", "controls the transaction")]
    [InlineData("BEGIN", "controls the transaction")]
    [InlineData("SET TRANSACTION READ WRITE", "transaction or session mode")]
    [InlineData("SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE", "transaction or session mode")]
    [InlineData("SET default_transaction_read_only = off", "transaction or session mode")]
    [InlineData("SELECT * INTO copy FROM t", "")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", "")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", "")]
    [InlineData("CREATE TABLE x(a int)", "schema")]
    [InlineData("COPY t TO PROGRAM 'cat'", "")]
    public void Refuses_writes_and_transaction_control(string sql, string reason)
    {
        var violations = SqlSafety.ReadOnlyViolations(Split(sql));
        Assert.NotEmpty(violations);
        Assert.Contains(reason, violations[0].What);
    }

    private static IReadOnlyList<string> Split(string sql) => PgEmbed.Cli.Sql.SqlRunner.Split(sql);
}

public class FastPathTests
{
    public static TheoryData<string> Statements =>
    [
        "SELECT 1", "SELECT * FROM t WHERE id = 1", "SELECT \"update\", \"delete\" FROM t", "WITH x AS (SELECT 1) SELECT * FROM x",
        "VALUES (1), (2)", "TABLE t", "SHOW search_path", "EXPLAIN SELECT * FROM t", "INSERT INTO t VALUES (1)",
        "INSERT INTO t SELECT * FROM s", "SELECT * FROM t FOR UPDATE", "SELECT * INTO copy FROM t",
        "WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", "EXPLAIN ANALYZE DELETE FROM t", "EXPLAIN DELETE FROM t",
        "INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = 1", "UPDATE t SET a = 1", "DELETE FROM t",
        "TRUNCATE t", "CREATE TABLE x(a int)", "COPY t TO PROGRAM 'cat'", "SELECT 'drop table' AS text", "MERGE INTO t USING s ON true WHEN MATCHED THEN DELETE"
    ];

    [Theory]
    [MemberData(nameof(Statements))]
    public void Never_skips_a_statement_the_full_classification_would_ask_about(string statement)
    {
        if (SqlSafety.IsPlainRead(statement)) Assert.Empty(SqlSafety.Changes([statement]));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT \"update\" FROM t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("SELECT 'drop table' AS text")]
    public void Takes_the_fast_path_for_plain_reads(string statement) => Assert.True(SqlSafety.IsPlainRead(statement));

    [Theory]
    [InlineData("SELECT * INTO copy FROM t")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = 1")]
    [InlineData("UPDATE t SET a = 1")]
    public void Sends_anything_that_might_change_data_to_the_full_classification(string statement) =>
        Assert.False(SqlSafety.IsPlainRead(statement));
}
