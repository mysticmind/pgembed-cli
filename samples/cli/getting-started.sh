#!/usr/bin/env bash
# Samples for docs/guide/getting-started.md. Run by samples/run-cli-samples.sh, so every command shown works.
set -euo pipefail

# region sample_first_instance
pgembed start app --version 17          # downloads Postgres 17 once, starts it in the background
pgembed sql app "SELECT version()"
pgembed stop app
# endregion

# Installing changes the machine, so it only runs when asked: RUN_SKIPPED_SAMPLES=1
if [[ "${RUN_SKIPPED_SAMPLES:-}" == 1 ]]; then
  # region sample_install_tool
  dotnet tool install --global pgembed
  # endregion

  # region sample_dnx
  dnx pgembed versions                  # or run it without installing
  # endregion
fi

pgembed rm app --yes
