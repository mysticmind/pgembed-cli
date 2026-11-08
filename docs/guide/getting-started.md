# Getting Started

## Install

snippet: sample_install_tool

On .NET 10 you can also run it without installing:

snippet: sample_dnx

Each platform (Windows x64, Linux x64 and arm64 with glibc or musl, macOS x64 and arm64) gets a native
executable, compiled ahead of time, so commands start in a few milliseconds; other platforms get a portable
build.

## Your first instance

snippet: sample_first_instance

`start` prints the connection string:

```txt
● Started app: PostgreSQL 17.11.0 on port 5432
  Host=localhost;Port=5432;Username=postgres;Database=postgres
```

The first start of a version downloads it (about 14 MB on Linux, 24 MB on Windows, 30 to 70 MB on macOS)
with a progress bar; later starts take about a second, and starting an instance that already runs
attaches to it instantly.

## Next

- [Instances](./instances): several versions, ports, settings, connection strings, logs.
- [SQL](./sql): scripts, output formats, the interactive prompt, confirmation of changes.
- [From .NET tests and apps](./testing): use instances from xUnit and from your app.
- [MCP](./mcp): give your AI assistant a Postgres.
