#!/usr/bin/env bash
# Samples for docs/guide/instances.md.
set -euo pipefail

# region sample_start_instances
pgembed start app --version 17          # background; prints the connection string
pgembed start app                       # already running? attaches and prints it again
pgembed start ci --version 16,17,18     # three instances side by side: ci-16, ci-17, ci-18
# endregion

# region sample_start_options
pgembed start tuned --version 17 --port 5433 \
  --param shared_buffers=256MB --param max_connections=300
# endregion

# region sample_inspect_instances
pgembed status                          # version, port, process, state, uptime, data size
pgembed connstr app                     # every format: npgsql, uri, libpq, jdbc, env
pgembed connstr app --format uri        # just one, for scripts
pgembed logs app --tail 20
# endregion

# region sample_connstr_in_scripts
export DATABASE_URL="$(pgembed connstr app --format uri)"
pgembed connstr app --format env > .env                      # PGHOST, PGPORT, PGUSER, PGDATABASE
set -a; eval "$(pgembed connstr app --format env)"; set +a   # or export them in this shell
# endregion

# region sample_stop_instances
pgembed restart app
pgembed stop app
pgembed stop 'ci-*'                     # wildcards; or: pgembed stop --all
pgembed rm tuned --force --yes          # delete an instance and its data
# endregion

# These block or need a person, so they only run when asked: RUN_SKIPPED_SAMPLES=1
if [[ "${RUN_SKIPPED_SAMPLES:-}" == 1 ]]; then
  # region sample_foreground
  pgembed start dev --version 17 --foreground   # streams the log; Ctrl+C stops Postgres cleanly
  # endregion

  # region sample_kill
  pgembed kill app                        # last resort, when stop does not work
  # endregion
fi

pgembed rm app 'ci-*' --force --yes
