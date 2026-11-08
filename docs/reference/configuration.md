# Configuration

## Environment variables

| Variable | |
| --- | --- |
| `PGEMBED_CACHE_DIR` | Cache folder for downloads and instances (also `--cache-dir`); shared with MysticMind.PostgresEmbed |
| `PGEMBED_MAVEN_REPO` | A Maven mirror with the Postgres binaries (also `--maven-repo`); default Maven Central |
| `PGEMBED_PAGER`, `PAGER` | The pager for long results; default `less -SFX`; an empty value turns it off |
| `PGEMBED_REQUIRE_PASSWORD` | `1` gives every instance started a password for TCP connections (as `start --password`), for shared machines |
| `PGEMBED_ALLOW_HTTP_REPO` | `1` allows an http Maven mirror on a network you trust |
| `PGEMBED_TRACE` | `1` prints where a command's time goes, on standard error |
| `PGPASSWORD`, `~/.pgpass` | Passwords for connection strings that have none |
| `NO_COLOR` | Turns colors off |

## Options for every command

| Option | |
| --- | --- |
| `--json` | Machine-readable output on standard output |
| `--cache-dir` | Cache folder |
| `--maven-repo` | A Maven mirror |
| `--verbose` | Log details to standard error |

## Cache

| OS | Default location |
| --- | --- |
| Linux | `$XDG_CACHE_HOME/pgembed`, else `~/.cache/pgembed` |
| macOS | `~/Library/Caches/pgembed` |
| Windows | `%LOCALAPPDATA%\pgembed` |

```txt
<cache>/
  binaries/          downloads and extracted installs, one per version and platform
  instances/<name>/  an instance: instance.json (version, port, settings), data/, postgres.log
  metadata/          cached version lists, refreshed every 24 hours
  history/           the interactive prompt's history
```

## Exit codes

| Code | |
| --- | --- |
| `0` | Success |
| `1` | A failure: a SQL error, a refused change, a failed check |
| `2` | A usage error |
| `130` | Cancelled (Ctrl+C) |
