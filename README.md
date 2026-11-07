<!-- mcp-name: io.github.mysticmind/pgembed -->
<img src="https://raw.githubusercontent.com/mysticmind/pgembed-cli/main/assets/icon.png" alt="pgembed icon: an elephant standing on a terminal" width="96">

# pgembed [![NuGet](https://badgen.net/nuget/v/pgembed)](https://www.nuget.org/packages/pgembed/)

Run PostgreSQL on your machine without installing it. `pgembed` downloads the official Postgres
binaries for your platform (Windows, macOS, Linux and Alpine, x64 and arm64), keeps named
instances of any version running in the background, and runs SQL against them. It is also an
[MCP server](#mcp-server-for-ai-assistants), so an AI assistant can do the same.

```console
$ pgembed start app --version 17
● Started app: PostgreSQL 17.11.0 on port 5432
  Host=localhost;Port=5432;Username=postgres;Database=postgres

$ pgembed sql app "SELECT version()"
$ pgembed stop app
```

It is a .NET tool built on [MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed),
and shares that library's download cache: a version installed by `pgembed` is not downloaded again
by your tests, and the other way round.

## Install

Needs the .NET 10 SDK or newer.

```console
dotnet tool install --global pgembed
dnx pgembed status                      # or run it without installing
```

The package is precompiled for each platform (Windows x64, Linux x64 and arm64 with glibc or
musl, macOS x64 and arm64), so commands start quickly; other platforms get a portable build.

## Instances

An instance is a named Postgres server with its own data folder. It is created on first start and
keeps its version, port and settings, so later commands only need its name.

```console
pgembed start app --version 17          # background; prints the connection string
pgembed start app                       # already running? attaches and prints it again
pgembed start ci --version 16,17,18     # three instances: ci-16, ci-17, ci-18
pgembed start dev --foreground          # in this terminal; Ctrl+C stops it cleanly

pgembed status                          # version, port, process, state, uptime, data size
pgembed connstr app                     # every format: npgsql, uri, libpq, jdbc, env
pgembed connstr app --format uri        # just one, for scripts; add --db and --schema as needed
pgembed logs app --follow
pgembed restart app
pgembed stop app                        # or: stop 'ci-*', stop --all
pgembed kill app                        # last resort
pgembed rm app                          # deletes the data, after asking; --force for a running one
```

`start` options:

| Option | Meaning |
|---|---|
| `-v, --version` | Exact (`17.6.0`), major (`17`), minor (`17.6`) or `latest`; comma-separated for several |
| `-p, --port` | Port; default 5432 when free, otherwise any free port. Kept for later starts |
| `--param KEY=VALUE` | A server setting, such as `--param shared_buffers=256MB`. Repeatable |
| `--extension URL` | An extension zip to install; add `#sha256=<hex>` to verify it. Repeatable |
| `--locale` | Locale for a new instance's data; default `en_US.UTF-8`, or `C.UTF-8` on Linux systems without it |
| `--foreground` | Stay attached to the terminal instead of running in the background |

An instance keeps its major version: its data folder can only be opened by that major. To move
to another major, create a new instance.

## SQL

```console
pgembed sql app "SELECT * FROM pg_stat_activity"
pgembed sql app -f seed.sql --timing
cat migrate.sql | pgembed sql app
pgembed sql app --db shop --schema sales "SELECT * FROM orders"
pgembed sql app "SELECT * FROM books" --format csv > books.csv
pgembed sql --check -f migrate.sql       # syntax and a summary per statement; needs no server
pgembed sql --check "DELETE FROM books"  # the SQL can be the one argument
```

The target is an instance name or a connection string in any common format, so `sql` works with any
PostgreSQL server too:

```console
pgembed sql postgresql://me@db.example.com/shop "SELECT 1"
pgembed sql "host=localhost port=5433 user=me dbname=shop" -f report.sql
pgembed sql "Host=localhost;Port=5433;Username=me;Database=shop" "SELECT 1"
pgembed sql "jdbc:postgresql://localhost:5433/shop?user=me" "SELECT 1"
```

Keep passwords off the command line: `PGPASSWORD` and the `~/.pgpass` file are used when the
connection string has none.

Scripts are split into statements with the real PostgreSQL parser (through
[pgsqlparser](https://github.com/mysticmind/pgsqlparser-dotnet)), so dollar-quoted function bodies
and semicolons inside strings are handled. Each statement's result is shown as it completes. An
error shows the line and points at the position:

```console
$ pgembed sql app "SELECT id FROM books WHERE titel = 1"
ERROR: column "titel" does not exist
LINE 1: SELECT id FROM books WHERE titel = 1
                                   ^
HINT: Perhaps you meant to reference the column "books.title".
```

### Changes are confirmed first

Statements that change the schema (`CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `SELECT INTO` and the
like) or change existing rows (`UPDATE`, `DELETE`, `MERGE`) are listed before anything runs, and
pgembed asks before running them. An `UPDATE` or `DELETE` without `WHERE` is called out. Reads and
`INSERT` run without asking. Where there is no terminal to ask on (piped SQL, CI), such statements
are refused unless you pass `--yes`.

The check reads the SQL; it cannot see inside functions, so `SELECT my_cleanup()` runs without
asking.

### Interactive prompt

Without SQL to run, `pgembed sql app` opens a prompt:

- syntax highlighting, and syntax errors underlined as you type
- Enter runs the SQL once it ends with a semicolon; until then it starts a new line
- Tab completes keywords, schemas, tables and columns
- history, kept between sessions
- Ctrl+C cancels a running statement; `\q` quits
- psql-style commands: `\d [name]`, `\dt`, `\dv`, `\df`, `\dn`, `\l`, `\c <db>`, `\conninfo`,
  `\x` (one block per row), `\timing`, `\format <format>`, `\pager [on|off]`, `\i <file>`, `\?`

### Output formats

| Format | |
|---|---|
| `table` | Fits the terminal, wrapping long values. The default in a terminal |
| `aligned` | psql-style text, never wrapped, one row per line. The default when output goes to a file or pipe |
| `json` | Every result, for scripts (`--json` is the same); `json` and `jsonb` values are nested JSON, one row per line |
| `csv` | Header and rows |
| `markdown` | A table for docs and issues |

Results stream: rows are written as they arrive, so a million-row export to `csv` or `json` uses no
more memory than a one-row query. The `table` view shows the first `--max-rows` rows and stops the
query there, instead of reading the rest.

In a terminal, a result taller than the screen opens in a pager, as in psql: `PGEMBED_PAGER`, else
`PAGER`, else `less -SFX` (long lines scroll sideways; a short result just prints). Set `PAGER=` to
an empty value, pass `--no-pager`, or use `\pager off` in the prompt to turn it off.

| Option | Meaning |
|---|---|
| `-f, --file` | Run a SQL file |
| `--db` | Database (default `postgres` for an instance) |
| `--schema` | Schema for unqualified names (sets the search path) |
| `--format` | One of the formats above |
| `--max-rows` | Rows shown per result in `table` format (default 500) |
| `--timing` | Show each statement's time |
| `--no-pager` | Never open a pager |
| `--check` | Check the syntax and summarize each statement (kind, tables, functions, whether it asks first) without running anything |
| `-y, --yes` | Run changes without asking |

## Format

```console
pgembed format "select a,b from t where a>1"     # prints the formatted SQL
pgembed format -f schema.sql --write             # rewrites the file
pgembed format -f a.sql -f b.sql --check         # lists unformatted files and exits 1: a CI lint
```

Formatting uses the PostgreSQL parser and keeps comments. It needs no server.

## Schema

```console
pgembed schema app                          # tables and views, with row estimates and sizes
pgembed schema app books                    # one table: columns, keys, indexes, constraints
pgembed schema app orders --schema sales
pgembed schema app "sales.*"                # every table in sales
pgembed schema app books --json             # one structured document, for scripts and tools
```

The same descriptions are available as psql-style commands, in the interactive prompt and as
one-shot SQL: `pgembed sql app "\d books"`, `"\dt sales.*"`, `"\dv"`, `"\df"`, `"\dn"`, `"\l"`.

## MCP server for AI assistants

`pgembed mcp` runs an [MCP](https://modelcontextprotocol.io) server over standard input and output.
Add it to your assistant:

```console
claude mcp add pgembed -- dnx pgembed mcp          # Claude Code, on .NET 10 or newer
```

```json
{ "mcpServers": { "pgembed": { "command": "dnx", "args": ["pgembed", "mcp"] } } }
```

(With the tool installed globally, use `"command": "pgembed", "args": ["mcp"]`.)

| Tool | Does |
|---|---|
| `list_versions`, `install_version`, `list_extensions` | Versions, ahead-of-time installs, extensions |
| `start_instance`, `list_instances`, `stop_instance`, `restart_instance` | Instances; started ones keep running after the assistant exits |
| `kill_instance`, `remove_instance` | Force-kill, or delete with its data (both confirmed) |
| `get_connection_string`, `get_logs` | For an instance |
| `run_sql` | Rows (at most `max_rows`, default 100, with a paging hint), affected rows, or an error with its position and hint |
| `check_sql`, `format_sql` | Offline: syntax and a summary per statement; formatting |
| `explain_query` | The plan as JSON; with `analyze`, runs it in a transaction that is always rolled back |
| `describe_schema`, `describe_table` | Tables; columns, keys, indexes, constraints |
| `doctor` | Why Postgres will not start here |

The same safety rules as the CLI apply:

- Statements that change the schema or existing rows, `kill_instance` and `remove_instance` ask
  first. Clients that support it ask you directly; otherwise the tool refuses until the assistant
  calls again with `confirm: true`. That second call is a speed bump for the assistant, not a check
  by you: your client's approval of tool calls is that check.
- Read-only mode refuses anything that writes, including `INSERT` and transaction control (so
  `COMMIT; DELETE ...` cannot escape it), and runs the SQL in a read-only transaction. It applies
  per call (`read_only: true`), to the whole server (`pgembed mcp --read-only`), and always to
  servers pgembed did not start (connection-string targets), unless you pass
  `--allow-external-writes`. Functions that act outside the transaction, such as
  `pg_terminate_backend`, are not stopped by it; use a restricted role for that.
- `pgembed mcp --no-confirm` skips the questions, for sandboxes and throwaway agents.

## Versions and extensions

```console
pgembed versions                        # newest version of each major
pgembed versions --major 17             # every 17.x
pgembed versions --platform linux-musl-x64
pgembed install 17                      # download ahead of time, for example in a Docker build
pgembed extensions 17                   # what CREATE EXTENSION can use in that version
```

The version list comes from Maven Central and is cached for 24 hours; `--refresh` checks now.
Offline, the cached list is used with a warning.

## Cache and diagnostics

```console
pgembed cache list                      # installs and downloads, with sizes and what is in use
pgembed cache clean --keep-latest       # keep the newest of each major
pgembed cache clean --version 16 --dry-run
pgembed doctor                          # platform, user, Maven, cache folder, port 5432
```

`cache clean` never removes a version a running instance uses.

## Options for every command

| Option | Meaning |
|---|---|
| `--json` | Machine-readable output on standard output |
| `--cache-dir` | Cache folder (default `PGEMBED_CACHE_DIR`, else the per-user cache folder) |
| `--maven-repo` | A Maven mirror (default Maven Central, or `PGEMBED_MAVEN_REPO`) |
| `--verbose` | Log details to standard error |

Set `PGEMBED_TRACE=1` to see where a command's time goes, on standard error.

The per-user cache folder is `~/.cache/pgembed` on Linux, `~/Library/Caches/pgembed` on macOS and
`%LOCALAPPDATA%\pgembed` on Windows. It holds `binaries/` (downloads and installs), `instances/`
(one folder per instance, with its data and log) and `metadata/` (cached version lists).

Exit codes: 0 for success, 1 for a failure, 2 for a usage error, 130 when cancelled.

## Notes

- On Linux and macOS, Postgres refuses to run as root. Run `pgembed` as a normal user; in a
  container, add one.
- The binaries come from [zonky embedded-postgres-binaries](https://github.com/zonkyio/embedded-postgres-binaries)
  and are verified against the SHA-256 that Maven publishes.
- Instances are for development and testing: they listen on localhost and trust local
  connections without a password.

## License

MIT
