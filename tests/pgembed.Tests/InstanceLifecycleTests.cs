using System.Text.Json;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Sql;
using Spectre.Console.Testing;

namespace PgEmbed.Cli.Tests;

/// <summary>
/// The real thing: downloads Postgres from Maven Central (once; the cache is kept between runs), starts
/// an instance, runs SQL against it and removes it. Tests in one class run one after another.
/// </summary>
public class InstanceLifecycleTests : IAsyncLifetime
{
    private const string Version = "17";
    private readonly string _name = TestCache.UniqueName("t");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    // whatever happens in a test, the instance and its data go
    public async ValueTask DisposeAsync() => await TestCli.RunAsync("rm", _name, "--force", "--yes");

    [Fact]
    public async Task Start_query_stop_and_remove()
    {
        // port 0: a free port, so tests running in parallel never race for 5432
        var start = await TestCli.RunAsync("start", _name, "--version", Version, "--port", "0", "--json");
        Assert.True(start.ExitCode == 0, start.All);
        using var started = JsonDocument.Parse(start.Out);
        var port = started.RootElement.GetProperty("port").GetInt32();
        Assert.False(started.RootElement.GetProperty("attached").GetBoolean());

        // a second start attaches to the running server
        var again = await TestCli.RunAsync("start", _name, "--json");
        using var attached = JsonDocument.Parse(again.Out);
        Assert.True(attached.RootElement.GetProperty("attached").GetBoolean());
        Assert.Equal(port, attached.RootElement.GetProperty("port").GetInt32());

        var connstr = await TestCli.RunAsync("connstr", _name, "--format", "uri");
        Assert.Equal($"postgresql://postgres@localhost:{port}/postgres", connstr.Out.Trim());
        var formats = await TestCli.RunAsync("connstr", _name);
        Assert.Contains("jdbc:postgresql://", formats.Console);
        Assert.Contains("host=localhost", formats.Console);

        // a script with a dollar-quoted body, run statement by statement
        var script = await TestCli.RunAsync("sql", _name, "--json", "--yes",
            "CREATE TABLE books(id int, title text); " +
            "CREATE FUNCTION one() RETURNS int AS $$ SELECT 1; $$ LANGUAGE sql; " +
            "INSERT INTO books VALUES (one(), 'a;b'), (2, NULL); " +
            "SELECT id, title FROM books ORDER BY id");
        Assert.True(script.ExitCode == 0, script.All);
        using var results = JsonDocument.Parse(script.Out);
        var all = results.RootElement.GetProperty("results");
        Assert.Equal(4, all.GetArrayLength());
        Assert.Equal(2, all[2].GetProperty("rowsAffected").GetInt32());
        var rows = all[3].GetProperty("rows");
        Assert.Equal("a;b", rows[0][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[1][1].ValueKind);

        // json values come back nested, not as strings
        var nested = await TestCli.RunAsync("sql", _name, "SELECT '{\"a\": [1, 2]}'::jsonb AS doc", "--json");
        using (var doc = JsonDocument.Parse(nested.Out))
        {
            var value = doc.RootElement.GetProperty("results")[0].GetProperty("rows")[0][0];
            Assert.Equal(2, value.GetProperty("a")[1].GetInt32());
        }

        // a capped table stops reading (the query is cancelled), and the next statement still runs
        var capped = await TestCli.RunAsync("sql", _name, "SELECT g FROM generate_series(1, 1000000) g; SELECT 'after' AS next",
            "--format", "table", "--max-rows", "3");
        Assert.True(capped.ExitCode == 0, capped.All);
        Assert.Contains("first 3 rows; more not shown", capped.Console);
        Assert.Contains("after", capped.Console);

        // inside a transaction it skips the rest instead, since a cancel would abort the transaction
        var inTransaction = await TestCli.RunAsync("sql", _name,
            "BEGIN; SELECT g FROM generate_series(1, 100000) g; SELECT 'same transaction' AS next; COMMIT",
            "--format", "table", "--max-rows", "3");
        Assert.True(inTransaction.ExitCode == 0, inTransaction.All);
        Assert.Contains("same transaction", inTransaction.Console);

        var csv = await TestCli.RunAsync("sql", _name, "SELECT title FROM books WHERE id = 1", "--format", "csv");
        Assert.Equal("title\na;b", csv.Out.Trim().ReplaceLineEndings("\n"));

        // redirected output gets psql-style aligned text, one row per line
        var piped = await TestCli.RunRedirectedAsync("sql", _name, "SELECT id, title FROM books ORDER BY id");
        Assert.Equal(" id | title\n----+-------\n  1 | a;b\n  2 |\n(2 rows)",
            piped.Out.TrimEnd().ReplaceLineEndings("\n"));

        var failed = await TestCli.RunAsync("sql", _name, "SELECT titel FROM books");
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("column \"titel\" does not exist", failed.Console);
        Assert.Contains("LINE 1: SELECT titel FROM books", failed.Console);

        var status = await TestCli.RunAsync("status", _name, "--json");
        using var statuses = JsonDocument.Parse(status.Out);
        Assert.Equal("running", statuses.RootElement[0].GetProperty("state").GetString());

        var stop = await TestCli.RunAsync("stop", _name);
        Assert.Equal(0, stop.ExitCode);
        var stopped = await TestCli.RunAsync("status", _name, "--json");
        using var after = JsonDocument.Parse(stopped.Out);
        Assert.Equal("stopped", after.RootElement[0].GetProperty("state").GetString());

        // the data survives a restart
        var restart = await TestCli.RunAsync("start", _name);
        Assert.Equal(0, restart.ExitCode);
        var count = await TestCli.RunAsync("sql", _name, "SELECT count(*) AS n FROM books", "--format", "csv");
        Assert.Equal("n\n2", count.Out.Trim().ReplaceLineEndings("\n"));

        var rm = await TestCli.RunAsync("rm", _name);
        Assert.NotEqual(0, rm.ExitCode); // a running instance needs --force
        var forced = await TestCli.RunAsync("rm", _name, "--force", "--yes");
        Assert.Equal(0, forced.ExitCode);
        var gone = await TestCli.RunAsync("sql", _name, "SELECT 1");
        Assert.Equal(1, gone.ExitCode);
    }

    [Fact]
    public async Task Interactive_prompt_confirms_changes_and_describes_tables()
    {
        var start = await TestCli.RunAsync("start", _name, "--version", Version, "--port", "0", "--json");
        Assert.True(start.ExitCode == 0, start.All);

        var console = new TestConsole().Width(250).Interactive();
        console.Input.PushTextWithEnter("y"); // create the table
        console.Input.PushTextWithEnter("n"); // but not delete its rows
        var services = new CliServices(console, new StringWriter(), new StringWriter(), new HttpClient());
        var cli = new CliContext(new GlobalSettings(), services);
        var (target, label) = await Commands.SqlCommand.ResolveTargetAsync(cli, _name, null, null, TestContext.Current.CancellationToken);

        var reader = new ScriptReader(
            "CREATE TABLE books(id int PRIMARY KEY, title text);",
            "INSERT INTO books VALUES (1, 'a'), (2, 'b');",
            "\\dt",
            "\\d books",
            "DELETE FROM books;",
            "SELECT count(*) AS n FROM books;",
            "\\x",
            "SELECT title FROM books WHERE id = 2;",
            "\\q",
            "SELECT 'never reached';");
        var exit = await new SqlShell(cli, target, label, new SqlOutput { StreamJson = true }, yes: false)
            .RunAsync(reader, TestContext.Current.CancellationToken);

        var output = console.Output;
        Assert.Equal(0, exit);
        Assert.Contains("changes the schema", output);
        Assert.Contains("2 rows affected", output);
        Assert.Contains("books", output);                     // \dt
        Assert.Contains("books_pkey", output);                // \d books: its index
        Assert.Contains("deletes every row of books (no WHERE)", output);
        Assert.Contains("Not run.", output);
        Assert.Contains("RECORD 1", output);                  // \x
        Assert.DoesNotContain("never reached", output);
        Assert.True(reader.SawBooks);                         // completion learned about the new table

        // the DELETE was declined: both rows are still there
        var count = await TestCli.RunAsync("sql", _name, "SELECT count(*) AS n FROM books", "--format", "csv");
        Assert.Equal("n\n2", count.Out.Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Sql_takes_a_connection_string_and_a_schema()
    {
        var start = await TestCli.RunAsync("start", _name, "--version", Version, "--port", "0", "--json");
        using var started = JsonDocument.Parse(start.Out);
        var port = started.RootElement.GetProperty("port").GetInt32();

        var setup = await TestCli.RunAsync("sql", _name, "--yes",
            "CREATE SCHEMA sales; CREATE TABLE sales.orders(id int); INSERT INTO sales.orders VALUES (1), (2), (3)");
        Assert.True(setup.ExitCode == 0, setup.All);

        var bySchema = await TestCli.RunAsync("sql", _name, "--schema", "sales", "SELECT count(*) AS n FROM orders", "--format", "csv");
        Assert.Equal("n\n3", bySchema.Out.Trim().ReplaceLineEndings("\n"));

        foreach (var format in new[] { "uri", "libpq", "jdbc", "npgsql" })
        {
            var connection = (await TestCli.RunAsync("connstr", _name, "--format", format, "--schema", "sales")).Out.Trim();
            var run = await TestCli.RunAsync("sql", connection, "SELECT count(*) AS n FROM orders", "--format", "csv");
            Assert.True(run.ExitCode == 0, $"{format}: {connection}\n{run.All}");
            Assert.Equal("n\n3", run.Out.Trim().ReplaceLineEndings("\n"));
        }

        var uri = $"postgresql://postgres@localhost:{port}/postgres";
        var withDb = await TestCli.RunAsync("sql", uri, "--db", "template1", "SELECT current_database() AS db", "--format", "csv");
        Assert.Equal("db\ntemplate1", withDb.Out.Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Schema_and_backslash_d_describe_tables_the_same_way()
    {
        var start = await TestCli.RunAsync("start", _name, "--version", Version, "--port", "0");
        Assert.True(start.ExitCode == 0, start.All);
        var setup = await TestCli.RunAsync("sql", _name, "--yes", """
            CREATE SCHEMA sales;
            CREATE TABLE sales.customers (id int PRIMARY KEY, name text NOT NULL);
            CREATE TABLE sales.orders (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                customer_id int NOT NULL REFERENCES sales.customers (id),
                total numeric(10, 2) NOT NULL DEFAULT 0 CHECK (total >= 0),
                total_with_tax numeric GENERATED ALWAYS AS (total * 1.2) STORED,
                note text);
            CREATE INDEX orders_customer ON sales.orders (customer_id);
            COMMENT ON TABLE sales.orders IS 'One row per order';
            """);
        Assert.True(setup.ExitCode == 0, setup.All);

        // pgembed schema: one structured document
        var described = await TestCli.RunAsync("schema", _name, "orders", "--schema", "sales", "--json");
        Assert.True(described.ExitCode == 0, described.All);
        using var json = JsonDocument.Parse(described.Out);
        var table = json.RootElement;
        Assert.Equal("sales", table.GetProperty("schema").GetString());
        Assert.Equal("One row per order", table.GetProperty("comment").GetString());
        var columns = table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.True(columns["id"].GetProperty("primaryKey").GetBoolean());
        Assert.Equal("always", columns["id"].GetProperty("identity").GetString());
        Assert.False(columns["customer_id"].GetProperty("nullable").GetBoolean());
        Assert.Equal("0", columns["total"].GetProperty("default").GetString());
        Assert.Contains("1.2", columns["total_with_tax"].GetProperty("generated").GetString());
        Assert.Contains(table.GetProperty("indexes").EnumerateArray(), i => i.GetProperty("name").GetString() == "orders_customer");
        var foreignKey = Assert.Single(table.GetProperty("constraints").EnumerateArray(), c => c.GetProperty("kind").GetString() == "foreign key");
        Assert.Equal("customers", foreignKey.GetProperty("references").GetString());
        Assert.Contains(table.GetProperty("constraints").EnumerateArray(), c => c.GetProperty("kind").GetString() == "check");

        // a pattern describes every match; no table lists them
        var both = await TestCli.RunAsync("schema", _name, "sales.*", "--json");
        Assert.True(both.ExitCode == 0, both.All);
        using (var many = JsonDocument.Parse(both.Out)) Assert.Equal(2, many.RootElement.GetArrayLength());
        var listed = await TestCli.RunAsync("schema", _name, "--schema", "sales", "--json");
        using (var list = JsonDocument.Parse(listed.Out)) Assert.Equal(2, list.RootElement.GetArrayLength());

        // \d as one-shot SQL shows the same description, here as psql-style text
        var text = await TestCli.RunRedirectedAsync("sql", _name, "\\d sales.orders");
        Assert.True(text.ExitCode == 0, text.All);
        Assert.Contains("sales.orders (table", text.Out);
        Assert.Contains("identity (always)", text.Out);
        Assert.Contains("orders_customer", text.Out);
        Assert.Contains("FOREIGN KEY (customer_id) REFERENCES", text.Out);

        var tables = await TestCli.RunRedirectedAsync("sql", _name, "\\dt sales.*");
        Assert.Equal(0, tables.ExitCode);

        var sessionOnly = await TestCli.RunAsync("sql", _name, "\\c postgres");
        Assert.Equal(1, sessionOnly.ExitCode);
        Assert.Contains("interactive prompt", sessionOnly.Console);

        var missing = await TestCli.RunAsync("schema", _name, "nope");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("no table or view named 'nope'", missing.Console);
    }

    /// <summary>Feeds the shell fixed lines, as if typed.</summary>
    private sealed class ScriptReader(params string[] lines) : ILineReader
    {
        private readonly Queue<string> _lines = new(lines);
        public bool SawBooks { get; private set; }

        public Task<LineResult?> ReadAsync(string prompt, Func<SqlCatalog> catalog)
        {
            SawBooks |= catalog().Relations.Any(r => r.Name == "books");
            return Task.FromResult(_lines.TryDequeue(out var line) ? new LineResult(true, line) : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
