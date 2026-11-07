using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MysticMind.PostgresEmbed;
using Npgsql;
using PgEmbed.Cli.Commands;
using PgEmbed.Cli.Core;
using PgEmbed.Cli.Infrastructure;
using PgEmbed.Cli.Sql;

namespace PgEmbed.Cli.Mcp;

/// <summary>How the MCP server guards the databases it reaches.</summary>
internal sealed record McpPolicy(bool ReadOnly, bool NoConfirm, bool AllowExternalWrites, IReadOnlyDictionary<string, string>? Connections = null)
{
    /// <summary>Servers the assistant uses by name, from --connection: their connection strings never reach it.</summary>
    public IReadOnlyDictionary<string, string> Named { get; } = Connections ?? new Dictionary<string, string>();
}

/// <summary>
/// The MCP tools: thin front ends over the same core as the commands, so a tool and its command behave the
/// same. Every tool maps to a command or option (see the spec's table). Nothing here writes to standard
/// output, which carries the protocol.
/// </summary>
internal sealed class PgEmbedTools(CliContext cli, McpPolicy policy) : IAsyncDisposable
{
    public const int DefaultMaxRows = 100;
    public const int MaxMaxRows = 10_000;
    public const int MaxValueLength = 2_000;

    // one pooled data source per target: a tool call reuses an open connection
    private readonly Dictionary<string, NpgsqlDataSource> _sources = [];
    private readonly SemaphoreSlim _sourcesLock = new(1, 1);
    private readonly List<PgServer> _started = [];

    // ---------- versions and extensions ----------

    [McpServerTool(Name = "list_versions", ReadOnly = true, Idempotent = true)]
    [Description("PostgreSQL versions published for this machine: the newest of each major, or every version of one major.")]
    public async Task<object> ListVersions(
        [Description("A major, for example 17, to list all of its versions")] string? major = null,
        CancellationToken ct = default)
    {
        var list = await cli.Catalog.GetAsync(cli.Platform, false, ct);
        var newestFirst = list.Versions.OrderByDescending(v => v, VersionOrder.Comparer).ToList();
        var stale = list.FromStaleCache ? $"Maven was unreachable; versions cached on {list.FetchedAt:yyyy-MM-dd}" : null;

        if (major is not null)
        {
            return new McpVersions(cli.Platform.RuntimeIdentifier, major, Versions: newestFirst.Where(v => VersionOrder.Major(v) == major).ToList(), Warning: stale);
        }

        return new McpVersions(cli.Platform.RuntimeIdentifier,
            Majors: newestFirst.GroupBy(VersionOrder.Major).Select(g => new VersionMajor(g.Key, g.First(), g.Count())).ToList(),
            Warning: stale);
    }

    [McpServerTool(Name = "install_version", Idempotent = true)]
    [Description("Download, verify and extract a PostgreSQL version ahead of time, so a later start_instance is fast.")]
    public async Task<object> InstallVersion(
        [Description("Exact (17.6.0), major (17), minor (17.6) or latest")] string version,
        CancellationToken ct = default)
    {
        var exact = await cli.Catalog.ResolveAsync(version, cli.Platform, false, ct);
        await using var server = new PgServer(new PgServerOptions
        {
            PgVersion = exact, BinariesDir = cli.Workspace.BinariesDir, MavenRepo = cli.MavenRepo,
            HttpClient = cli.Services.Http, Logger = cli.Logger
        });
        await server.InstallAsync(ct);
        return new InstalledVersion(exact, cli.Platform.RuntimeIdentifier, server.InstallDir);
    }

    [McpServerTool(Name = "list_extensions", ReadOnly = true, Idempotent = true)]
    [Description("The extensions CREATE EXTENSION can use in a PostgreSQL version: name, default version, what it requires, description.")]
    public async Task<object> ListExtensions(
        [Description("Exact (17.6.0), major (17), minor (17.6) or latest")] string version,
        CancellationToken ct = default)
    {
        var exact = await cli.Catalog.ResolveAsync(version, cli.Platform, false, ct);
        await using var server = new PgServer(new PgServerOptions
        {
            PgVersion = exact, BinariesDir = cli.Workspace.BinariesDir, MavenRepo = cli.MavenRepo,
            HttpClient = cli.Services.Http, Logger = cli.Logger
        });
        return new McpExtensions(exact, await server.GetAvailableExtensionsAsync(ct));
    }

    // ---------- instances ----------

    [McpServerTool(Name = "start_instance", Idempotent = true)]
    [Description("Start a named PostgreSQL instance in the background, creating it on first use (give a version then). " +
                 "If it is already running, attaches to it. It keeps running after this conversation, and keeps its port. " +
                 "Returns the connection string.")]
    public async Task<object> StartInstance(
        [Description("Instance name: letters, digits, '.', '_' and '-'")] string name,
        [Description("Version for a new instance: exact (17.6.0), major (17), minor (17.6) or latest. Not needed to restart an existing one")] string? version = null,
        [Description("Port; default 5432 when free, else any free port, remembered for later starts")] int? port = null,
        CancellationToken ct = default)
    {
        var result = await cli.Instances.StartAsync(name, version, port, new Dictionary<string, string>(), [],
            keepRunningAfterExit: true, refresh: false, ct);
        var serverPort = result.Server.PgPort;
        // never disposed: disposing stops a server this process started, and it is meant to outlive this process
        lock (_started) _started.Add(result.Server);

        return new StartedInstance(name, result.Record.Version, serverPort, result.Attached,
            InstanceManager.ConnectionString(serverPort), Connections.Format("uri", serverPort, InstanceManager.DefaultDatabase, null));
    }

    [McpServerTool(Name = "list_instances", ReadOnly = true, Idempotent = true)]
    [Description("Every pgembed instance on this machine (name, version, port, state, uptime, data size), and the names of the other servers this MCP server was given (connections), usable as a target.")]
    public async Task<object> ListInstances(CancellationToken ct = default)
    {
        var views = new List<InstanceSummary>();
        foreach (var name in cli.Registry.Names().Where(cli.Instances.Exists))
        {
            var v = await cli.Instances.ViewAsync(name, ct);
            var running = v.Status.State != PgInstanceState.Stopped;
            views.Add(new InstanceSummary(v.Name, v.Version ?? v.DataMajor, v.Status.State.ToString().ToLowerInvariant(), v.Port,
                running && v.Status.StartedAt is { } at ? (long)(DateTimeOffset.UtcNow - at).TotalSeconds : null, v.DataBytes));
        }

        return new McpInstances(views, policy.Named.Keys.Order().ToList());
    }

    [McpServerTool(Name = "stop_instance", Idempotent = true)]
    [Description("Stop a running instance cleanly. Its data is kept; start_instance starts it again.")]
    public async Task<object> StopInstance([Description("Instance name, as list_instances shows it")] string name, CancellationToken ct = default)
    {
        RequireInstance(name);
        var stopped = await cli.Instances.StopAsync(name, ct);
        return new McpStopped(name, stopped, stopped ? $"Stopped {name}." : $"{name} was not running.");
    }

    [McpServerTool(Name = "restart_instance")]
    [Description("Stop an instance and start it again with the same version, port and settings.")]
    public async Task<object> RestartInstance([Description("Instance name, as list_instances shows it")] string name, CancellationToken ct = default)
    {
        RequireInstance(name);
        await cli.Instances.StopAsync(name, ct);
        return await StartInstance(name, null, null, ct);
    }

    [McpServerTool(Name = "kill_instance", Destructive = true)]
    [Description("Force-kill an instance's server processes, as a last resort when stop_instance does not work. Asks for confirmation.")]
    public async Task<CallToolResult> KillInstance(
        McpServer server, [Description("Instance name, as list_instances shows it")] string name,
        [Description("Set to true, after the user agreed, when the client cannot ask the user itself")] bool confirm = false,
        CancellationToken ct = default)
    {
        RequireInstance(name);
        if (await RefusalAsync(server, $"Force-kill instance '{name}'? Sessions are cut off without a clean shutdown.", [], confirm, ct) is { } refusal)
        {
            return refusal;
        }

        var killed = await cli.Instances.KillAsync(name, ct);
        return Text(killed ? $"Killed {name}." : $"{name} was not running.");
    }

    [McpServerTool(Name = "remove_instance", Destructive = true)]
    [Description("Delete an instance and all of its data, stopping it first if it runs. Asks for confirmation.")]
    public async Task<CallToolResult> RemoveInstance(
        McpServer server, [Description("Instance name, as list_instances shows it")] string name,
        [Description("Set to true, after the user agreed, when the client cannot ask the user itself")] bool confirm = false,
        CancellationToken ct = default)
    {
        RequireInstance(name);
        if (await RefusalAsync(server, $"Delete instance '{name}' and all of its data? This cannot be undone.", [], confirm, ct) is { } refusal)
        {
            return refusal;
        }

        await cli.Instances.RemoveAsync(name, force: true, ct);
        return Text($"Removed {name} and its data.");
    }

    [McpServerTool(Name = "get_connection_string", ReadOnly = true, Idempotent = true)]
    [Description("The connection string of a running instance.")]
    public async Task<object> GetConnectionString(
        [Description("Instance name, as list_instances shows it")] string name,
        [Description("npgsql (default), uri, libpq, jdbc or env")] string format = "npgsql",
        [Description("Database (default: postgres)")] string? database = null,
        [Description("Schema for unqualified names (sets the search path)")] string? schema = null,
        CancellationToken ct = default)
    {
        if (!Connections.FormatNames.Contains(format)) throw new McpException($"format is one of: {string.Join(", ", Connections.FormatNames)}");
        var port = await RunningPortAsync(name, ct);
        return new McpConnectionString(name, format, Connections.Format(format, port, database ?? InstanceManager.DefaultDatabase, schema));
    }

    [McpServerTool(Name = "get_logs", ReadOnly = true, Idempotent = true)]
    [Description("The last lines of an instance's server log: startup messages, errors, slow statements.")]
    public object GetLogs([Description("Instance name, as list_instances shows it")] string name, [Description("Lines from the end (default 50, at most 1000)")] int lines = 50)
    {
        RequireInstance(name);
        var file = cli.Instances.LogFile(name);
        var tail = File.Exists(file) ? LogsCommand.ReadShared(file).TakeLast(Math.Clamp(lines, 1, 1000)).ToList() : [];
        return new McpLogs(name, tail);
    }

    [McpServerTool(Name = "doctor", ReadOnly = true, Idempotent = true)]
    [Description("Check that PostgreSQL can run on this machine: platform, user, Maven reachable, cache writable, folder usable, port free.")]
    public async Task<object> Doctor([Description("Port to check is free (default 5432)")] int port = 5432, CancellationToken ct = default)
    {
        var checks = await DoctorCommand.RunChecksAsync(cli, null, port, ct);
        return new McpDoctor(checks.All(c => c.Outcome != DoctorCommand.Outcome.Failed),
            checks.Select(c => new DoctorCheckOutput(c.Name, c.Outcome.ToString().ToLowerInvariant(), c.Detail)).ToList());
    }

    // ---------- SQL ----------

    [McpServerTool(Name = "run_sql")]
    [Description("Run SQL against an instance (by name) or any server (by connection string). Each statement returns its rows " +
                 "(at most max_rows; the result says when it was truncated) or its affected rows. json/jsonb values come back as JSON. " +
                 "Statements that change the schema or existing rows ask for confirmation first. With read_only, anything that writes is refused " +
                 "and the SQL runs in a read-only transaction. Errors include the position, detail and hint, to correct the SQL. " +
                 "To page through a large result, use ORDER BY with a keyset (WHERE id > last) or LIMIT/OFFSET.")]
    public async Task<CallToolResult> RunSql(
        McpServer server,
        [Description("Instance name, a connection name the server was given (list_instances shows them), or a connection string. Never put a password in a tool argument: it would be visible in the conversation; use a connection name, or PGPASSWORD or ~/.pgpass on the server")] string target,
        [Description("SQL: one or more statements")] string sql,
        [Description("Database (default: postgres for an instance)")] string? database = null,
        [Description("Schema for unqualified names (sets the search path)")] string? schema = null,
        [Description("Rows returned per statement (default 100, at most 10000)")] int max_rows = DefaultMaxRows,
        [Description("Refuse anything that writes, and run in a read-only transaction")] bool read_only = false,
        [Description("Set to true, after the user agreed to the changes, when the client cannot ask the user itself")] bool confirm = false,
        CancellationToken ct = default)
    {
        var statements = SqlRunner.Split(sql);
        var readOnly = read_only || policy.ReadOnly || IsExternal(target) && !policy.AllowExternalWrites;

        if (readOnly)
        {
            var violations = SqlSafety.ReadOnlyViolations(statements);
            if (violations.Count > 0)
            {
                var why = policy.ReadOnly ? "this server runs read-only"
                    : read_only ? "read_only was requested"
                    : "the target is a server pgembed did not start (pgembed mcp --allow-external-writes allows writes there)";
                return Error($"Refused, since {why}. Nothing was run.\n" + string.Join("\n", violations.Select(v => $"- {Abbreviate(v.Statement)}: {v.What}")));
            }
        }
        else
        {
            var changes = SqlSafety.Changes(statements);
            if (changes.Count > 0 && await RefusalAsync(server, "Run SQL that changes the database?",
                    changes.Select(c => $"{Abbreviate(c.Statement)}: {c.What}").ToList(), confirm, ct) is { } refusal)
            {
                return refusal;
            }
        }

        var collector = new CollectingSink(Math.Clamp(max_rows, 1, MaxMaxRows));
        await using var connection = await OpenAsync(target, database, schema, ct);
        await using var transaction = readOnly ? await connection.BeginTransactionAsync(ct) : null;
        if (transaction is not null)
        {
            await using var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await setReadOnly.ExecuteNonQueryAsync(ct);
        }

        foreach (var statement in statements)
        {
            if (!await SqlExecutor.RunAsync(connection, statement, [], null, collector.MaxRows, collector, null, ct)) break;
        }

        if (transaction is not null) await transaction.RollbackAsync(ct);
        return collector.ToResult();
    }

    [McpServerTool(Name = "check_sql", ReadOnly = true, Idempotent = true)]
    [Description("Check SQL syntax without a server, and summarize each statement: its kind, the tables it reads, writes or defines, " +
                 "the functions it calls, and whether run_sql would ask for confirmation.")]
    public object CheckSql([Description("SQL: one or more statements")] string sql) => SqlAnalysis.Check(sql);

    [McpServerTool(Name = "format_sql", ReadOnly = true, Idempotent = true)]
    [Description("Format SQL with the PostgreSQL parser, keeping comments. No server needed.")]
    public object FormatSql([Description("SQL: one or more statements; comments are kept")] string sql)
    {
        var (formatted, error) = SqlAnalysis.Format(sql);
        return error is null ? new FormattedSql(formatted) : throw new McpException(SyntaxMessage(sql, error));
    }

    [McpServerTool(Name = "explain_query")]
    [Description("The query plan, as JSON (EXPLAIN FORMAT JSON). With analyze, the statement really runs, inside a transaction that is " +
                 "always rolled back, so its changes never persist (sequences still advance); actual times and row counts are included.")]
    public async Task<CallToolResult> ExplainQuery(
        [Description("Instance name, a connection name the server was given (list_instances shows them), or a connection string. Never put a password in a tool argument: it would be visible in the conversation; use a connection name, or PGPASSWORD or ~/.pgpass on the server")] string target,
        [Description("One statement")] string sql,
        [Description("Run it and include actual times and rows (rolled back afterwards)")] bool analyze = false,
        [Description("Database (default: postgres for an instance)")] string? database = null,
        [Description("Schema for unqualified names")] string? schema = null,
        CancellationToken ct = default)
    {
        var statements = SqlRunner.Split(sql);
        if (statements.Count != 1) return Error("explain_query takes one statement.");
        var readOnly = policy.ReadOnly || IsExternal(target) && !policy.AllowExternalWrites;
        if (analyze && readOnly && SqlSafety.ReadOnlyViolations(statements) is { Count: > 0 } violations)
        {
            return Error($"Refused: with analyze the statement runs, and it {violations[0].What}, but this target is read-only.");
        }

        await using var connection = await OpenAsync(target, database, schema, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (readOnly)
        {
            await using var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await setReadOnly.ExecuteNonQueryAsync(ct);
        }

        var options = analyze ? "FORMAT JSON, ANALYZE, BUFFERS" : "FORMAT JSON";
        try
        {
            await using var command = new NpgsqlCommand($"EXPLAIN ({options}) {statements[0].TrimEnd(';')}", connection, transaction);
            var plan = (string?)await command.ExecuteScalarAsync(ct) ?? "[]";
            using var document = JsonDocument.Parse(plan);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = plan }],
                StructuredContent = Json.ToElement(new McpExplain(document.RootElement.Clone(), analyze, true))
            };
        }
        catch (PostgresException ex)
        {
            return Error(ErrorMessage(new StatementError(statements[0], ex.SqlState, ex.MessageText, ex.Position, ex.Detail, ex.Hint)));
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [McpServerTool(Name = "describe_schema", ReadOnly = true, Idempotent = true)]
    [Description("The tables, views and foreign tables in a database, with row estimates and sizes.")]
    public async Task<object> DescribeSchema(
        [Description("Instance name, a connection name the server was given (list_instances shows them), or a connection string. Never put a password in a tool argument: it would be visible in the conversation; use a connection name, or PGPASSWORD or ~/.pgpass on the server")] string target,
        [Description("Database (default: postgres for an instance)")] string? database = null,
        [Description("Only this schema")] string? schema = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(target, database, null, ct);
        return new McpRelations(await SchemaReader.ListAsync(connection, [.. SchemaReader.Tables, .. SchemaReader.Views, "f"], null, schema, ct));
    }

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true)]
    [Description("One table or view in full: columns (type, nullability, default, identity, generated, primary key), indexes, " +
                 "and constraints with foreign key targets.")]
    public async Task<object> DescribeTable(
        [Description("Instance name, a connection name the server was given (list_instances shows them), or a connection string. Never put a password in a tool argument: it would be visible in the conversation; use a connection name, or PGPASSWORD or ~/.pgpass on the server")] string target,
        [Description("Table or view, optionally schema-qualified (sales.orders)")] string table,
        [Description("Database (default: postgres for an instance)")] string? database = null,
        [Description("Schema where an unqualified name is looked up")] string? schema = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(target, database, schema, ct);
        return await SchemaReader.DescribeAsync(connection, table, ct)
               ?? throw new McpException($"There is no table or view named '{table}'. describe_schema lists them.");
    }

    // ---------- helpers ----------

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values) await source.DisposeAsync();
        _sources.Clear();
    }

    // a server pgembed did not start: a named connection or a connection string
    private bool IsExternal(string target) => policy.Named.ContainsKey(target) || Connections.IsConnectionString(target);

    private void RequireInstance(string name)
    {
        if (!cli.Instances.Exists(name))
        {
            throw new McpException($"There is no instance named '{name}'. list_instances shows them; start_instance creates one.");
        }
    }

    private async Task<int> RunningPortAsync(string name, CancellationToken ct)
    {
        RequireInstance(name);
        var status = await cli.Instances.StatusAsync(name, ct);
        return status.State != PgInstanceState.Stopped && status.Port is { } port
            ? port
            : throw new McpException($"'{name}' is not running. Start it with start_instance.");
    }

    private async Task<NpgsqlConnection> OpenAsync(string target, string? database, string? schema, CancellationToken ct)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            // a named connection resolves here, on the server: the assistant only ever sees its name
            var resolved = policy.Named.TryGetValue(target, out var named) ? named : target;
            (builder, _) = await SqlCommand.ResolveTargetAsync(cli, resolved, database, schema, ct);
        }
        catch (PostgresEmbedException ex)
        {
            throw new McpException(System.Text.RegularExpressions.Regex.Replace(ex.Message, @"Start it with: pgembed start (\S+)", "Start it with start_instance."));
        }

        builder.Pooling = true;
        builder.ApplicationName ??= "pgembed-mcp";
        var key = builder.ConnectionString;

        NpgsqlDataSource source;
        await _sourcesLock.WaitAsync(ct);
        try
        {
            if (!_sources.TryGetValue(key, out source!)) _sources[key] = source = NpgsqlDataSource.Create(builder);
        }
        finally
        {
            _sourcesLock.Release();
        }

        return await source.OpenConnectionAsync(ct);
    }

    /// <summary>
    /// Null to go ahead; otherwise the result to return instead. With a client that can ask the user (elicitation),
    /// the user is asked. Otherwise the call runs only when repeated with confirm: true, which slows the assistant
    /// down but is not a human check: that is the client's own approval of tool calls.
    /// </summary>
    private async Task<CallToolResult?> RefusalAsync(McpServer server, string question, IReadOnlyList<string> items, bool confirm, CancellationToken ct)
    {
        if (policy.NoConfirm) return null;

        var details = items.Count == 0 ? "" : "\n" + string.Join("\n", items.Select(i => "- " + i));
        if (server.ClientCapabilities?.Elicitation is not null)
        {
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = question + details,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["confirm"] = new ElicitRequestParams.BooleanSchema { Description = "Go ahead", Default = false }
                    }
                }
            }, ct);

            var agreed = answer.IsAccepted && answer.Content is { } content && content.TryGetValue("confirm", out var value)
                         && value.ValueKind == JsonValueKind.True;
            return agreed ? null : Error("The user did not agree. Nothing was run.");
        }

        return confirm
            ? null
            : Error($"{question}{details}\nNothing was run. Ask the user; if they agree, call again with confirm: true.");
    }

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };

    internal static string ErrorMessage(StatementError e)
    {
        var console = new StringWriter();
        var ansi = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(console)
        });
        ansi.Profile.Width = 1000;
        Renderers.ReportError(ansi, e);
        return $"{(e.SqlState is null ? "" : $"[{e.SqlState}] ")}{console.ToString().TrimEnd()}\nin: {Abbreviate(e.Statement)}";
    }

    private static string SyntaxMessage(string sql, SyntaxError error) =>
        ErrorMessage(new StatementError(sql, "42601", error.Message, error.Position, null, null));

    private static string Abbreviate(string statement)
    {
        var line = string.Join(' ', statement.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    /// <summary>Collects results for one tool call: structured content plus a compact Markdown rendering.</summary>
    private sealed class CollectingSink(int maxRows) : IResultSink
    {
        private readonly List<Dictionary<string, object?>> _results = [];
        private readonly StringBuilder _text = new();
        private Dictionary<string, object?>? _current;
        private List<object?[]>? _rows;
        private StatementError? _error;
        private bool _shortened;

        public int MaxRows => maxRows;

        public void Begin(string statement, string? title, IReadOnlyList<SqlColumn> columns)
        {
            _rows = [];
            _current = new Dictionary<string, object?>
            {
                ["statement"] = statement,
                ["columns"] = columns.Select(c => c.Name).ToList(),
                ["types"] = columns.Select(c => c.DataType).ToList(),
                ["rows"] = _rows
            };
        }

        public void Row(object?[] values)
        {
            for (var i = 0; i < values.Length; i++) values[i] = Plain(values[i]);
            _rows!.Add(values);
        }

        public void End(StatementSummary summary)
        {
            var result = _current ?? new Dictionary<string, object?> { ["statement"] = summary.Statement };
            if (summary.HasResultSet)
            {
                result["rowCount"] = summary.Rows;
                result["truncated"] = summary.Truncated;
                if (summary.Truncated) result["hint"] = $"Only the first {summary.Rows} rows were read. Page with ORDER BY and a keyset (WHERE id > <last>) or LIMIT/OFFSET, or raise max_rows.";
            }
            else
            {
                result["rowsAffected"] = summary.RowsAffected;
            }

            result["milliseconds"] = Math.Round(summary.Milliseconds, 2);
            _results.Add(result);
            Markdown(result);
            _current = null;
            _rows = null;
        }

        public void Fail(StatementError error)
        {
            _error = error;
            _current = null;
            _rows = null;
        }

        public CallToolResult ToResult()
        {
            if (_shortened) _text.AppendLine($"_Values longer than {MaxValueLength} characters were shortened._");
            var structured = new McpSqlResults(_results, _error is null ? null
                : new McpSqlError(_error.SqlState, _error.Message, _error.Position, _error.Detail, _error.Hint, _error.Statement));

            var text = _error is null ? _text.ToString() : _text + ErrorMessage(_error) + (_results.Count > 0 ? $"\n({_results.Count} statement(s) ran before it.)" : "");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text.Length == 0 ? "OK" : text.TrimEnd() }],
                StructuredContent = Json.ToElement(structured),
                IsError = _error is not null
            };
        }

        private object? Plain(object? value)
        {
            switch (value)
            {
                case SqlJson json:
                    try
                    {
                        using var document = JsonDocument.Parse(json.Text);
                        return document.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        return json.Text;
                    }
                case string s when s.Length > MaxValueLength:
                    _shortened = true;
                    return s[..MaxValueLength] + $"... ({s.Length} characters)";
                default:
                    return value;
            }
        }

        private void Markdown(Dictionary<string, object?> result)
        {
            if (result.TryGetValue("rowsAffected", out var affected))
            {
                _text.AppendLine($"{Abbreviate((string)result["statement"]!)}: {Values.Affected((int?)affected)}");
                return;
            }

            var columns = (List<string>)result["columns"]!;
            var rows = (List<object?[]>)result["rows"]!;
            _text.AppendLine("| " + string.Join(" | ", columns.Select(Cell)) + " |");
            _text.AppendLine("|" + string.Concat(columns.Select(_ => " --- |")));
            foreach (var row in rows)
            {
                _text.AppendLine("| " + string.Join(" | ", row.Select(v => v is JsonElement e ? Cell(e.GetRawText()) : v is null ? "NULL" : Cell(Values.Text(v)))) + " |");
            }

            _text.AppendLine($"({Values.RowCount((long)result["rowCount"]!)}{((bool)result["truncated"]! ? "; more not read, see hint" : "")})");
            _text.AppendLine();
        }

        private static string Cell(string value) => value.Replace("|", "\\|").ReplaceLineEndings("<br>");
    }
}
