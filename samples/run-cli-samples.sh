#!/usr/bin/env bash
#
# Runs every CLI sample the docs show (samples/cli/*.sh), so a command on the site that stops working fails
# CI. Each script runs in its own scratch folder against a scratch cache, with `pgembed` on PATH.
#
# Usage:
#   samples/run-cli-samples.sh <pgembed command>        for example: ./publish/pgembed
#   samples/run-cli-samples.sh "dotnet src/pgembed/bin/Release/net10.0/pgembed.dll"
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PGEMBED="${1:?usage: samples/run-cli-samples.sh <pgembed command>}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# a `pgembed` on PATH that runs the build under test
mkdir -p "$WORK/bin"
printf '#!/usr/bin/env bash\nexec %s "$@"\n' "$PGEMBED" > "$WORK/bin/pgembed"
chmod +x "$WORK/bin/pgembed"
export PATH="$WORK/bin:$PATH"

# a scratch cache, unless one is given (CI reuses one so Postgres downloads once)
export PGEMBED_CACHE_DIR="${PGEMBED_CACHE_DIR:-$WORK/cache}"
export NO_COLOR=1

failed=0
for script in "$ROOT"/samples/cli/*.sh; do
  name="$(basename "$script")"
  mkdir -p "$WORK/run/$name"
  echo "==> $name"
  if (cd "$WORK/run/$name" && bash "$script" > "$WORK/$name.log" 2>&1); then
    echo "    ok"
  else
    echo "    FAILED; output:"
    sed 's/^/    | /' "$WORK/$name.log"
    failed=1
  fi
done

exit $failed
