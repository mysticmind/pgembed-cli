#!/usr/bin/env bash
# Samples for docs/guide/sql.md. Runs in a scratch folder, so the files it writes are throwaway.
set -euo pipefail
pgembed start app --version 17 >/dev/null
cat > seed.sql <<'SQL'
CREATE TABLE books(id int PRIMARY KEY, title text, doc jsonb);
INSERT INTO books VALUES (1, 'Dune', '{"tags": ["sf"]}'), (2, 'Emma', NULL);
SQL
echo "SELECT count(*) FROM books;" > report.sql

# region sample_sql_basics
pgembed sql app -f seed.sql --yes                     # schema changes need --yes without a terminal
pgembed sql app "SELECT * FROM books ORDER BY id"
pgembed sql app -f report.sql --timing
cat report.sql | pgembed sql app                      # or pipe it in
# endregion

# region sample_sql_formats
pgembed sql app "SELECT * FROM books" --format csv > books.csv
pgembed sql app "SELECT * FROM books" --format json   # json and jsonb values come out nested
pgembed sql app "SELECT * FROM books" --format markdown
# endregion

# region sample_sql_db_and_schema
pgembed sql app "CREATE SCHEMA sales; CREATE TABLE sales.orders(id int)" --yes
pgembed sql app --schema sales "SELECT * FROM orders"
# endregion

set -a; eval "$(pgembed connstr app --format env)"; set +a

# region sample_sql_connection_strings
pgembed sql "postgresql://$PGUSER@$PGHOST:$PGPORT/$PGDATABASE" "SELECT 1"
pgembed sql "host=$PGHOST port=$PGPORT user=$PGUSER dbname=$PGDATABASE" "SELECT 1"
pgembed sql "Host=$PGHOST;Port=$PGPORT;Username=$PGUSER;Database=$PGDATABASE" "SELECT 1"
pgembed sql "jdbc:postgresql://$PGHOST:$PGPORT/$PGDATABASE?user=$PGUSER" "SELECT 1"
# endregion

# region sample_sql_check
pgembed sql --check "DELETE FROM books"               # syntax and a summary; needs no server
pgembed sql --check -f seed.sql
# endregion

# region sample_sql_confirm
pgembed sql app "DELETE FROM books WHERE id = 2" --yes
# endregion

# The prompt needs a person, so it only runs when asked: RUN_SKIPPED_SAMPLES=1
if [[ "${RUN_SKIPPED_SAMPLES:-}" == 1 ]]; then
  # region sample_prompt
  pgembed sql app                                     # no SQL: opens the interactive prompt
  # endregion
fi

pgembed rm app --force --yes
