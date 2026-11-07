# In CI

pgembed needs no Docker, so it runs on any CI runner with .NET: Linux, macOS and Windows, including runners
that cannot run containers. These steps are from a GitHub Actions workflow.

Cache the Postgres downloads:

snippet: sample_ci_cache

Start an instance and pass its connection string to the tests:

snippet: sample_ci_start

snippet: sample_ci_test

Test against several versions at once:

snippet: sample_ci_versions

On Linux, Postgres refuses to run as root. GitHub's hosted runners are not root; in a container job, run the
tests as a normal user.
