<!-- mcp-name: io.github.mysticmind/pgembed -->
<img src="https://raw.githubusercontent.com/mysticmind/pgembed-cli/main/assets/icon.png" alt="pgembed icon: an elephant standing on a terminal" width="96">

# pgembed [![npm](https://badgen.net/npm/v/pgembed)](https://www.npmjs.com/package/pgembed) [![NuGet](https://badgen.net/nuget/v/pgembed)](https://www.nuget.org/packages/pgembed/)

Run PostgreSQL on your machine without installing it. `pgembed` downloads minimal, small sized Postgres
binaries for your platform (Windows, macOS, Linux and Alpine, x64 and arm64), keeps named instances of any
version running in the background, and runs SQL against them. It is also an MCP server, so an AI assistant
can do the same.

**Documentation: [mysticmind.github.io/pgembed-cli](https://mysticmind.github.io/pgembed-cli/)**

```console
$ pgembed start app --version 17
● Started app: PostgreSQL 17.11.0 on port 5432
  Host=localhost;Port=5432;Username=postgres;Database=postgres

$ pgembed sql app "SELECT version()"
$ pgembed stop app
```

## Install

> [!NOTE]
> The npm package needs a fix and will be available again on 10 November 2026. Until then, install the .NET tool
> below, or use `dnx pgembed mcp` for the MCP server.

With npm (Node.js 18 or newer):

```console
npm install --global pgembed
npx pgembed status                      # or run it without installing
```

Or as a .NET tool (the .NET 10 SDK or newer):

```console
dotnet tool install --global pgembed
dnx pgembed status                      # or run it without installing
```

Both install the same native executable, compiled ahead of time, so commands start in a few milliseconds.
See [Getting Started](https://mysticmind.github.io/pgembed-cli/guide/getting-started).

## What it does

- **[Instances](https://mysticmind.github.io/pgembed-cli/guide/instances)**: named servers of any version,
  started in the background and kept between runs; several versions at once (`--version 16,17,18`).
- **[SQL](https://mysticmind.github.io/pgembed-cli/guide/sql)**: against an instance or any PostgreSQL
  server, with readable results and errors that point at the position. Changes to the schema or existing
  rows ask first. An interactive prompt with completion, highlighting and psql-style commands.
- **[Schema](https://mysticmind.github.io/pgembed-cli/guide/schema)**: tables, or one table's columns,
  keys, indexes and constraints.
- **[Format and check](https://mysticmind.github.io/pgembed-cli/guide/format)**: SQL formatting that keeps
  comments, and a syntax check for CI; neither needs a server.
- **[Versions and cache](https://mysticmind.github.io/pgembed-cli/guide/versions-and-cache)**: published
  versions, extensions, ahead-of-time installs, and a cache shared with
  [MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed), so your tests do not
  download a version again.
- **[Tests](https://mysticmind.github.io/pgembed-cli/guide/testing)** and
  **[CI](https://mysticmind.github.io/pgembed-cli/guide/ci)**: one server per test run, or one per version.

## MCP server for AI assistants

```console
claude mcp add pgembed -- npx -y pgembed mcp       # Claude Code; or: dnx pgembed mcp, with .NET 10
```

```json
{ "mcpServers": { "pgembed": { "command": "npx", "args": ["-y", "pgembed", "mcp"] } } }
```

The assistant gets tools for instances, SQL, schema and query plans. Changes ask you first, servers pgembed
did not start are read-only, and passwords stay out of the conversation. See
[With AI Assistants](https://mysticmind.github.io/pgembed-cli/guide/mcp) and the
[tool reference](https://mysticmind.github.io/pgembed-cli/reference/mcp-tools).

## Notes

- Instances are for development and testing: they listen on localhost and, unless started with
  `--password`, trust local connections. See [Security](https://mysticmind.github.io/pgembed-cli/guide/security).
- On Linux and macOS, Postgres refuses to run as root; in a container, add a normal user.
- The binaries come from [zonky embedded-postgres-binaries](https://github.com/zonkyio/embedded-postgres-binaries)
  and are verified against the SHA-256 that Maven publishes.

Every command and option: [Commands](https://mysticmind.github.io/pgembed-cli/reference/commands) and
[Configuration](https://mysticmind.github.io/pgembed-cli/reference/configuration).

## License

MIT
