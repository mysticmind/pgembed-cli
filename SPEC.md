# pgembed: specification

`pgembed` is a command-line tool, and later an MCP server, for embedded PostgreSQL. It runs real Postgres servers of any version side by side, with no Docker and no system install, and lets you query and inspect them.

It is built on the [MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed) library and uses the same binaries ([zonky](https://github.com/zonkyio/embedded-postgres-binaries)).

Status: proposal. Decisions marked **(decided)** were agreed on 2026-11-06.

## Goals

1. **Prepare machines:** list versions, install them ahead of time (CI caches, offline machines), see their extensions, manage the download cache.
2. **Run local databases:** named instances of any version, several at once, running in the background.
3. **Work with the data:** run SQL, see results, inspect the schema.
4. **Diagnose:** explain why a server will not start.
5. **Serve AI assistants** through MCP, with the same capabilities.

## Packaging and repos

| | |
| --- | --- |
| Repo | `mysticmind/pgembed-cli` |
| NuGet package and command | `pgembed` (a .NET tool) |
| Install | `dotnet tool install -g pgembed`, or `dnx pgembed <command>` on .NET 10+ |
| Runtime | built for net8.0 with roll-forward, so it runs on .NET 8 and later |
| Versioning | independent of the library, starting at 0.1.0 |
| Library reference | `MysticMind.PostgresEmbed` `[5.2.0, 6.0.0)` (5.2 adds what pgembed needs, see below) |
| Publishing | NuGet trusted publishing, policy for repository `pgembed-cli`, workflow `publish.yaml` |
| Dependencies | Spectre.Console and Spectre.Console.Cli (commands and output), Npgsql (SQL), [pgsqlparser](https://github.com/mysticmind/pgsqlparser-dotnet) (formatting, splitting, syntax checks, classification), PrettyPrompt (shell, 0.2), ModelContextProtocol (MCP, 0.2) |

## Architecture

All behavior lives in one internal core: an **instance manager**, a **cache manager** and a **version catalog**, on top of the library. The CLI commands and the MCP tools are thin front ends over that core, so a command and its MCP tool always behave the same.

## Cache layout

One cache per user, shared by the CLI, the MCP server and (opt-in) the library in your tests:

| OS | Default location |
| --- | --- |
| Linux | `$XDG_CACHE_HOME/pgembed`, else `~/.cache/pgembed` |
| macOS | `~/Library/Caches/pgembed` |
| Windows | `%LOCALAPPDATA%\pgembed` |

Override with `--cache-dir` or `PGEMBED_CACHE_DIR`.

```
<cache>/
  metadata/<artifact>.json          version list per platform, its ETag and fetch time
  binaries/                         downloads (.jar, .txz, extension zips) and extracted installs
    postgres-<version>-<artifact>[-ext-<hash>]/
      extensions.json               extension index, built once per install
      .last-used                    for cache clean --unused-for
  instances/<name>/
    instance.json                   registry entry: version, port, settings, timestamps
    data/                           the Postgres data folder
    postgres.log
    .lock                           serializes start, stop and kill across processes
```

### Caching rules

**Version metadata.** Fresh for 24 hours (configurable): resolving `17` needs no network. When stale, one conditional request with `If-None-Match` (Maven answers `304 Not Modified` when nothing changed). Offline, the cached list is used with a warning, however old. Exact versions never need metadata. `--refresh` forces a check.

**Downloads and installs.** Each version is downloaded once across all processes: a lock per archive makes concurrent test runs, the CLI and the MCP server wait for one download. Interrupted downloads resume with HTTP range requests (Maven supports them). Every archive is verified against Maven's published SHA-256. Each install is extracted once and shared by every instance of that version.

**Named instances.** The registry plus Postgres's own `postmaster.pid` tell whether an instance is running, its PID and port. A `postmaster.pid` pointing at a dead process is reported as stopped and cleaned up. A port is chosen once per instance and remembered, so connection strings never change.

## Named instances and attaching

- An instance has a **name** (`app`), a **version** pinned to the major chosen at creation (Postgres cannot open data from another major), a **port** and its **data**.
- Instances of different versions run side by side.
- **Attach if running (decided, default for named instances):** starting a named instance that is already running, whether started by `pgembed`, the MCP server or another test process, attaches to it in milliseconds instead of starting another server.
- **Ownership:** a server is stopped on dispose only by the process that started it. Explicit `stop` and `kill` work from anywhere.

From tests, through the library:

```bash
pgembed start app --version 17        # once, in a terminal
```
```csharp
await using var server = new PgServer(new PgServerOptions { PgVersion = "17", InstanceName = "app" });
await server.StartAsync();            // attaches: near-instant
```

## Commands

Common options: `--cache-dir`, `--maven-repo`, `--json` (machine-readable output; no colors or tables), `--verbose`, `--quiet`.
Exit codes: `0` success, `1` failure, `2` usage error. Output is plain text when redirected, and `NO_COLOR` is respected.

### Discovery and cache

| Command | Does |
| --- | --- |
| `pgembed versions [--major 17] [--all] [--platform linux-arm64] [--refresh]` | published versions: newest per major by default, every version of a major, or all |
| `pgembed install <version> [--extension <url>[#sha256=<hex>]]...` | resolve, download, verify, extract; prints the install folder |
| `pgembed extensions <version>` | what `CREATE EXTENSION` can use: name, default version, description, requirements |
| `pgembed cache list` | installs and downloads, with versions, platforms, sizes, last use |
| `pgembed cache clean (--keep-latest \| --version <spec> \| --unused-for <age> \| --all) [--dry-run]` | delete selected entries; never one in use by a running instance |
| `pgembed cache refresh` | refresh version metadata now |
| `pgembed doctor [--dir <path>] [--port <n>]` | checks platform, not running as root, Maven reachable, cache writable, folder usable by Postgres (Windows short names, issue #29), port free; exit 1 if any fails |

Version specs everywhere: exact (`17.6.0`), major (`17`, `9.6`), minor (`17.6`) or `latest`.
Platforms for `--platform`: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64`.

### Instances

| Command | Does |
| --- | --- |
| `pgembed start <name> --version <spec> [--port <n>] [--param key=value]... [--extension <url>]...` | create or start an instance in the background (decided default); prints the connection string |
| `pgembed start <name> ... --foreground` | stay attached and stream the log; Ctrl+C or SIGTERM stops Postgres cleanly (for systemd, Docker, supervisord) |
| `pgembed start <prefix> --version 16,17,18` | one instance per version: `<prefix>-16`, `<prefix>-17`, `<prefix>-18` |
| `pgembed status [<name>]` (alias `list`) | name, version, port, PID, state, uptime, data size |
| `pgembed stop <name>... \| --all [--immediate]` | clean shutdown, or immediate (aborts sessions) |
| `pgembed kill <name>... \| --all` | force-kill the server processes |
| `pgembed restart <name>` | stop, then start with the same settings |
| `pgembed logs <name> [--tail <n>] [--follow]` | the server log |
| `pgembed connstr <name> [--db <db>] [--format npgsql\|uri\|env]` | connection string, `postgresql://` URI, or `PGHOST`/`PGPORT`/... lines |
| `pgembed rm <name> [--force]` | delete the instance and its data; refuses while running unless `--force` |

Names accept wildcards where several instances make sense: `pgembed stop ci-*`.

### SQL and schema (decided: pgembed instances only, for now)

| Command | Does |
| --- | --- |
| `pgembed sql <name> "<sql>" \| -f <file> \| (stdin) [--db <db>] [--format table\|json\|csv\|markdown] [--timing]` | run SQL; each statement shows its result set or affected rows; errors show the message and mark the failing position |
| `pgembed schema <name> [<table>] [--schema <schema>] [--db <db>]` | tables with row estimates and sizes; for one table: columns, types, nullability, defaults, primary key, indexes, foreign keys, checks |
| `pgembed shell <name> [--db <db>]` (0.2) | interactive: history per instance, multi-line statements, SQL highlighting, completion of keywords and your tables and columns, and `psql`-style commands: `\dt`, `\d <table>`, `\dn`, `\di`, `\dx`, `\l`, `\c <db>`, `\x`, `\timing`, `\i <file>`, `\q` |

| `pgembed format ["<sql>"] [-f <file>]... [--check]` | format SQL from arguments, files or stdin; `--check` exits 1 when a file is not formatted (a CI lint); needs no server |

The table view caps rows (default 500) and says when it truncates; `json` and `csv` return everything.

### Using the PostgreSQL parser (pgsqlparser)

[pgsqlparser](https://github.com/mysticmind/pgsqlparser-dotnet) wraps libpg_query, the real PostgreSQL parser, with native libraries for every platform pgembed supports. pgembed uses it to:

- **split scripts correctly** for `sql -f`: dollar-quoted function bodies and semicolons in strings are handled, so each statement gets its own result and an error names the failing statement;
- **check syntax offline** with `sql --check`, showing the error position, without a running server;
- **format SQL** in `format` and in the shell;
- drive the shell's **highlighting** (tokenizer) and decide when a **multi-line statement is complete** (scanner);
- **classify statements**: the shell asks before `DROP`, `TRUNCATE`, or `DELETE`/`UPDATE` without `WHERE`, and the MCP read-only mode refuses writes before they reach the server (on top of the read-only transaction).

The server stays the authority: when the parser rejects SQL the server might accept (for example syntax newer than the parser's PostgreSQL 18 grammar), pgembed still sends it and only skips splitting and checks.

## MCP server (decided: in 0.2)

`pgembed mcp` runs an MCP server over stdio:

```json
{ "mcpServers": { "pgembed": { "command": "dnx", "args": ["pgembed", "mcp"] } } }
```

| Tool | Does |
| --- | --- |
| `list_versions` | published versions, newest per major |
| `start_instance` | start (or reuse) a named instance of a version; returns the connection string |
| `list_instances` | name, version, port, state |
| `stop_instance`, `kill_instance` | clean stop, or force (kill asks for confirmation) |
| `remove_instance` | delete an instance and its data (asks for confirmation) |
| `get_connection_string`, `get_logs` | for a named instance |
| `list_extensions` | for a version |
| `run_sql` | run SQL, return columns and rows, with a row limit; optional read-only mode (writes are refused by classification, and the SQL runs in a read-only transaction) |
| `format_sql` | format SQL (no server needed) |
| `analyze_query` | the tables and columns a query uses and the locks it takes (no server needed) |
| `describe_schema`, `describe_table` | tables; columns, keys, indexes, constraints |
| `explain_query` | the query plan |

Instances started through MCP are background servers, so they survive restarts of the editor or assistant. The tool package declares itself an MCP server so nuget.org shows the configuration, and `dnx` runs it without an install.

## Library work first: MysticMind.PostgresEmbed 5.2

pgembed needs these library additions. Additive, no breaking changes:

1. **Named instances:** `PgServerOptions.InstanceName`, stored in the shared cache by default so the CLI, MCP and tests meet; attach if running (default); per-instance cross-process lock; registry entry.
2. **Across processes:** `GetStatusAsync()` (running, PID, port, start time, from `postmaster.pid` and a process check); `StopAsync()` stops a named instance started by another process; new `KillAsync()`.
3. **Metadata cache:** stored version lists with ETag refresh, offline fallback, configurable age.
4. **Download cache:** opt-in shared cache through `PGEMBED_CACHE_DIR`, cross-process download lock, resumable downloads, extension index per install, last-used tracking.
5. **Progress:** an `IProgress<>` for downloads and extraction, so pgembed can show a real progress bar.

## Stages

| Release | Contents |
| --- | --- |
| Library 5.1.0 | merged (#40), to publish: version specs, `InstallAsync`, extension discovery, checksum verification |
| Library 5.2.0 | the five items above |
| pgembed 0.1 | discovery and cache commands, `doctor`, instances (background by default, `--foreground`, several versions), `sql` (with parser-based splitting and `--check`), `schema`, `format` |
| pgembed 0.2 | `shell`, MCP server |
| Later | `ext add` with curated extensions (from the planned `mysticmind-postgres-binaries`), a remote extension catalog, `schema --ddl` |

## Out of scope for now

- Connecting to arbitrary Postgres servers (only pgembed instances).
- Backups, users and roles, migrations: other tools do these well.
- Wrapping `psql` or `pg_dump`: zonky's binaries do not include them.
- Running as root: Postgres refuses, so `doctor` reports it.
