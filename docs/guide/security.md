# Security

pgembed runs real PostgreSQL servers as you. That shapes what it protects, and what it leaves to you.

## What an instance is

- It listens on this machine only (`localhost`), never on the network.
- It runs as your user, and connections log in as the `postgres` superuser. A superuser can read and write
  your files and run programs, with your permissions. Everything below follows from that.
- It trusts local connections: no password, by default. The Unix socket is private to you (0700), so on Linux
  and macOS only you can use it; TCP connections from this machine are trusted.

**On a machine you share with other users**, start instances with a password, so other users cannot connect
over TCP:

```console
pgembed start app --version 17 --password
pgembed connstr app            # the connection string, with the password, for your app
```

The password is generated, kept in the instance's settings (readable only by you), and used by pgembed's own
commands and its MCP server. Tests that attach through MysticMind.PostgresEmbed connect without a password, so
they need the connection string from `pgembed connstr` on such an instance.

## SQL that reaches outside the database

Statements that change the schema or existing rows ask before they run (see [SQL](./sql#changes-are-confirmed-first)).
So does anything that reaches outside the database or cannot be checked:

- code: `DO` blocks, `CALL`, `EXECUTE` of a prepared statement
- libraries and programs: `LOAD`, `COPY ... PROGRAM`
- files: `COPY` to or from a file, and the built-in functions that read or write files (`pg_read_file`,
  `lo_import`, `lo_export` and the like)
- other servers and the server itself: `dblink`, `pg_terminate_backend`, `pg_reload_conf` and the like
- SQL given as text: `query_to_xml`, `ts_stat`

Functions you define are not looked into: a superuser can do anything through them.

pgembed and the server must read SQL the same way, or a statement could hide where they differ. So every
connection pins how strings are read (`standard_conforming_strings`), changing it in SQL is refused by the MCP
server and read-only mode (and asks first in `sql`), and each command reaches the server as exactly one statement:
anything that would split differently is refused by the server rather than run. A multi-statement script that
pgembed's parser cannot read therefore has to be split into single statements.

## AI assistants (MCP)

An assistant can be steered by text it reads: a query result, a document, a web page. pgembed assumes it might
be, and holds these lines whatever it asks:

- **Changes ask you.** Clients that support it ask you directly; otherwise the tool refuses until the assistant
  calls again with `confirm: true`, and your client's approval of tool calls is the check. `--no-confirm` turns
  this off: use it only in a sandbox.
- **Read-only mode is enforced by the server.** It refuses writes, transaction control and everything in the list
  above before anything runs, runs the SQL in a read-only transaction and, on PostgreSQL 14 and later, as the
  built-in `pg_read_all_data` role: it reads every table, but cannot touch files, programs or libraries, even
  through a function the check cannot see into.
- **Servers pgembed did not start are read-only** unless you pass `--allow-external-writes`.
- **Passwords stay out of the conversation.** Give other servers as [named connections](./mcp#other-servers-and-passwords);
  password-protected instances' passwords are masked in what tools return; errors never repeat a password.
- **Runaway queries stop**: each statement gets 120 seconds (`--statement-timeout`).

## Your terminal

Data can carry terminal control sequences (to retitle the window, write the clipboard or hide text). In a
terminal, pgembed shows them as symbols (`␛` for ESC) instead of sending them; files and pipes get the data
byte for byte.

## Downloads

- Every Postgres archive is checked against the SHA-256 Maven publishes, and the repository must be https (or
  on this machine). An http mirror on a network you trust needs `PGEMBED_ALLOW_HTTP_REPO=1`.
- An extension (`--extension`) over plain http needs its checksum: `<url>#sha256=<hex>`.

## Secrets on your machine

- Instance settings (with a generated password) and the prompt's history (the SQL you typed) are readable only
  by you.
- Keep passwords for other servers out of command lines, where shell history and process lists show them: use
  `PGPASSWORD` or `~/.pgpass`.
