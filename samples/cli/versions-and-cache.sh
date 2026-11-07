#!/usr/bin/env bash
# Samples for docs/guide/versions-and-cache.md.
set -euo pipefail

# region sample_versions
pgembed versions                        # newest version of each major
pgembed versions --major 17             # every 17.x
pgembed versions --platform linux-musl-x64
# endregion

# region sample_install_ahead
pgembed install 17                      # download ahead of time, for example in a Docker build or CI cache step
pgembed extensions 17                   # what CREATE EXTENSION can use in that version
# endregion

# region sample_cache
pgembed cache list                      # installs and downloads, with sizes and what is in use
pgembed cache clean --keep-latest --dry-run
pgembed doctor || true                  # platform, user, Maven, cache folder, port 5432
# endregion
