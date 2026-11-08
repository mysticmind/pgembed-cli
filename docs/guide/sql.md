# SQL

## Run SQL

snippet: sample_sql_basics

Scripts are split into statements with the real PostgreSQL parser, so dollar-quoted function bodies and
semicolons inside strings are handled. Each statement's result is shown as it completes. Each statement reaches
the server on its own, also in a script with syntax newer than the parser knows (it then asks first). An error shows the
line and points at the position:

```txt
$ pgembed sql app "SELECT id FROM books WHERE titel = 1"
ERROR: column "titel" does not exist
LINE 1: SELECT id FROM books WHERE titel = 1
                                   ^
HINT: Perhaps you meant to reference the column "books.title".
```

## Any server

The target is an instance name or a connection string in any common format: a `postgresql://` URI, libpq
`key=value`, Npgsql or JDBC.

snippet: sample_sql_connection_strings

Keep passwords off the command line: `PGPASSWORD` and the `~/.pgpass` file are used when the connection
string has none.

## Database and schema

snippet: sample_sql_db_and_schema

## Output formats

snippet: sample_sql_formats

| Format | |
| --- | --- |
| `table` | Fits the terminal, wrapping long values. The default in a terminal |
| `aligned` | psql-style text, never wrapped, one row per line. The default when output goes to a file or pipe |
| `json` | Every result, for scripts (`--json` is the same); `json` and `jsonb` values are nested JSON, one row per line |
| `csv` | Header and rows |
| `markdown` | A table for docs and issues; line breaks in values become `<br>` |

Results stream: rows are written as they arrive, so a million-row export to `csv` or `json` uses no more
memory than a one-row query. The `table` view shows the first `--max-rows` rows (default 500) and stops the
query there, instead of reading the rest.

In a terminal, a result taller than the screen opens in a pager, as in psql: `PGEMBED_PAGER`, else `PAGER`,
else `less -SFX` (long lines scroll sideways; a short result just prints). Set `PAGER=` to an empty value,
pass `--no-pager`, or use `\pager off` in the prompt to turn it off.

## Changes are confirmed first

Statements that change the schema (`CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `SELECT INTO` and the like) or
change existing rows (`UPDATE`, `DELETE`, `MERGE`, data-modifying `WITH`, and these under `EXPLAIN ANALYZE`)
are listed before anything runs, and pgembed asks before running them. An `UPDATE` or `DELETE` without
`WHERE` is called out. Reads and `INSERT` run without asking.

```txt
$ pgembed sql app "DELETE FROM books"
This statement changes the database:
  ! DELETE FROM books  deletes every row of books (no WHERE)
Run it? [y/n] (n):
```

Where there is no terminal to ask on (piped SQL, CI), such statements are refused unless you pass `--yes`:

snippet: sample_sql_confirm

The check reads the SQL; it cannot see inside functions, so `SELECT my_cleanup()` runs without asking.

## Check without running

`--check` needs no server. It checks the syntax and summarizes each statement:

snippet: sample_sql_check

```txt
✓ Valid SQL: 1 statement
1  DELETE  writes books
           ! deletes every row of books (no WHERE), asks first
```

## Interactive prompt

Without SQL to run, `sql` opens a prompt:

snippet: sample_prompt

- syntax highlighting, and syntax errors underlined as you type
- Enter runs the SQL once it ends with a semicolon; until then it starts a new line
- Tab completes keywords, schemas, tables and columns
- history, kept between sessions
- Ctrl+C cancels a running statement; `\q` quits

| Command | |
| --- | --- |
| `\d [name]` | Tables, views and sequences; or one table's columns, indexes and constraints |
| `\dt`, `\dv`, `\df`, `\dn` `[pattern]` | Tables, views, functions, schemas (`*` and `?` wildcards) |
| `\l` | Databases |
| `\c <database>` | Connect to another database |
| `\conninfo` | The connection string, password hidden |
| `\x [on\|off]` | Expanded display: one block per row; `json` values indented |
| `\timing [on\|off]` | Show how long statements take |
| `\format <format>` | `table`, `aligned`, `json`, `csv` or `markdown` |
| `\pager [on\|off]` | Open results taller than the screen in a pager |
| `\i <file>` | Run a SQL file |
| `\?`, `\q` | Help, quit |

Every option is in the [command reference](../reference/commands#sql).
