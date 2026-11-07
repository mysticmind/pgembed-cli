# Introduction

pgembed runs real PostgreSQL servers on your machine without installing Postgres and without Docker. It
downloads the official binaries for your platform (the [zonky embedded-postgres-binaries](https://github.com/zonkyio/embedded-postgres-binaries),
verified against the SHA-256 Maven publishes), keeps named instances of any version running in the
background, and lets you query and inspect them.

The binaries are minimal: just the server, `initdb` and `pg_ctl`. A version is about a 14 MB download on
Linux and 24 MB on Windows (30 to 70 MB on macOS), against 112 to 154 MB for the official Docker image,
and it downloads once into a cache shared by every instance of that version.

It is a .NET tool, built on the [MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed)
library, and is also an MCP server for AI assistants.

## What it is for

- **Local databases for development.** `pgembed start app --version 17` gives you a Postgres that survives
  restarts of your terminal and keeps its port, so its connection string never changes.
- **Several versions at once.** Test against 16, 17 and 18 side by side, on a laptop or a CI runner that
  cannot run Docker.
- **Tests.** Instances live in the library's shared cache, so a test using `InstanceName = "app"` attaches
  to the instance you started, or starts it when it is not running. See [From .NET tests and apps](./testing).
- **AI assistants.** `pgembed mcp` gives an assistant a real, throwaway Postgres it can start, query, explain
  and inspect, with read-only mode and confirmation of changes. See [MCP](./mcp).

## What it is not

- It does not manage Postgres servers it did not start. `sql` and `schema` connect to any server by
  connection string, but starting, stopping and logs are for pgembed instances.
- Backups, roles and migrations are left to the tools that do them well.
- It does not wrap `psql` or `pg_dump`: the binaries it uses do not include them. `pgembed sql` has its own
  interactive prompt.
- Instances are for development and testing: they listen on localhost and trust local connections without a
  password.

## Requirements

The .NET 10 SDK or newer, on Windows x64, macOS (x64, arm64) or Linux (x64, arm64, glibc or musl). On Linux
and macOS, Postgres refuses to run as root, so run pgembed as a normal user; in a container, add one.
