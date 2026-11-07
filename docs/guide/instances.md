# Instances

An instance is a named Postgres server with its own data folder. It is created on first start and keeps
its version, port and settings, so later commands only need its name.

## Start

snippet: sample_start_instances

An instance keeps its major version: its data can only be opened by that major. To move to another major,
create a new instance. Starting an instance that is already running, whether started by pgembed, by the MCP
server or by a test, attaches to it instead of starting another server.

Settings and a fixed port:

snippet: sample_start_options

| Option | Meaning |
| --- | --- |
| `-v, --version` | Exact (`17.6.0`), major (`17`), minor (`17.6`) or `latest`; comma-separated for several |
| `-p, --port` | Port; default 5432 when free, otherwise any free port. Kept for later starts |
| `--param KEY=VALUE` | A server setting. Repeatable |
| `--extension URL` | An extension zip to install; add `#sha256=<hex>` to verify it. Repeatable |
| `--locale` | Locale for a new instance's data; default `en_US.UTF-8`, or `C.UTF-8` on Linux systems without it |
| `--foreground` | Stay attached to the terminal instead of running in the background |

To keep Postgres in the foreground, for systemd, supervisord or a container's main process:

snippet: sample_foreground

## Inspect

snippet: sample_inspect_instances

```txt
  NAME   VERSION   PORT   PID     STATE     UPTIME   DATA
 ───────────────────────────────────────────────────────────
  app    17.11.0   5432   78419   running   2h 14m   46 MB
```

## Connection strings in scripts

`connstr` prints every format, or just one with `--format`: `npgsql`, `uri`, `libpq`, `jdbc` or `env`.

snippet: sample_connstr_in_scripts

## Stop and remove

snippet: sample_stop_instances

`rm` asks before deleting an instance's data, and needs `--force` for a running one. When `stop` does not
work:

snippet: sample_kill
