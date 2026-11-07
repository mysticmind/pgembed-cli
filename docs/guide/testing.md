# From .NET Tests and Apps

pgembed keeps its instances where [MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed)
looks for named instances. A test that asks the library for `InstanceName = "app"` attaches in milliseconds
when `pgembed start app` already runs it, and starts it itself when it does not. Only the process that
started a server stops it, so tests leave your instance running.

These samples are an xUnit project that CI runs against real Postgres.

## An xUnit fixture

snippet: sample_xunit_fixture

snippet: sample_xunit_test

## Every Postgres version

Start the instances once with `pgembed start ci --version 16,17,18`, or let each test start its own:

snippet: sample_each_version

## From an app

Pass the connection string pgembed prints, here through configuration:

snippet: sample_app_connection_string

`pgembed connstr app` prints it in every format: Npgsql, `postgresql://` URI, libpq, JDBC, and `PG*`
environment variables.
