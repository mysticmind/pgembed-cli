# Performance

pgembed aims to feel instant: a command that does not wait on Postgres or the network finishes before you
notice, and large results stream at the speed of the server.

Measured on macOS arm64 against a running Postgres 17 instance, as shipped (the Native AOT platform
package), with `psql` 14 for comparison:

| | pgembed | psql |
| --- | --- | --- |
| `--version` | 15 to 19 ms | |
| `connstr <instance>` | 15 to 20 ms | |
| `status` (one instance) | 19 to 39 ms | |
| `sql <instance> "SELECT 1"` | 23 to 38 ms | 40 ms |
| `sql --check "SELECT 1"`, `format "select 1"` | 37 to 71 ms | |
| `start <instance>`, attaching to a running one | 17 ms | |
| 1M rows (two columns) as CSV or JSON | 1.7 s, 39 MB | 1.9 s, 121 MB |
| 1M-row table, `table` view capped at 500 | 36 ms | |
| 5M rows as CSV | 8.5 s, 41 MB | |
| Prompt, per keystroke, 200-line statement | 1 ms | |
| MCP server start; a warm tool call | 31 ms; 1 to 20 ms | |

Ranges are runs on a quiet and on a busy machine. Memory does not grow with the row count: about 40 MB for one
row or five million.

## How

- **Native AOT.** Each platform gets a native executable, compiled ahead of time: no JIT, no runtime to start.
  The command line runs on System.CommandLine and JSON serialization is generated at build time, both of
  which compile ahead of time; Spectre.Console draws the tables and progress bars.
- **Streaming.** Rows are written as they arrive; `aligned` measures column widths over the first 1,000 rows,
  as psql does. The `table` view stops the query at its row cap instead of reading the rest.
- **Only what is needed.** Plain reads skip the full parse; an instance's port comes from Postgres's own
  `postmaster.pid`; the prompt warms the parser while it connects.
- **Warm connections in MCP.** The server keeps a pooled connection per target.

## See where the time goes

```console
$ PGEMBED_TRACE=1 pgembed sql app "SELECT 1"
    11 ms  main
    21 ms  configured
    21 ms  sql command
    21 ms  target resolved
    22 ms  checked
    29 ms  connected
    29 ms  done
```

The budgets are checked in CI on every change (`bench/budgets.py`), and `bench/pgembed.Benchmarks` measures
the core with BenchmarkDotNet.
