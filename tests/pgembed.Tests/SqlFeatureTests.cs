using Npgsql;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Sql;

namespace PgEmbed.Cli.Tests;

public class ConnectionFormatTests
{
    [Fact]
    public void Formats_every_kind_with_a_schema()
    {
        Assert.Equal("Host=localhost;Port=5433;Username=postgres;Database=shop;Search Path=sales",
            Connections.Format("npgsql", 5433, "shop", "sales"));
        Assert.Equal("postgresql://postgres@localhost:5433/shop?options=-c%20search_path%3Dsales",
            Connections.Format("uri", 5433, "shop", "sales"));
        Assert.Equal("host=localhost port=5433 user=postgres dbname=shop options='-c search_path=sales'",
            Connections.Format("libpq", 5433, "shop", "sales"));
        Assert.Equal("jdbc:postgresql://localhost:5433/shop?user=postgres&currentSchema=sales",
            Connections.Format("jdbc", 5433, "shop", "sales"));
        Assert.Contains("PGOPTIONS='-c search_path=sales'", Connections.Format("env", 5433, "shop", "sales"));
    }

    [Theory]
    [InlineData("npgsql")]
    [InlineData("uri")]
    [InlineData("libpq")]
    [InlineData("jdbc")]
    public void Reads_back_what_it_writes(string format)
    {
        var builder = Connections.Parse(Connections.Format(format, 5433, "shop", null));

        Assert.Equal("localhost", builder.Host);
        Assert.Equal(5433, builder.Port);
        Assert.Equal("postgres", builder.Username);
        Assert.Equal("shop", builder.Database);
    }

    [Fact]
    public void Reads_uri_credentials_and_parameters()
    {
        var builder = Connections.Parse("postgres://me:p%40ss@db.example.com:6543/my%20db?sslmode=require&application_name=pgembed");

        Assert.Equal("me", builder.Username);
        Assert.Equal("p@ss", builder.Password);
        Assert.Equal("db.example.com", builder.Host);
        Assert.Equal(6543, builder.Port);
        Assert.Equal("my db", builder.Database);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.Equal("pgembed", builder.ApplicationName);
    }

    [Fact]
    public void Reads_libpq_quoting_and_jdbc_schema()
    {
        var libpq = Connections.Parse(@"host=localhost dbname='my db' user=me password='it\'s'");
        Assert.Equal("my db", libpq.Database);
        Assert.Equal("it's", libpq.Password);

        var jdbc = Connections.Parse("jdbc:postgresql://localhost/shop?user=me&currentSchema=sales&ssl=true");
        Assert.Equal("sales", jdbc.SearchPath);
        Assert.Equal(SslMode.Require, jdbc.SslMode);
    }

    [Fact]
    public void Tells_instance_names_from_connection_strings()
    {
        Assert.False(Connections.IsConnectionString("app"));
        Assert.False(Connections.IsConnectionString("ci-17"));
        Assert.True(Connections.IsConnectionString("postgresql://localhost/db"));
        Assert.True(Connections.IsConnectionString("host=localhost"));
        Assert.True(Connections.IsConnectionString("jdbc:postgresql://localhost/db"));
    }

    [Fact]
    public void Hides_the_password()
    {
        var shown = Connections.Redacted(Connections.Parse("postgresql://me:secret@localhost/db"));
        Assert.DoesNotContain("secret", shown);
    }

    [Fact]
    public void Rejects_unknown_parameters_clearly()
    {
        var error = Assert.Throws<MysticMind.PostgresEmbed.PostgresEmbedException>(() => Connections.Parse("postgresql://localhost/db?bogus=1"));
        Assert.Contains("bogus", error.Message);
    }

    [Fact]
    public void Converts_postgres_positions_counted_in_code_points()
    {
        const string sql = "SELECT '😀', x"; // the emoji is one code point, two UTF-16 units
        Assert.Equal(sql.IndexOf('x'), Connections.CodePointToIndex(sql, 13));
    }
}

public class SqlSafetyTests
{
    private static IReadOnlyList<SqlChange> Changes(string sql) => SqlSafety.Changes(SqlRunner.Split(sql));

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("BEGIN; COMMIT")]
    [InlineData("SET search_path TO sales")]
    public void Reads_and_inserts_run_without_asking(string sql) => Assert.Empty(Changes(sql));

    [Theory]
    [InlineData("CREATE TABLE t (id int)", "changes the schema")]
    [InlineData("ALTER TABLE t ADD COLUMN x int", "changes the schema")]
    [InlineData("DROP TABLE t", "changes the schema")]
    [InlineData("TRUNCATE t", "every row")]
    [InlineData("UPDATE t SET x = 1 WHERE id = 2", "updates rows of t")]
    [InlineData("DELETE FROM sales.t WHERE id = 2", "deletes rows of sales.t")]
    [InlineData("SELECT * INTO copy FROM t", "creates a table")]
    [InlineData("WITH gone AS (DELETE FROM t RETURNING *) SELECT count(*) FROM gone", "WITH")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t WHERE id = 1", "deletes rows")]
    public void Changes_need_confirming(string sql, string what)
    {
        var change = Assert.Single(Changes(sql));
        Assert.Contains(what, change.What);
    }

    [Fact]
    public void Calls_out_update_and_delete_without_where()
    {
        var changes = Changes("UPDATE t SET x = 1; DELETE FROM t");
        Assert.All(changes, c => Assert.True(c.EveryRow));
        Assert.Contains("no WHERE", changes[1].What);
    }

    [Fact]
    public void Lists_each_changing_statement_of_a_script()
    {
        var changes = Changes("SELECT 1; DROP TABLE a; INSERT INTO b VALUES (1); DELETE FROM c WHERE id = 1");
        Assert.Equal(["DROP TABLE a", "DELETE FROM c WHERE id = 1"], changes.Select(c => c.Statement));
    }
}

public class SqlEditorTests
{
    [Theory]
    [InlineData("SELECT 1;", true)]
    [InlineData("SELECT 1", false)]
    [InlineData("SELECT 1; -- done", true)]
    [InlineData("SELECT 'a;", false)]                                         // unterminated string
    [InlineData("CREATE FUNCTION f() RETURNS int AS $$ SELECT 1;", false)]   // unterminated body
    [InlineData("CREATE FUNCTION f() RETURNS int AS $$ SELECT 1; $$ LANGUAGE sql;", true)]
    [InlineData("\\dt", true)]
    [InlineData("  ", true)]
    [InlineData("quit", true)]
    public void Enter_runs_only_finished_sql(string text, bool complete) => Assert.Equal(complete, SqlEditor.IsComplete(text));

    [Fact]
    public void Highlights_tokens()
    {
        const string sql = "SELECT 'x', 42 -- note";
        var spans = SqlEditor.Highlight(sql);

        Assert.Contains(new StyledSpan(0, 6, SqlStyle.Keyword), spans);
        Assert.Contains(spans, s => s.Style == SqlStyle.String && sql.Substring(s.Start, s.Length) == "'x'");
        Assert.Contains(spans, s => s.Style == SqlStyle.Number && sql.Substring(s.Start, s.Length) == "42");
        Assert.Contains(spans, s => s.Style == SqlStyle.Comment);
        Assert.DoesNotContain(spans, s => s.Style == SqlStyle.Error);
    }

    [Fact]
    public void Underlines_a_syntax_error_but_not_unfinished_input()
    {
        const string bad = "SELECT FROM WHERE x";
        var error = Assert.Single(SqlEditor.Highlight(bad), s => s.Style == SqlStyle.Error);
        Assert.Equal("WHERE", bad.Substring(error.Start, error.Length));

        Assert.DoesNotContain(SqlEditor.Highlight("SELECT * FROM books WHERE"), s => s.Style == SqlStyle.Error);
    }

    private static readonly SqlCatalog Catalog = new(
        ["public", "sales"],
        [new CatalogRelation("public", "books"), new CatalogRelation("sales", "orders")],
        new Dictionary<string, List<string>> { ["books"] = ["id", "title"], ["orders"] = ["id", "total"] });

    private static IReadOnlyList<string> Complete(string text) =>
        SqlEditor.Complete(Catalog, text, text.Length - text.Reverse().TakeWhile(char.IsLetterOrDigit).Count(),
            text.Reverse().TakeWhile(char.IsLetterOrDigit).Count());

    [Fact]
    public void Completes_tables_after_from() => Assert.Equal(["books"], Complete("SELECT * FROM bo"));

    [Fact]
    public void Completes_qualified_names()
    {
        Assert.Equal(["orders"], Complete("SELECT * FROM sales."));
        Assert.Equal(["title"], Complete("SELECT books.t"));
        Assert.Equal(["total"], Complete("SELECT orders.t"));
    }

    [Fact]
    public void Completes_keywords_in_the_case_typed()
    {
        Assert.Contains("SELECT", Complete("SEL"));
        Assert.Contains("select", Complete("sel"));
    }
}

public class AlignedFormatTests
{
    private static string Write(string[] columns, object?[][] rows, bool expanded = false)
    {
        var output = new StringWriter();
        Aligned.Write(output, columns, rows, expanded, v => v?.ToString() ?? "");
        return output.ToString().ReplaceLineEndings("\n");
    }

    [Fact]
    public void Lays_out_like_psql()
    {
        var text = Write(["id", "name"], [[1, "alpha"], [100, null]]);
        Assert.Equal("""
             id  | name
            -----+-------
               1 | alpha
             100 |

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Gives_multi_line_values_several_lines_and_never_wraps()
    {
        var wide = new string('x', 300);
        var text = Write(["note", "wide"], [["one\ntwo", wide]]);
        var lines = text.TrimEnd('\n').Split('\n');

        Assert.Equal(4, lines.Length); // header, rule, two lines for the row
        Assert.EndsWith(wide, lines[2]);
        Assert.StartsWith(" two", lines[3]);
    }

    [Fact]
    public void Shows_one_block_per_row_when_expanded()
    {
        var text = Write(["id", "title"], [[1, "a"], [2, "b"]], expanded: true);
        Assert.Contains("-[ RECORD 2 ]", text);
        Assert.Contains("title | b", text);
    }
}
