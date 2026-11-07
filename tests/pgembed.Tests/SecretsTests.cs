using PgEmbed.Cli.Core;

namespace PgEmbed.Cli.Tests;

/// <summary>Passwords in connection strings never come back out: not in errors, not in MCP results.</summary>
public class SecretsTests
{
    private const string Secret = "S3cretPw";

    [Theory]
    [InlineData("postgresql://me:S3cretPw@db.example.com:5432/shop")]
    [InlineData("postgres://me:S3cretPw@")]
    [InlineData("me:S3cretPw@localhost/db")]
    [InlineData("host=db user=me password=S3cretPw dbname=shop")]
    [InlineData("host=db password='S3cretPw' dbname=shop")]
    [InlineData("Host=db;Username=me;Password=S3cretPw;Database=shop")]
    [InlineData("Host=db;PWD=S3cretPw")]
    [InlineData("Host=db;SSL Password=S3cretPw")]
    [InlineData("jdbc:postgresql://db/shop?user=me&password=S3cretPw&ssl=true")]
    [InlineData("error near 'Password=S3cretPw;Host=x'")]
    public void Masks_passwords_in_every_form(string text)
    {
        var masked = Connections.Mask(text);
        Assert.DoesNotContain(Secret, masked);
        Assert.Contains("***", masked);
    }

    [Theory]
    [InlineData("postgresql://me@db/shop")]
    [InlineData("app")]
    // it errs on the side of masking: anything shaped like user:secret@ is masked, even outside a connection string
    public void Leaves_text_without_passwords_alone(string text) => Assert.Equal(text, Connections.Mask(text));

    [Theory]
    [InlineData("me:S3cretPw@localhost/db")]               // a URI without its scheme
    [InlineData("user=me password=S3cretPw dbnam=x")]      // a mistyped libpq key
    [InlineData("hots=x;Password=S3cretPw")]               // a mistyped Npgsql key
    [InlineData("postgres://me:S3cretPw@localhost:1/db")]  // a server that is not there
    public async Task Sql_errors_never_show_the_password(string target)
    {
        var run = await TestCli.RunAsync("sql", target, "SELECT 1");
        Assert.NotEqual(0, run.ExitCode);
        Assert.DoesNotContain(Secret, run.All);
    }

    [Fact]
    public async Task A_mistyped_libpq_key_is_named()
    {
        var run = await TestCli.RunAsync("sql", "user=me password=S3cretPw dbnam=x", "SELECT 1");
        Assert.Contains("unknown key 'dbnam'", run.All);
    }

    [Fact]
    public async Task A_uri_without_a_host_means_this_machine_instead_of_a_crash()
    {
        var run = await TestCli.RunAsync("sql", "postgres://me:S3cretPw@:1/db", "SELECT 1");
        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain("ArgumentNullException", run.All);
        Assert.DoesNotContain(Secret, run.All);
    }

    [Fact]
    public async Task Mcp_rejects_a_bad_connection_option_without_repeating_it()
    {
        var run = await TestCli.RunAsync("mcp", "--connection", "postgresql://me:S3cretPw@db/shop");
        Assert.Equal(2, run.ExitCode);
        Assert.DoesNotContain(Secret, run.All);
    }
}

/// <summary>What reaches outside the database, or cannot be checked, asks first; read-only mode refuses it.</summary>
public class ServerAccessTests
{
    [Theory]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$", "runs code on the server")]
    [InlineData("CALL cleanup()", "calls a procedure")]
    [InlineData("EXECUTE wipe", "runs a prepared statement")]
    [InlineData("LOAD 'plugin'", "loads a library")]
    [InlineData("COPY books TO '/tmp/books.csv'", "writes a file on this machine")]
    [InlineData("COPY books FROM '/tmp/books.csv'", "reads a file on this machine")]
    [InlineData("COPY books TO PROGRAM 'cat'", "runs a program")]
    [InlineData("SELECT pg_read_file('/etc/passwd')", "reads files on this machine")]
    [InlineData("SELECT pg_catalog.pg_read_binary_file('x')", "reads files on this machine")]
    [InlineData("SELECT lo_export(1, '/tmp/x')", "writes files on this machine")]
    [InlineData("SELECT * FROM dblink('host=x', 'SELECT 1') AS t(a int)", "connects to another server")]
    [InlineData("SELECT pg_terminate_backend(42)", "controls the server")]
    [InlineData("SELECT query_to_xml('DELETE FROM t', true, false, '')", "runs SQL given as text")]
    public void Asks_first_and_is_refused_read_only(string sql, string what)
    {
        Assert.Contains(SqlSafety.Changes([sql]), c => c.What.Contains(what));
        Assert.Contains(SqlSafety.ReadOnlyViolations([sql]), c => c.What.Contains(what));
    }

    [Theory]
    [InlineData("SELECT * FROM books")]
    [InlineData("COPY (SELECT 1) TO STDOUT")]
    [InlineData("INSERT INTO books VALUES (1)")]
    [InlineData("VACUUM books")]
    [InlineData("SET work_mem = '64MB'")]
    [InlineData("SELECT 'pg_read_file' AS just_text")]
    public void Ordinary_statements_do_not_ask(string sql) => Assert.Empty(SqlSafety.Changes([sql]));

    [Theory]
    [InlineData("RESET ALL", "transaction or session mode")]
    [InlineData("RESET ROLE", "transaction or session mode")]
    [InlineData("SELECT set_config('statement_timeout', '0', false)", "changes a setting")]
    public void Read_only_also_refuses_changes_to_the_session(string sql, string what) =>
        Assert.Contains(SqlSafety.ReadOnlyViolations([sql]), c => c.What.Contains(what));

    [Fact]
    public async Task Sql_without_a_terminal_refuses_file_access_unless_yes()
    {
        var run = await TestCli.RunAsync("sql", "postgresql://localhost:1/x", "SELECT pg_read_file('/etc/passwd')");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("reads files on this machine", run.All);
        Assert.Contains("Nothing was run", run.All);
    }
}
