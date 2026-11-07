# Schema

snippet: sample_schema

For a table, `schema` shows its columns (type, nullability, default, identity, generated, primary key),
its indexes, and its constraints with foreign key targets. `--json` gives one structured document per
table, for scripts and tools.

The same descriptions work as psql-style commands, in the [interactive prompt](./sql#interactive-prompt) and
as one-shot SQL:

snippet: sample_describe_as_sql
