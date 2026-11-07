<!-- Generated from the MCP tool definitions by DocsTests; run the tests with PGEMBED_UPDATE_DOCS=1 to update. -->

### `check_sql`

*read-only, idempotent*

Check SQL syntax without a server, and summarize each statement: its kind, the tables it reads, writes or defines, the functions it calls, and whether run_sql would ask for confirmation.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `sql` | string | required | SQL: one or more statements |

### `describe_schema`

*read-only, idempotent*

The tables, views and foreign tables in a database, with row estimates and sizes.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `target` | string | required | Instance name, or a connection string |
| `database` | string | optional | Database (default: postgres for an instance) |
| `schema` | string | optional | Only this schema |

### `describe_table`

*read-only, idempotent*

One table or view in full: columns (type, nullability, default, identity, generated, primary key), indexes, and constraints with foreign key targets.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `target` | string | required | Instance name, or a connection string |
| `table` | string | required | Table or view, optionally schema-qualified (sales.orders) |
| `database` | string | optional | Database (default: postgres for an instance) |
| `schema` | string | optional | Schema where an unqualified name is looked up |

### `doctor`

*read-only, idempotent*

Check that PostgreSQL can run on this machine: platform, user, Maven reachable, cache writable, folder usable, port free.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `port` | integer | default `5432` | Port to check is free (default 5432) |

### `explain_query`

The query plan, as JSON (EXPLAIN FORMAT JSON). With analyze, the statement really runs, inside a transaction that is always rolled back, so its changes never persist (sequences still advance); actual times and row counts are included.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `target` | string | required | Instance name, or a connection string |
| `sql` | string | required | One statement |
| `analyze` | boolean | default `false` | Run it and include actual times and rows (rolled back afterwards) |
| `database` | string | optional | Database (default: postgres for an instance) |
| `schema` | string | optional | Schema for unqualified names |

### `format_sql`

*read-only, idempotent*

Format SQL with the PostgreSQL parser, keeping comments. No server needed.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `sql` | string | required | SQL: one or more statements; comments are kept |

### `get_connection_string`

*read-only, idempotent*

The connection string of a running instance.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name, as list_instances shows it |
| `format` | string | default `npgsql` | npgsql (default), uri, libpq, jdbc or env |
| `database` | string | optional | Database (default: postgres) |
| `schema` | string | optional | Schema for unqualified names (sets the search path) |

### `get_logs`

*read-only, idempotent*

The last lines of an instance's server log: startup messages, errors, slow statements.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name, as list_instances shows it |
| `lines` | integer | default `50` | Lines from the end (default 50, at most 1000) |

### `install_version`

*idempotent*

Download, verify and extract a PostgreSQL version ahead of time, so a later start_instance is fast.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `version` | string | required | Exact (17.6.0), major (17), minor (17.6) or latest |

### `kill_instance`

*destructive, asks first*

Force-kill an instance's server processes, as a last resort when stop_instance does not work. Asks for confirmation.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name, as list_instances shows it |
| `confirm` | boolean | default `false` | Set to true, after the user agreed, when the client cannot ask the user itself |

### `list_extensions`

*read-only, idempotent*

The extensions CREATE EXTENSION can use in a PostgreSQL version: name, default version, what it requires, description.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `version` | string | required | Exact (17.6.0), major (17), minor (17.6) or latest |

### `list_instances`

*read-only, idempotent*

Every pgembed instance on this machine: name, version, port, state, uptime, data size.


### `list_versions`

*read-only, idempotent*

PostgreSQL versions published for this machine: the newest of each major, or every version of one major.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `major` | string | optional | A major, for example 17, to list all of its versions |

### `remove_instance`

*destructive, asks first*

Delete an instance and all of its data, stopping it first if it runs. Asks for confirmation.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name, as list_instances shows it |
| `confirm` | boolean | default `false` | Set to true, after the user agreed, when the client cannot ask the user itself |

### `restart_instance`

Stop an instance and start it again with the same version, port and settings.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name, as list_instances shows it |

### `run_sql`

Run SQL against an instance (by name) or any server (by connection string). Each statement returns its rows (at most max_rows; the result says when it was truncated) or its affected rows. json/jsonb values come back as JSON. Statements that change the schema or existing rows ask for confirmation first. With read_only, anything that writes is refused and the SQL runs in a read-only transaction. Errors include the position, detail and hint, to correct the SQL. To page through a large result, use ORDER BY with a keyset (WHERE id > last) or LIMIT/OFFSET.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `target` | string | required | Instance name, or a connection string (Npgsql, postgresql:// URI, libpq or JDBC) |
| `sql` | string | required | SQL: one or more statements |
| `database` | string | optional | Database (default: postgres for an instance) |
| `schema` | string | optional | Schema for unqualified names (sets the search path) |
| `max_rows` | integer | default `100` | Rows returned per statement (default 100, at most 10000) |
| `read_only` | boolean | default `false` | Refuse anything that writes, and run in a read-only transaction |
| `confirm` | boolean | default `false` | Set to true, after the user agreed to the changes, when the client cannot ask the user itself |

### `start_instance`

*idempotent*

Start a named PostgreSQL instance in the background, creating it on first use (give a version then). If it is already running, attaches to it. It keeps running after this conversation, and keeps its port. Returns the connection string.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name: letters, digits, '.', '_' and '-' |
| `version` | string | optional | Version for a new instance: exact (17.6.0), major (17), minor (17.6) or latest. Not needed to restart an existing one |
| `port` | integer | optional | Port; default 5432 when free, else any free port, remembered for later starts |

### `stop_instance`

*idempotent*

Stop a running instance cleanly. Its data is kept; start_instance starts it again.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | string | required | Instance name, as list_instances shows it |
