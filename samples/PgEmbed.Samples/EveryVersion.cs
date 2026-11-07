using MysticMind.PostgresEmbed;
using Npgsql;

namespace PgEmbed.Samples;

public class EveryVersion
{
    #region sample_each_version
    // `pgembed start ci --version 16,17,18` runs ci-16, ci-17 and ci-18;
    // without it, each test starts its own
    [Theory]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    public async Task Runs_on_every_supported_major(string major)
    {
        await using var server = new PgServer(new PgServerOptions
        {
            PgVersion = major,
            InstanceName = $"ci-{major}"
        });
        await server.StartAsync(TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith($"{major}.", connection.PostgreSqlVersion.ToString());
    }
    #endregion
}
