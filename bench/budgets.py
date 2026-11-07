#!/usr/bin/env python3
"""
End-to-end performance budgets (SPEC.md, Performance): runs pgembed commands as a user would, best of N runs,
against a throwaway instance, and compares them with the budgets below. Exits 1 when a command is over its
budget times --tolerance, or when memory grows with the row count.

    python3 bench/budgets.py <pgembed command> [--tolerance 1.0] [--runs 7]
    python3 bench/budgets.py ./publish/pgembed --tolerance 2.0      # CI: shared runners are slower and noisier

The budgets are for the reference machine (macOS arm64, a ReadyToRun build, warm). A Markdown report goes to
standard output and, on GitHub Actions, to the job summary.
"""
import argparse, json, os, resource, subprocess, sys, time, uuid

# milliseconds on the reference machine
BUDGETS = {
    "--version": 80,
    "connstr <instance> --format uri": 90,
    "status <instance>": 150,
    "sql <instance> \"SELECT 1\"": 150,
    "sql --check \"SELECT 1\"": 150,
    "format \"select 1\"": 150,
    "start <instance> (attach to a running one)": 200,
    "sql: 1M-row table, table view capped at 500": 600,
}
MEMORY_GROWTH_LIMIT = 1.3  # peak memory for 1M rows over 100k rows, streaming as CSV


def run(cmd, **kw):
    return subprocess.run(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=600, **kw)


def best_ms(cmd, runs):
    times = []
    for _ in range(runs):
        start = time.perf_counter()
        result = run(cmd)
        times.append((time.perf_counter() - start) * 1000)
        if result.returncode != 0:
            sys.exit(f"failed: {' '.join(cmd)}\n{result.stderr.decode(errors='replace')}")
    return min(times)


def peak_rss_mb(cmd):
    """Peak resident memory of one run, in MB (a fresh process per measurement, so children do not accumulate)."""
    probe = ("import resource, subprocess, sys; subprocess.run(sys.argv[1:], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL); "
             "r = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss; print(r / (1 << 20) if sys.platform == 'darwin' else r / 1024)")
    return float(subprocess.run([sys.executable, "-c", probe, *cmd], capture_output=True, text=True, check=True).stdout)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pgembed", nargs="+", help="how to run pgembed, for example ./publish/pgembed or dotnet pgembed.dll")
    parser.add_argument("--tolerance", type=float, default=1.0)
    parser.add_argument("--runs", type=int, default=7)
    args = parser.parse_args()
    pg = args.pgembed
    name = f"budget-{uuid.uuid4().hex[:8]}"

    started = subprocess.run(pg + ["start", name, "--version", "17", "--port", "0"], capture_output=True, text=True)
    if started.returncode != 0:
        sys.exit(f"could not start an instance:\n{started.stdout}{started.stderr}")
    try:
        run(pg + ["sql", name, "--yes", "CREATE TABLE big AS SELECT g, md5(g::text) AS h FROM generate_series(1, 1000000) g; "
                                       "CREATE TABLE small AS SELECT * FROM big WHERE g <= 100000"]).check_returncode()

        measured = {
            "--version": best_ms(pg + ["--version"], args.runs),
            "connstr <instance> --format uri": best_ms(pg + ["connstr", name, "--format", "uri"], args.runs),
            "status <instance>": best_ms(pg + ["status", name], args.runs),
            "sql <instance> \"SELECT 1\"": best_ms(pg + ["sql", name, "SELECT 1", "--format", "csv"], args.runs),
            "sql --check \"SELECT 1\"": best_ms(pg + ["sql", "--check", "SELECT 1"], args.runs),
            "format \"select 1\"": best_ms(pg + ["format", "select 1"], args.runs),
            "start <instance> (attach to a running one)": best_ms(pg + ["start", name], args.runs),
            "sql: 1M-row table, table view capped at 500": best_ms(pg + ["sql", name, "SELECT * FROM big", "--format", "table", "--no-pager"], 3),
        }
        small = peak_rss_mb(pg + ["sql", name, "SELECT * FROM small", "--format", "csv"])
        large = peak_rss_mb(pg + ["sql", name, "SELECT * FROM big", "--format", "csv"])
    finally:
        run(pg + ["rm", name, "--force", "--yes"])

    failed = False
    lines = [f"### Performance budgets (tolerance x{args.tolerance:g})", "", "| | Measured | Budget | |", "| --- | ---: | ---: | --- |"]
    for what, ms in measured.items():
        limit = BUDGETS[what] * args.tolerance
        ok = ms <= limit
        failed |= not ok
        lines.append(f"| `{what}` | {ms:.0f} ms | {BUDGETS[what]} ms | {'ok' if ok else 'OVER'} |")
    growth = large / small
    ok = growth <= MEMORY_GROWTH_LIMIT
    failed |= not ok
    lines.append(f"| peak memory, CSV of 100k then 1M rows | {small:.0f} MB, {large:.0f} MB | flat (x{MEMORY_GROWTH_LIMIT:g}) | {'ok' if ok else 'GROWS'} |")

    report = "\n".join(lines)
    print(report)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a") as f:
            f.write(report + "\n")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
