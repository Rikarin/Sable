#!/usr/bin/env python3
"""Runs the benchmark suite: docs/04 §3.8's "a benchmark suite exists and is
tracked per-commit".

    tools/bench/run.py [--record] [--runs N] [name ...]

Builds the compiler, then each benchmark (benchmarks/*.sb, and the JSON exit
program), optimised; runs each N times and keeps the fastest; and runs it once
more with SABLE_STATS for what ARC did. Prints a table. With --record, appends
a row per benchmark to benchmarks/history.tsv, keyed by commit, so that a
change's cost or saving is on the record beside it.
"""

import argparse
import datetime
import os
import pathlib
import subprocess
import sys
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
HISTORY = ROOT / "benchmarks" / "history.tsv"
HEADER = "date\tcommit\tbenchmark\tms\tallocations\tretains\treleases\n"


def benchmarks():
    found = sorted((ROOT / "benchmarks").glob("*.sb"))
    found.append(ROOT / "tests" / "programs" / "json.sb")
    return found


def commit():
    head = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    dirty = subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    return head + ("+" if dirty else "")


def build_compiler(directory):
    subprocess.run(
        ["dotnet", "build", str(ROOT / "src" / "Sable.Driver"), "-c", "Release", "-o", str(directory), "-v", "quiet", "-nologo"],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    return directory / "sable"


def stats(executable):
    result = subprocess.run([executable], env={**os.environ, "SABLE_STATS": "1"}, capture_output=True, text=True, check=True)
    for line in result.stderr.splitlines():
        if line.startswith("sable: allocations"):
            parts = line.replace(",", "").split()
            return int(parts[2]), int(parts[4]), int(parts[6])
    return 0, 0, 0


def fastest(executable, runs):
    best = None
    for _ in range(runs):
        start = time.perf_counter()
        subprocess.run([executable], stdout=subprocess.DEVNULL, check=True)
        elapsed = (time.perf_counter() - start) * 1000
        best = elapsed if best is None else min(best, elapsed)
    return best


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--record", action="store_true", help="append the results to benchmarks/history.tsv")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("names", nargs="*")
    arguments = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="sable-bench-") as scratch:
        scratch = pathlib.Path(scratch)
        sable = build_compiler(scratch / "compiler")

        rows = []
        for source in benchmarks():
            name = source.stem
            if arguments.names and name not in arguments.names:
                continue

            executable = scratch / name
            subprocess.run([sable, "build", str(source), "-o", str(executable)], check=True)
            ms = fastest(executable, arguments.runs)
            allocations, retains, releases = stats(executable)
            rows.append((name, ms, allocations, retains, releases))

    width = max(len(row[0]) for row in rows)
    print(f"{'benchmark':<{width}}  {'ms':>9}  {'allocations':>12}  {'retains':>12}  {'releases':>12}")
    for name, ms, allocations, retains, releases in rows:
        print(f"{name:<{width}}  {ms:9.1f}  {allocations:12,}  {retains:12,}  {releases:12,}")

    if arguments.record:
        new = not HISTORY.exists()
        with HISTORY.open("a") as history:
            if new:
                history.write(HEADER)
            today = datetime.date.today().isoformat()
            label = commit()
            for name, ms, allocations, retains, releases in rows:
                history.write(f"{today}\t{label}\t{name}\t{ms:.1f}\t{allocations}\t{retains}\t{releases}\n")
        print(f"\nrecorded in {HISTORY.relative_to(ROOT)}")


if __name__ == "__main__":
    sys.exit(main())
