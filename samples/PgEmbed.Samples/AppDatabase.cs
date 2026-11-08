using MysticMind.PostgresEmbed;
using Npgsql;

namespace PgEmbed.Samples;

#region sample_xunit_fixture
// The "app" instance for a test run. If `pgembed start app` already runs it,
// the tests attach in milliseconds and leave it running; otherwise they start
// it, and stop it when they finish.
public sealed class AppDatabase : IAsyncLifetime
{
    private readonly PgServer _server = new(new PgServerOptions
    {
        PgVersion = "17",
        InstanceName = "app"
    });

    public string ConnectionString => _server.ConnectionString;

    public async ValueTask InitializeAsync() => await _server.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
#endregion

#region sample_xunit_test
public class BookTests(AppDatabase database) : IClassFixture<AppDatabase>
{
    [Fact]
    public async Task Stores_a_book()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS books(id int PRIMARY KEY, title text);
            INSERT INTO books VALUES (1, 'Dune') ON CONFLICT DO NOTHING;
            SELECT title FROM books WHERE id = 1;
            """, connection);

        var title = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Dune", title);
    }
}
#endregion
