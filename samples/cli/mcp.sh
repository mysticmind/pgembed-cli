#!/usr/bin/env bash
# Samples for docs/guide/mcp.md. Adding a server changes the assistant's configuration, so these do not run in CI.
set -euo pipefail

if [[ "${RUN_SKIPPED_SAMPLES:-}" == 1 ]]; then
  # region sample_mcp_claude_code
  claude mcp add pgembed -- dnx pgembed mcp
  # endregion

  # region sample_mcp_options
  pgembed mcp --read-only                 # refuse anything that writes, on every target
  pgembed mcp --no-confirm                # no questions: for sandboxes and throwaway agents
  pgembed mcp --allow-external-writes     # allow writes to servers pgembed did not start
  # endregion
fi
