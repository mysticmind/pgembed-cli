# Versions and Cache

## Versions

snippet: sample_versions

The version list comes from Maven Central and is cached for 24 hours; `--refresh` checks now. Offline, the
cached list is used with a warning. Version specs work everywhere a version is taken: exact (`17.6.0`), major
(`17`, `9.6`), minor (`17.6`) or `latest`.

## Install ahead of time and extensions

snippet: sample_install_ahead

## Cache and diagnostics

snippet: sample_cache

`cache clean` never removes a version a running instance uses. `doctor` checks what Postgres needs: the
platform, not running as root, Maven reachable, the cache folder writable and usable by Postgres, and port
5432 free; it exits 1 when a check fails.

The cache is shared with MysticMind.PostgresEmbed: a version installed by pgembed is not downloaded again by
your tests, and the other way round. Where it lives is in [Configuration](../reference/configuration#cache).
