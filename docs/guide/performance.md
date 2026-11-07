# Performance

pgembed aims to feel instant: a command that does not wait on Postgres or the network finishes before you
notice, and large results stream at the speed of the server.

Measured on macOS arm64 against a running Postgres 17 instance, as shipped (the precompiled platform
package), with `psql` 14 for comparison:

| | pgembed | psql |
| --- | --- | --- |
| `--version` | 54 to 75 ms | |
| `connstr <instance>` | 59 to 80 ms | |
| `sql <instance> "SELECT 1"` | 101 to 130 ms | 40 ms |
| `sql --check "SELECT 1"`, `format "select 1"` | 111 to 140 ms | |
| `start <instance>`, attaching to a running one | 93 ms | |
| 1M rows (two columns) as CSV | 2.3 s, 130 MB | 1.9 s, 121 MB |
| 1M-row table, `table` view capped at 500 | 0.15 to 0.27 s | |
| 5M rows as CSV | 8.8 s, 130 MB | |
| Prompt, per keystroke, 200-line statement | 1 ms | |
| MCP tool call, warm | 1 to 20 ms | |

Ranges are runs on a quiet and on a busy machine. Memory does not grow with the row count: 130 MB is the
process with the .NET runtime, the PostgreSQL parser and Npgsql, the same for one row or five million.

## How

- **Precompiled.** Each platform gets a ReadyToRun package, so there is no JIT compilation at startup.
- **Streaming.** Rows are written as they arrive; `aligned` measures column widths over the first 1,000 rows,
  as psql does. The `table` view stops the query at its row cap instead of reading the rest.
- **Only what is needed.** Plain reads skip the full parse; an instance's port comes from Postgres's own
  `postmaster.pid`; the prompt warms the parser while it connects.
- **Warm connections in MCP.** The server keeps a pooled connection per target.

## See where the time goes

```console
$ PGEMBED_TRACE=1 pgembed sql app "SELECT 1"
    22 ms  main
    50 ms  configured
    64 ms  sql command
    68 ms  target resolved
    76 ms  checked
   113 ms  connected
   123 ms  done
```

The budgets are checked in CI on every change (`bench/budgets.py`), and `bench/pgembed.Benchmarks` measures
the core with BenchmarkDotNet.
