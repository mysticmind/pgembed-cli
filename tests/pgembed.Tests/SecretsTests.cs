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
