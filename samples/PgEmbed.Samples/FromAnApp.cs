using Npgsql;

namespace PgEmbed.Samples;

public class FromAnApp
{
    [Fact]
    public async Task Connects_with_the_connection_string_pgembed_prints()
    {
        await Cli.RunAsync("start", "app", "--version", "17");
        var connstr = await Cli.RunAsync("connstr", "app", "--format", "npgsql");

        Environment.SetEnvironmentVariable("ConnectionStrings__App", connstr.Trim());
        try
        {
            #region sample_app_connection_string
            // set by: export ConnectionStrings__App="$(pgembed connstr app --format npgsql)"
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__App");

            await using var dataSource = NpgsqlDataSource.Create(connectionString!);
            await using var command = dataSource.CreateCommand("SELECT current_database()");
            var database = (string?)await command.ExecuteScalarAsync();
            #endregion

            Assert.Equal("postgres", database);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__App", null);
            await Cli.RunAsync("stop", "app");
        }
    }
}
