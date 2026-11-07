#!/usr/bin/env bash
# Samples for docs/guide/schema.md and docs/guide/format.md.
set -euo pipefail
pgembed start app --version 17 >/dev/null
pgembed sql app --yes "CREATE TABLE books(id int PRIMARY KEY, title text NOT NULL);
  CREATE SCHEMA sales; CREATE TABLE sales.orders(id int PRIMARY KEY, book_id int REFERENCES books(id));" >/dev/null

# region sample_schema
pgembed schema app                          # tables and views, with row estimates and sizes
pgembed schema app books                    # one table: columns, keys, indexes, constraints
pgembed schema app "sales.*"                # every table in sales
pgembed schema app books --json             # structured, for scripts and tools
# endregion

# region sample_describe_as_sql
pgembed sql app "\d books"
pgembed sql app "\dt sales.*"
# endregion

echo "select id,title from books where id=1" > query.sql
cp query.sql other.sql

# region sample_format
pgembed format "select id,title from books where id=1"
pgembed format -f query.sql --write                    # rewrite the file
pgembed format -f query.sql -f other.sql --check || true   # exits 1 when one is not formatted: a CI lint
# endregion

pgembed rm app --force --yes
