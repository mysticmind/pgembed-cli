# pgembed: specification

`pgembed` is a command-line tool and an MCP server for embedded PostgreSQL. It runs real Postgres servers of any version side by side, with no Docker and no system install, and lets you query and inspect them.

It is built on the [MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed) library and uses the same binaries ([zonky](https://github.com/zonkyio/embedded-postgres-binaries)).

Status: 0.1 is built (not yet published); 0.2 is built, across the branches listed under "Before releasing 0.2". Decisions marked **(decided)** were agreed on 2026-11-06. Decisions marked **(proposed)** were made for 0.2 on 2026-11-07 and built as described; they wait for review.

## Goals

1. **Prepare machines:** list versions, install them ahead of time (CI caches, offline machines), see their extensions, manage the download cache.
2. **Run local databases:** named instances of any version, several at once, running in the background.
3. **Work with the data:** run SQL, see results, inspect the schema.
4. **Diagnose:** explain why a server will not start.
5. **Serve AI assistants** through MCP, with the same capabilities.

Every goal is held to one bar: **exceptional developer experience**. Concretely, a command:

- works with the fewest arguments that can be right (remembered ports, versions and settings; sensible defaults);
- never leaves the user waiting without feedback (anything over a second shows progress);
- fails with one clear line that says what happened and what to do next (the exact command, where there is one);
- prints what a script needs when redirected (plain text, `--json`) and what a person needs in a terminal;
- behaves the same from the CLI, from MCP and from a test using the library.

## Performance (proposed, 0.2)

pgembed should feel instant: a command that does not wait on Postgres or the network finishes before the user notices, and large results stream at the speed of the server.

### Measurements (2026-11-07)

macOS arm64, warm, against a running Postgres 17 instance; `psql` 14 for comparison. Each pgembed column is measured as it ships: "0.2 ReadyToRun" is the precompiled package, "0.2 AOT" the Native AOT one that 0.2 ships (see How). `bench/budgets.py` reproduces the AOT column.

| | 0.1 | 0.2 ReadyToRun | 0.2 AOT | psql |
| --- | --- | --- | --- | --- |
| `--version` | 95 ms | 54 to 75 ms | 15 to 19 ms | |
| `connstr <instance>` | | 59 to 80 ms | 15 to 20 ms | |
| `status` (one instance) | 157 ms | 99 to 133 ms | 19 to 39 ms | |
| `sql <instance> "SELECT 1"` | 350 ms | 101 to 130 ms | 23 to 38 ms | 40 ms |
| `sql --check "SELECT 1"` | (checked nothing, see below) | 131 to 140 ms | 37 to 71 ms | |
| `format "select 1"` | | 111 to 129 ms | 37 to 70 ms | |
| `start <instance>`, attaching to a running one | | 93 ms | 17 ms | |
| 1M rows (two columns) as CSV | 2.9 s, 327 MB | 2.3 s, 130 MB | 1.7 s, 39 MB | 1.9 s, 121 MB |
| 1M rows as JSON | 2.8 s, 699 MB | 2.1 s, 131 MB | 1.7 s, 41 MB | |
| 1M-row table, `table` view capped at 500 | 2.1 s (reads everything) | 0.15 to 0.27 s | 36 ms | |
| 5M rows as CSV | | 8.8 s, 130 MB | 8.5 s, 41 MB | |
| Prompt, per keystroke, 200-line statement | | 1 ms | | |
| Rendering 10,000 rows (`csv`, `json`, `aligned`, `markdown`) | | 2 to 4 ms | | |
| MCP server start; a warm tool call | | 175 ms; 1 to 20 ms | 31 ms; 0 to 23 ms | |

Ranges are runs on a quiet and on a busy machine. Where the time went, and what removed it: JIT compilation and type loading (Native AOT; ReadyToRun removed only part); Spectre.Console.Cli building its command model by reflection on every run, 30 to 40 ms (System.CommandLine); the first use of the parser's full parse tree, about 90 ms, for every `sql` command (a tokenizer check answers for plain reads); the library's general status check to find an instance's port (Postgres's own `postmaster.pid` is read instead); reading every row behind a capped table (the query is cancelled at the cap); and holding whole results in memory (rows stream). Memory is flat: about 40 MB under AOT (130 MB with the JIT runtime), the same for one row or five million. 0.1's `sql --check "<sql>"` took the SQL as the target and checked an empty statement; 0.2 fixes that, so its time includes real parsing. `PGEMBED_TRACE=1` prints where a command's time goes.

### Budgets

Warm, on the reference machine above, as shipped. `bench/budgets.py` checks them end to end; CI runs it on Linux x64 with the Native AOT build and a tolerance of 2.5, since shared runners are slower and noisier, and puts the report in the job summary.

| | Budget | 0.2 |
| --- | --- | --- |
| `--version` | under 80 ms | met |
| `connstr` | under 90 ms | met |
| `status` of a few instances | under 150 ms | met |
| `sql <instance> "SELECT 1"` | under 150 ms | met |
| `sql --check`, `format` on a short statement | under 150 ms | met |
| Attaching to a running instance (`start`) | under 200 ms | met |
| Streaming formats (`csv`, `json`, `markdown`, `aligned`) | memory flat in the row count (at most 1.3x from 100k to 1M rows), within 1.2x of psql's time | met |
| `table` with a cap, on a 1M-row table | under 600 ms | met |
| Interactive prompt, per keystroke, 200-line statement | under 16 ms | met (1 ms) |
| MCP tool call, beyond the query itself | under 5 ms | met |

Under Native AOT every command is well inside its budget: the slowest, `sql --check` and `format`, spend most of their 40 to 70 ms in the parser's first full parse. The budgets stay as they are, as a regression line for CI.

### How

1. **Native AOT (built, on `v0.2-aot`).** One native package per platform (win-x64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-x64, osx-arm64), about 15 MB each: the executable and the parser's native library. A portable `any` package (JIT) covers other platforms, and a top-level package points at them; `dotnet tool install` and `dnx` pick the right one. AOT compiles only for the OS it runs on, so `publish.yaml` packs each platform on a matching runner (musl ones in the Alpine SDK image) and pushes them before the top-level package. What made it possible: the command line moved from Spectre.Console.Cli (which reads metadata tokens AOT does not keep) to System.CommandLine through a thin layer that keeps the commands' settings classes; JSON serialization is generated at build time (`PgEmbedJsonContext`); trim and AOT warnings fail the build. Spectre.Console still draws tables, colours and progress. ReadyToRun came first and is superseded: These platform-specific packages need the .NET 10 SDK to install (a .NET 9 SDK refuses them: "Format version is higher than supported") and allow one target framework, so pgembed targets net10.0 only; .NET 8 and 9 reach end of support on 2026-11-10. The portable package needs `any` in `RuntimeIdentifiers` as well as `ToolPackageRuntimeIdentifiers`. `publish.yaml` pushes the platform packages before the top-level one.
2. **Stream results (built).** The executor yields rows as they arrive; `csv`, `json` and `markdown` write them straight to a buffered output. `aligned` needs column widths, so it buffers up to a window (1,000 rows), as psql does, then continues with those widths. `table` reads at most the cap and cancels the rest (inside a transaction it skips the rest instead, since a cancel would abort the transaction). Leaving the pager stops reading too; Postgres finishes a write it is blocked in before acting on a cancel, so what was already sent is drained.
3. **Pay only for what is used (built).** Plain reads skip the full parse; transaction tracking uses the tokenizer; an instance's port comes from `postmaster.pid`; `connstr` no longer measures the data folder; the prompt warms the parser while it connects, so the first keystroke does not pay for it.
4. **Warm processes stay warm (built for MCP).** The MCP server keeps one `NpgsqlDataSource` per target with pooling, so a tool call reuses an open connection and code that is already compiled. The web UI will do the same.
5. **The prompt (measured; no debounce).** Highlighting and the syntax check of a 200-line statement take 1 ms per keystroke, so a debounce would only add lag.
6. **Measure (built).** `bench/pgembed.Benchmarks` (BenchmarkDotNet) for the core without a server: the prompt, split, classify, check, format and rendering. `bench/budgets.py` for the budgets above, end to end.

## Security (built, 0.2)

Threat model: instances run as the user with the `postgres` superuser and trust local connections, so SQL can act
with the user's permissions; an AI assistant using the MCP server may be steered by text it reads (prompt
injection); other users may share the machine; downloads cross the network; data printed to a terminal is
untrusted. What holds (each with tests):

| Concern | What pgembed does |
| --- | --- |
| SQL reaching outside the database | asks first (CLI, prompt, MCP); read-only refuses: DO, CALL, EXECUTE, LOAD, COPY PROGRAM, COPY to or from a file, and built-in functions that touch files, other servers, the server, or run SQL text (`SqlSafety.ServerAccess`) |
| Read-only mode | the check above, plus transaction control, `RESET ALL`, `set_config`; a read-only transaction; on PostgreSQL 14+ as a superuser, `SET LOCAL ROLE pg_read_all_data`, which holds even through user functions |
| `explain_query` | one statement the parser reads; otherwise a `COMMIT` inside it could end the rolled-back transaction |
| Reading SQL the way the server does | every connection pins `standard_conforming_strings=on` as a startup option; changing it (or `backslash_quote`) in SQL is refused by MCP and read-only mode and asks first in the CLI; and each command reaches the server as exactly one statement, so where the readings could still differ, a smuggled statement is refused by the server (found by an independent review: `SET standard_conforming_strings = off` let a `COPY ... TO PROGRAM` hide inside what the checks read as a string) |
| Passwords | masked in every error and MCP result (`Connections.Mask`); MCP named connections (`--connection`) keep connection strings out of the conversation |
| Other local users | the Unix socket is 0700; `start --password` for shared machines (SCRAM over TCP, a generated password in 0600 settings); trust stays the default so library tests attach without one |
| Files | instance settings and prompt history 0600, folders 0700; archive extraction keeps every entry and link inside the install |
| Terminals | control characters in data, names, server messages and logs shown as symbols, never sent; pipes and files unchanged |
| Downloads | Maven SHA-256 checked; https or local repositories only (`PGEMBED_ALLOW_HTTP_REPO=1` to override); http extensions need `#sha256=`; library 5.3 `RequireChecksum` refuses mirrors that publish none |
| Runaway queries | MCP statement timeout (120 s, `--statement-timeout`); none from the CLI, where Ctrl+C cancels |
| Functions the user defines | not looked into (a superuser can do anything through them); `pgembed mcp --least-privilege` (PostgreSQL 14+) connects as `pgembed_assistant` (`LOGIN NOSUPERUSER NOCREATEROLE`, `pg_read_all_data`, `pg_write_all_data`, `CREATE` on the database and `public`), so the server refuses files, programs and libraries whatever the SQL |
| Relaxed protections | `--no-confirm` and `--allow-external-writes` are announced on standard error at startup and in the MCP server instructions |
| Trust authentication | the default, so library tests attach without a password; `PGEMBED_REQUIRE_PASSWORD=1` gives every new instance one |

Not covered, by design: functions the user defines unless `--least-privilege` is on; `--no-confirm` and
`--allow-external-writes`, which turn protections off on request.

## Packaging and repos

| | |
| --- | --- |
| Repo | `mysticmind/pgembed-cli` |
| Packages and command | `pgembed`: a .NET tool on NuGet, and on npm (the same Native AOT executables, one package per platform as `@mysticmind/pgembed-<platform>`) |
| Install | `npm install -g pgembed`, or `npx pgembed <command>`; needs Node.js 18 or later. Or `dotnet tool install -g pgembed`, or `dnx pgembed <command>`; needs the .NET 10 SDK or later |
| Runtime | net10.0; a Native AOT executable per platform, and a portable build rolling forward to later runtimes (see Performance) |
| Versioning | independent of the library, starting at 0.1.0 |
| Library reference | `MysticMind.PostgresEmbed` `[5.2.1, 6.0.0)` (5.2 adds what pgembed needs, see below; 5.2.1 fixes shared memory after a kill) |
| Publishing | NuGet trusted publishing, policy for repository `pgembed-cli`, workflow `publish.yaml` |
| Docs site | VitePress on GitHub Pages ([mysticmind.github.io/pgembed-cli](https://mysticmind.github.io/pgembed-cli/)), deployed by `deploy-docs.yml` on a manual run. Every code sample comes from `samples/` as a region snippet: shell scripts CI runs (`samples/run-cli-samples.sh`), a C# xUnit project CI runs, and config files. The command and MCP tool references are generated from the code, and `DocsTests` fails when they are out of date (`PGEMBED_UPDATE_DOCS=1` rewrites them) |
| Dependencies | System.CommandLine (the command line), Spectre.Console (tables, colours, progress, questions), Npgsql (SQL), [pgsqlparser](https://github.com/mysticmind/pgsqlparser-dotnet) (formatting, splitting, syntax checks, classification, summaries), PrettyPrompt 6 (the interactive prompt's line editor); from pgembed 0.2, the ModelContextProtocol C# SDK 1.x (`ModelContextProtocol.Core`, without the hosting package) |

## Architecture

All behavior lives in one internal core: an **instance manager**, a **cache manager**, a **version catalog**, a **SQL executor** and a **schema reader**, on top of the library. The CLI commands and the MCP tools are thin front ends over that core, so a command and its MCP tool always behave the same.

The core returns data (records), never output. Rendering (Spectre tables, `aligned`, CSV, JSON, MCP results) belongs to the front ends. **(proposed, 0.2)** In 0.1 the SQL runner both runs and renders; 0.2 splits it into an executor that returns results and errors, and renderers that consume them. The same split is what any later front end (see "Under consideration") needs.

Rule for MCP parity: every MCP tool maps to a CLI command or option. A tool without a command means the CLI is missing something.

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
  metadata/<artifact>.json          (CLI) version list per platform, its ETag and fetch time
  binaries/                         (library) downloads (.jar, .txz, extension zips) and extracted installs
    postgres-<version>-<artifact>[-ext-<hash>]/
  instances/<name>/                 (library) a named instance
    instance.json                   (CLI) registry entry: version, port, settings, timestamps
    data/                           the Postgres data folder
    postgres.log
    .lock                           (library) serializes start, stop and kill across processes
```

### Caching rules

**Version metadata (CLI).** Fresh for 24 hours: resolving `17` needs no network. When stale, one conditional request with `If-None-Match` (Maven answers `304 Not Modified` when nothing changed). Offline, the cached list is used with a warning, however old. Exact versions never need metadata. `--refresh` (on `versions`, `install` and `start`) forces a check. The CLI resolves specs itself and hands the library an exact version.

**Downloads and installs (library).** Each version is downloaded once across all processes: a lock per archive makes concurrent test runs, the CLI and the MCP server wait for one download. Every archive is verified against Maven's published SHA-256 (since 5.1). Each install is extracted once and shared by every instance of that version.

**Named instances (library and CLI).** The library tells whether an instance is running, its PID and port, from Postgres's own `postmaster.pid` and a process check; a `postmaster.pid` pointing at a dead process counts as stopped. The CLI's registry remembers each instance's settings and port, so connection strings never change across restarts.

## What lives where

Rule: **the library gets a feature only if a test or app using `PgServer` benefits on its own and it needs the library's internals.** Managing many instances, remembering state, presentation and policy belong in the CLI, so the library never grows CLI-shaped APIs.

| Library (MysticMind.PostgresEmbed) | CLI (pgembed) |
| --- | --- |
| `InstanceName`, attach if running, lock per instance | instance registry: settings, timestamps, remembered port |
| `GetStatusAsync`, cross-process `StopAsync`, `KillAsync` | names, wildcards, several versions per `start` |
| `PGEMBED_CACHE_DIR` as the default binaries folder, cross-process download lock | version metadata cache with ETag refresh |
| `PgPlatform.Detect()` | cache listing and cleanup |
| version specs, `InstallAsync`, extension discovery, checksums (5.1) | background and foreground modes, logs, connection-string formats |
| download progress (`IProgress`), proposed for 5.3 | SQL, schema, format, `doctor`, MCP |

## Named instances and attaching

- An instance has a **name** (`app`), a **version** pinned to the major chosen at creation (Postgres cannot open data from another major), a **port** and its **data**.
- Instances of different versions run side by side.
- **Attach if running (decided, default for named instances):** starting a named instance that is already running, whether started by `pgembed`, the MCP server or another test process, attaches to it in milliseconds instead of starting another server.
- **Ownership:** a server is stopped on dispose only by the process that started it. Explicit `stop` and `kill` work from anywhere.
- A first start that fails (initdb, port, shared memory) leaves no instance behind.

From tests, through the library:

```bash
pgembed start app --version 17        # once, in a terminal
```
```csharp
await using var server = new PgServer(new PgServerOptions { PgVersion = "17", InstanceName = "app" });
await server.StartAsync();            // attaches: near-instant
```

## Commands

Common options: `--cache-dir`, `--maven-repo`, `--json` (machine-readable output; no colors or tables), `--verbose` (library log on stderr).
Exit codes: `0` success, `1` failure, `2` usage error, `130` cancelled. Output is plain text when redirected, and `NO_COLOR` is respected.

### Discovery and cache

| Command | Does |
| --- | --- |
| `pgembed versions [--major 17] [--all] [--platform linux-arm64] [--refresh]` | published versions: newest per major by default, every version of a major, or all |
| `pgembed install <version> [--extension <url>[#sha256=<hex>]]... [--refresh]` | resolve, download, verify, extract; prints the install folder |
| `pgembed extensions <version>` | what `CREATE EXTENSION` can use: name, default version, description, requirements |
| `pgembed cache list` | installs and downloads, with versions, platforms, sizes, and whether a running instance uses them |
| `pgembed cache clean (--keep-latest \| --version <spec> \| --all) [--dry-run]` | delete selected entries; never one in use by a running instance |
| `pgembed doctor [--dir <path>] [--port <n>]` | checks platform, not running as root, Maven reachable, cache writable, folder usable by Postgres (Windows short names, issue #29), port free; exit 1 if any fails |

Version specs everywhere: exact (`17.6.0`), major (`17`, `9.6`), minor (`17.6`) or `latest`.
Platforms for `--platform`: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64`.

### Instances

| Command | Does |
| --- | --- |
| `pgembed start <name> --version <spec> [-p\|--port <n>] [--param key=value]... [--extension <url>]... [--locale <locale>] [--refresh]` | create or start an instance in the background (decided default); prints the connection string. The version is needed only on first start. The locale applies to a new instance's data: `en_US.UTF-8` by default, and on Linux systems without it, `C.UTF-8`, else `C` |
| `pgembed start <name> ... --foreground` | stay attached and stream the log; Ctrl+C or SIGTERM stops Postgres cleanly (for systemd, Docker, supervisord) |
| `pgembed start <prefix> --version 16,17,18` | one instance per version: `<prefix>-16`, `<prefix>-17`, `<prefix>-18` |
| `pgembed status [<name>...]` (alias `list`) | name, version, port, PID, state, uptime, data size |
| `pgembed stop <name>... \| --all` | clean shutdown |
| `pgembed kill <name>... \| --all` | force-kill the server processes |
| `pgembed restart <name>` | stop, then start with the same settings |
| `pgembed logs <name> [-n\|--tail <n>] [-f\|--follow]` | the server log (last 50 lines by default) |
| `pgembed connstr <name> [--db <db>] [--schema <schema>] [--format npgsql\|uri\|libpq\|jdbc\|env]` | every format by default; one, plain, with `--format` |
| `pgembed rm <name>... \| --all [--force] [--yes]` | delete instances and their data; refuses while running unless `--force`; confirms unless `--yes` |

Names accept wildcards where several instances make sense: `pgembed stop ci-*`.

### SQL and schema (decided: instances and any connection string)

| Command | Does |
| --- | --- |
| `pgembed sql <target> "<sql>" \| -f <file> \| (stdin) [--db <db>] [--schema <schema>] [--format table\|aligned\|json\|csv\|markdown] [--max-rows <n>] [--timing] [--yes]` | run SQL; each statement shows its result set or affected rows; errors show the message, mark the failing position, and add the server's detail and hint |
| `pgembed sql [<target>] --check "<sql>" \| -f <file>` | check syntax offline, showing the error position; needs no server. **(proposed, 0.2)** also prints a summary per statement: its kind, the tables and functions it uses, and whether it would need confirming |
| `pgembed sql <target>` (no SQL, in a terminal) | interactive prompt: highlighting, syntax errors underlined as you type, multi-line statements (Enter runs once the statement ends with a semicolon), completion of keywords, schemas, tables and columns, persistent history, and psql-style commands: `\d [name]`, `\dt`, `\dv`, `\df`, `\dn`, `\l`, `\c <db>`, `\conninfo`, `\x`, `\timing`, `\format`, `\i <file>`, `\?`, `\q` |
| `pgembed schema <target> [table\|pattern] [--db] [--schema] [--json]` | tables with row estimates and sizes; for a table: columns (type, nullability, default, identity, generated, primary key), indexes and constraints with foreign key targets. `--json` gives one structured document per table. The `\d` commands render the same reader, and work as one-shot SQL too |
| `pgembed format ["<sql>"] [-f <file>]... [--check] [--write]` **(proposed, 0.2)** | format SQL, keeping comments; `--write` rewrites the files; `--check` exits 1 when a file is not formatted (a CI lint); needs no server |

- The target is an instance name or a connection string: Npgsql, `postgresql://` URI, libpq
  `key=value` or JDBC. Passwords can come from `PGPASSWORD` or `~/.pgpass`.
- Confirmation: statements that change the schema (DDL, `TRUNCATE`, `SELECT INTO`), change existing rows
  (`UPDATE`, `DELETE`, `MERGE`, data-modifying CTEs, and these under `EXPLAIN ANALYZE`) or run a program
  (`COPY ... PROGRAM`) are listed and confirmed before anything runs; `UPDATE`/`DELETE` without `WHERE`
  are called out. Reads and `INSERT` do not ask. Without a terminal they are refused unless `--yes`.
- Output: `table` (Spectre, fits the terminal) is the default in a terminal; `aligned` (psql-style,
  never wrapped) when output is redirected. The table view caps rows (`--max-rows`, default 500) and says
  when it truncates; the other formats return everything.
- The line editor is PrettyPrompt: 4.1 in the net8.0 build (.NET 8 and 9), 6.x in the net10.0 build
  (.NET 10 and later); same API. Terminals without a size get plain line input.

### Using the PostgreSQL parser (pgsqlparser)

[pgsqlparser](https://github.com/mysticmind/pgsqlparser-dotnet) wraps libpg_query, the real PostgreSQL parser, with native libraries for every platform pgembed supports. pgembed uses it to:

- **split scripts correctly** for `sql -f`: dollar-quoted function bodies and semicolons in strings are handled, so each statement gets its own result and an error names the failing statement;
- **check syntax offline** with `sql --check`, showing the error position, without a running server;
- drive the prompt's **highlighting** and live **syntax-error underline** (tokenizer and parser), and decide when a **multi-line statement is complete**;
- **classify statements**: `sql` and the prompt confirm schema changes and changes to existing rows before they run (calling out `UPDATE`/`DELETE` without `WHERE`), and MCP's read-only mode refuses them before they reach the server (on top of the read-only transaction);
- **(proposed, 0.2)** **format SQL** in `format` and `format_sql` (`Parser.Format`), and **summarize statements** in `sql --check` and `check_sql` (`Parser.Summary`: tables, functions, filter columns, statement types).

The server stays the authority: when the parser rejects SQL the server might accept (for example syntax newer than the parser's PostgreSQL 18 grammar), pgembed still sends it; since it cannot classify it, it asks first (or needs `--yes`). Each command reaches the server as exactly one statement (Npgsql's SQL rewriting is off): scripts are split by the parser, or, for SQL it cannot read, by its scanner (strings, comments and dollar quotes, without the grammar). This is what keeps a statement from hiding where pgembed's reading and the server's differ.

## pgembed 0.2

### MCP server (decided: in 0.2; details proposed)

`pgembed mcp` runs an MCP server over stdio:

```json
{ "mcpServers": { "pgembed": { "command": "dnx", "args": ["pgembed", "mcp"] } } }
```

```bash
claude mcp add pgembed -- dnx pgembed mcp        # Claude Code
```

Options: `pgembed mcp [--read-only] [--no-confirm] [--allow-external-writes] [--least-privilege] [--statement-timeout <s>] [--connection <name=connstr>] [--cache-dir <path>]`.

| Tool | CLI equivalent | Does |
| --- | --- | --- |
| `list_versions` | `versions` | published versions, newest per major (or every version of one major) |
| `install_version` | `install` | download and verify a version ahead of time, with progress |
| `list_extensions` | `extensions` | what `CREATE EXTENSION` can use in a version |
| `start_instance` | `start` | start (or attach to) a named instance; creates it on first use; returns the connection string |
| `list_instances` | `status` | name, version, port, state, uptime, data size |
| `stop_instance`, `restart_instance` | `stop`, `restart` | clean stop; stop and start with the same settings |
| `kill_instance` | `kill` | force-kill (confirmed) |
| `remove_instance` | `rm --force` | delete an instance and its data (confirmed) |
| `get_connection_string` | `connstr` | in one format (Npgsql by default) |
| `get_logs` | `logs --tail` | the last lines of the server log |
| `run_sql` | `sql` | run SQL; columns and rows, affected rows, or a structured error |
| `check_sql` | `sql --check` | syntax check and statement summary; no server |
| `format_sql` | `format` | formatted SQL; no server |
| `explain_query` | `sql` with `EXPLAIN` | the query plan as JSON, optionally with `ANALYZE` |
| `describe_schema`, `describe_table` | `schema`, `schema <table>` | tables; columns, keys, indexes, constraints |
| `doctor` | `doctor` | why a server will not start here |

Not exposed, on purpose: `cache clean` (deletes downloads shared with every project and test run on the machine; an operator's task), `logs --follow`, `--foreground` and the interactive prompt (they have no meaning for a request and response).

**Instances survive the assistant.** Instances started through MCP are background servers (`KeepRunningAfterExit`), so they survive restarts of the editor or assistant and are the same instances the CLI and tests see.

**Targets.** `run_sql`, `check_sql`, `explain_query` and the schema tools take an instance name or a connection string, like the CLI. A connection-string target is a server pgembed did not start, possibly a real one, so it is read-only unless the server runs with `--allow-external-writes`.

**Confirmation.** The CLI's classification applies unchanged: changes to the schema or existing rows, `kill_instance` and `remove_instance` need confirming.

- When the client supports elicitation, the server asks the user, listing what would change (with `UPDATE`/`DELETE` without `WHERE` called out).
- Otherwise the tool runs nothing and returns the list of changes; it runs when called again with `confirm: true`. This is a speed bump for the assistant, not a human gate: the human gate is the client's own approval of tool calls, and the documentation says so.
- `--no-confirm` turns confirmation off, for sandboxes and throwaway agents.

**Read-only mode** (`--read-only` for the whole server, or `read_only: true` on a call, or any connection-string target without `--allow-external-writes`):

1. The classifier refuses changes before they reach the server, and also refuses transaction control (`BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT`, `SET TRANSACTION`, `SET SESSION CHARACTERISTICS`, `SET default_transaction_read_only`), since `COMMIT; DELETE ...` would otherwise end the read-only transaction and run the delete.
2. The call runs inside `BEGIN READ ONLY ... ROLLBACK`, which stops writes made inside functions the parser cannot see into.
3. Not covered: functions that act outside the transaction (`pg_terminate_backend`, `pg_cancel_backend`, `dblink`). Guarding those needs a restricted role, which is out of scope for 0.2; the documentation says so.

**`explain_query`** returns `EXPLAIN (FORMAT JSON)`. With `analyze: true` the statement runs inside a transaction that is always rolled back, so its changes never persist (sequences still advance); it is classified like the statement itself, so read-only mode still refuses a write.

**Results sized for an assistant.** `run_sql` returns at most `max_rows` rows per statement (default 100, up to 10,000) with a `truncated` flag and, when truncated, a hint to page with `ORDER BY` and a keyset (`WHERE id > <last>`) or `LIMIT`/`OFFSET`. Reading stops at the cap. Paging stays stateless on purpose: no `offset` argument that wraps the query (it fits only a single `SELECT`, and pages are unstable without `ORDER BY`), and no cursor held open between calls (it would hold a transaction open while the assistant thinks). It also shortens long values (default 2,000 characters, said in the result). Results come as structured content (columns, rows, affected rows, timing; `json` and `jsonb` values nested, not as strings) plus a compact Markdown table for clients that show text only. Errors are tool errors with the SQLSTATE, message, line and caret, detail and hint, so the assistant can correct the SQL.

**Progress.** `install_version` and `start_instance` send MCP progress notifications: bytes while downloading, then the verify and extract steps. Built on `v0.2-progress-aot`, with library 5.3, below.

**Protocol hygiene.** Standard output carries only protocol messages; everything else (library log, warnings) goes to standard error. The MCP host builds the core services directly, not through the CLI's console context, so no Spectre output can reach standard output.

**Packaging.** The tool package declares itself an MCP server (package type `McpServer` and `.mcp/server.json`), so nuget.org shows the configuration, and `dnx` runs it without an install. On npm, `npx -y pgembed mcp` does the same; `server.json` lists both packages, and the npm package names the server in `mcpName`, which the MCP registry checks.

### Also in 0.2 (proposed, built)

| Item | Why |
| --- | --- |
| Split the SQL runner into an executor and renderers | MCP needs results, not output; tests get simpler; it is the base for any later front end |
| `format` and `sql --check` summaries | so `format_sql` and `check_sql` have CLI equivalents (parity rule); both are small, on top of pgsqlparser |
| Download progress in `install`, `start` and MCP | the first `start` of a version downloads 10 to 60 MB with no feedback today; needs `IProgress` in library 5.3 |
| `explain_query` | the plan is the most useful thing an assistant can read about a slow query |
| `json` and `jsonb` values as nested JSON in `--format json` and in `run_sql` (by column type) | today they come out as escaped strings, so `jq` and assistants have to decode them twice |
| JSON output without needless escaping (relaxed encoder in the shared options) | today quotes and apostrophes come out as `"` and `'` in every `--json` output |
| Line breaks inside `markdown` cells written as `<br>` | today a value with a line break (a `json` or `text` value) breaks the table row |
| Pretty-printed `json` and `jsonb` values in the expanded `table` view (`\x`) | long documents are hard to read when wrapped at the column width; the normal grid stays compact |
| A pager, as in psql: in a terminal, a result taller than the screen opens in `$PAGER` (default `less -SFX`, which exits at once when the result fits); `--no-pager` on `sql`, `\pager on\|off` in the prompt | today a long result scrolls past, and the only way to page is piping `--format aligned` into `less -S` |
| Stop reading at the row cap: once `--max-rows` rows are read, the rest of the query is cancelled and the footer says "first N rows shown" | today the reader fetches and discards every row past the cap, so a `SELECT` over 10 million rows streams all of them to show 500 |

## Library work: MysticMind.PostgresEmbed

### 5.2 (released)

Six small, generic additions, each useful to a test author who never uses the CLI. Additive, no breaking changes:

1. **`PgServerOptions.InstanceName`:** a stable, readable instance folder instead of a GUID. With `PGEMBED_CACHE_DIR` set, named instances live in `<cache>/instances/<name>`, where the CLI, MCP and tests meet.
2. **Attach if running** (default for named instances): `StartAsync` on a named instance that is already running attaches instead of starting another server; the port comes from `postmaster.pid`. Only the process that started a server stops it on dispose.
3. **A lock per instance,** so two processes cannot start or stop the same named instance at once.
4. **Across processes:** `GetStatusAsync()` (running, PID, port, start time), `StopAsync()` for a named instance started by another process, and a new `KillAsync()`.
5. **Shared cache:** `PGEMBED_CACHE_DIR` as the default `BinariesDir` (`<cache>/binaries`), and a cross-process lock so each archive is downloaded once.
6. **`PgPlatform.Detect()`:** the detected OS, CPU and binaries artifact name, so the CLI does not duplicate platform detection.

### 5.3 (released 2026-11-08)

- **Install progress:** `PgServerOptions.InstallProgress`, an `IProgress<PgInstallProgress>` (step: download, verify, extract; the item; bytes received and total), reported only when a download or install happens. A test runner benefits on its own (a first CI run shows why it is slow), so it passes the library rule. pgembed shows it as a progress bar in the terminal and as MCP progress notifications (branch `v0.2-progress-aot`). Also in 5.3: `RequireChecksum`, which refuses a mirror's download that has no published SHA-256.

Later, when needed: resumable downloads.

## Stages

| Release | Contents |
| --- | --- |
| Library 5.1.0 | released 2026-11-06: version specs, `InstallAsync`, extension discovery, checksum verification |
| Library 5.2.0 | released 2026-11-06: the six items above |
| Library 5.2.1 | released 2026-11-06: a killed server's shared memory is removed, so kills no longer exhaust macOS's 32 System V segments |
| pgembed 0.1 | built: `versions`, `install`, `extensions`; instances (`start`/`stop`/`kill`/`restart`/`status`/`connstr`/`logs`/`rm`; background by default, `--foreground`, several versions); `sql` against instances or any connection string, with parser-based splitting, `--check`, `--schema`, confirmation of changes and the `aligned` format; the interactive prompt; `schema` and the `\d` commands; `connstr` in every format; `doctor`, `cache list`/`clean` |
| Library 5.3.0 | released 2026-11-08: install progress, `RequireChecksum`, archive links kept inside the install; a docs site |
| pgembed 0.2 | built: MCP server; SQL executor split with streaming; `format`; `sql --check` summaries; JSON values in results (nested in JSON output, pretty in `\x`), relaxed JSON escaping, line breaks in Markdown cells; a pager; reading stops at the row cap; instance names validated; precompiled packages per platform on .NET 10; the performance budgets met and checked in CI; a locale that exists on Linux images without `en_US.UTF-8`; download progress (with library 5.3) |
| pgembed 0.3 | web UI (`pgembed ui`), see below |
| When there is demand | the backlog below |

Scope is kept lean on purpose. 0.1 grew past the first plan (the prompt, `schema`, connection strings) because they were wanted while building it; the differentiators (several Postgres versions without Docker, including on machines and CI runners that cannot run Docker, and a throwaway real Postgres for AI assistants) come first, and the rest waits for real demand (issues, downloads, MCP usage).

### Before releasing 0.2

The work is stacked on three branches: `v0.2` (the features), `v0.2-aot` on it (System.CommandLine, generated
JSON, Native AOT packaging), and `v0.2-progress-aot` on that (download progress, with library 5.3.0).

1. ~~Publish MysticMind.PostgresEmbed 5.3.0.~~ Done on 2026-11-08.
2. Merge `v0.2-progress-aot` into `main` (it moves the library reference to `[5.3.0, 6.0.0)`).
3. Check that CI's `aot` job passes on Windows: the native Windows build is the one platform not yet run by hand.
4. Run `publish.yaml`; then, to list the server in the official MCP Registry, `mcp-publisher publish src/pgembed/.mcp/server.json` (the README already names it, as the registry requires).

### Backlog

Planned in the first draft of this spec and not built in 0.1, plus later ideas:

- `stop --immediate` (abort sessions instead of waiting for them).
- `cache clean --unused-for <age>`, with last-used tracking per install, and a last-used column in `cache list`.
- `cache refresh` (today: `versions --refresh`).
- `--quiet` as a common option.
- `ext add` with curated extensions (the planned `mysticmind-postgres-binaries`), and a remote extension catalog.
- Resumable downloads.

## pgembed 0.3: web UI (decided: in 0.3; details proposed)

`pgembed ui` serves a local web page over the same core, a third front end beside the CLI and MCP. It does not try to match DataGrip, DBeaver or pgAdmin on breadth; it does what only pgembed can:

- **Instances:** every instance with its version, port and state; start, stop, restart, logs; new instances of several versions side by side.
- **Query editor:** the prompt's parser features in the browser (highlighting, syntax errors as you type, confirmation of changes), with a virtualized results grid that **pages through a server-side cursor** held for the browser session, so scrolling a large result fetches the next page instead of loading everything.
- **Schema browser and diagram:** from the schema reader, foreign keys drawn as relationships.
- **Visual EXPLAIN:** the JSON plan from `explain_query`, drawn as a tree with costs and timings.
- **Compare versions:** run one query on several instances (16, 17, 18) and compare results and plans.

Shape: Kestrel on localhost, a random token in the URL (as Jupyter does) and a Host header check against DNS rebinding; the browser opens on start. The frontend is prebuilt and embedded in the tool, so nothing runs Node at runtime (CodeMirror 6 for the editor keeps it light). Open question: it needs the ASP.NET Core runtime, which a runtime-only .NET install may lack; the alternative is a separate `pgembed-ui` package. 0.3 starts as a time-boxed spike of instances and the query editor; the rest follows usage.

## Under consideration

- **A project file** (`pgembed.json` next to the code): the instances a project needs (name, version, parameters, extensions, seed scripts), started with one command. Not yet specified.

## Out of scope for now

- Managing Postgres servers that pgembed did not start (starting, stopping, logs). `sql`, `schema` and the MCP SQL tools do connect to any server by connection string.
- Backups, users and roles, migrations: other tools do these well.
- Wrapping `psql` or `pg_dump`: zonky's binaries do not include them.
- Running as root: Postgres refuses, so `doctor` reports it.
