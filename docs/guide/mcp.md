# With AI Assistants (MCP)

`pgembed mcp` runs an [MCP](https://modelcontextprotocol.io) server over standard input and output. It gives
an assistant a real Postgres of any version that it can start, query, explain and inspect, with the same
safety rules as the command line.

## Set it up

Claude Code:

snippet: sample_mcp_claude_code

Claude Desktop, Cursor and other clients that read an `mcpServers` file:

snippet: @/samples/mcp/mcp.json

VS Code (`.vscode/mcp.json`):

snippet: @/samples/mcp/vscode-mcp.json

`dnx` runs it without installing (.NET 10). With the tool installed globally, use `"command": "pgembed"` and
`"args": ["mcp"]`.

## What the assistant can do

| Tools | |
| --- | --- |
| `list_versions`, `install_version`, `list_extensions` | Versions, ahead-of-time installs with progress, extensions |
| `start_instance`, `list_instances`, `stop_instance`, `restart_instance` | Instances; started ones keep running after the assistant exits |
| `kill_instance`, `remove_instance` | Force-kill, or delete with its data; both ask first |
| `get_connection_string`, `get_logs` | For an instance |
| `run_sql` | Rows (at most `max_rows`, default 100, with a paging hint), affected rows, or an error with its position and hint |
| `check_sql`, `format_sql` | Offline: syntax and a summary per statement; formatting |
| `explain_query` | The plan as JSON; with `analyze`, runs the statement in a transaction that is always rolled back |
| `describe_schema`, `describe_table` | Tables; columns, keys, indexes, constraints |
| `doctor` | Why Postgres will not start here |

Every tool and parameter is in the [MCP tools reference](../reference/mcp-tools). Results come as structured
content plus a compact Markdown table; `json` and `jsonb` values arrive as JSON, and long values are
shortened, so results stay small for the assistant.

## Safety

- **Changes ask first.** Statements that change the schema or existing rows, `kill_instance` and
  `remove_instance` ask before running. Clients that support it (elicitation) ask you directly. Otherwise the
  tool refuses until the assistant calls again with `confirm: true`; that second call slows the assistant
  down but is not a check by you: your client's approval of tool calls is that check.
- **Read-only mode** refuses anything that writes, including `INSERT` and transaction control (so
  `COMMIT; DELETE ...` cannot escape it), and runs the SQL in a read-only transaction. It applies per call
  (`read_only: true`), to the whole server (`--read-only`), and always to servers pgembed did not start
  (connection-string targets), unless you pass `--allow-external-writes`. Functions that act outside the
  transaction, such as `pg_terminate_backend`, are not stopped by it; use a restricted role for that.

snippet: sample_mcp_options

A read-only setup:

snippet: @/samples/mcp/read-only.json

## Other servers, and passwords

Anything in a tool call is part of the conversation: the client shows it, logs it, and sends it to the model.
So a password must never be in one. pgembed instances need none (they trust local connections). For any
other server, give the MCP server a **named connection**: the assistant uses the name, and the connection
string stays on your machine.

snippet: @/samples/mcp/named-connection.json

- `list_instances` shows connection names, never their connection strings.
- A named connection is a server pgembed did not start, so it is read-only unless you pass
  `--allow-external-writes`.
- Keep the password out of the connection string too: `PGPASSWORD`, or a `.pgpass` file (`PGPASSFILE`), set
  for the server process.
- Errors never repeat a password, even from a connection string pgembed could not read.
