#!/usr/bin/env python3
"""Sable against .NET: the Phase 4 exit criterion "Dictionary/List/String
within 15% of their .NET equivalents on a standard benchmark set" (docs/04
§4.4).

    tools/bench/versus_dotnet.py [--runs N] [name ...]

Builds the compiler and benchmarks/dotnet (Release), then for each
benchmarks/versus-dotnet/<name>.sb builds it optimised and runs both sides
N times, keeping the fastest of each. The two must print the same line, or
the comparison means nothing and the script fails.

Times are processor time, user and system, not wall time: the machine this
runs on is often busy, and processor time is skewed less by that. The Sable
time is the whole process, start-up and exit included; the .NET time is
measured inside the process around the benchmark alone, after the first run
has warmed the JIT. So the comparison leans towards .NET — on purpose: it is
the time a Sable program has to beat.
"""

import argparse
import pathlib
import resource
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCES = ROOT / "benchmarks" / "versus-dotnet"
DOTNET = ROOT / "benchmarks" / "dotnet"


def build(project, directory, name):
    subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", "-o", str(directory), "-v", "quiet", "-nologo"],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    return directory / name


def sable_times(executable, runs):
    times, output = [], None
    for _ in range(runs):
        before = resource.getrusage(resource.RUSAGE_CHILDREN)
        result = subprocess.run([executable], capture_output=True, text=True, check=True)
        after = resource.getrusage(resource.RUSAGE_CHILDREN)
        times.append((after.ru_utime + after.ru_stime - before.ru_utime - before.ru_stime) * 1000)
        output = result.stdout
    return times, output


def dotnet_times(executable, name, runs):
    # The program warms the JIT up itself, before the runs it times.
    result = subprocess.run([executable, name, str(runs)], capture_output=True, text=True, check=True)
    times = next(line.split()[1:] for line in result.stderr.splitlines() if line.startswith("times "))
    return [float(ms) for ms in times], result.stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("names", nargs="*")
    arguments = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="sable-versus-") as scratch:
        scratch = pathlib.Path(scratch)
        sable = build(ROOT / "src" / "Sable.Driver", scratch / "compiler", "sable")
        reference = build(DOTNET, scratch / "dotnet", "VersusDotnet")

        rows = []
        for source in sorted(SOURCES.glob("*.sb")):
            name = source.stem
            if arguments.names and name not in arguments.names:
                continue

            executable = scratch / name
            subprocess.run([sable, "build", str(source), "-o", str(executable)], check=True)
            sable_all, sable_output = sable_times(executable, arguments.runs)
            dotnet_all, dotnet_output = dotnet_times(reference, name, arguments.runs)
            if sable_output != dotnet_output:
                print(f"{name}: the outputs differ\n  sable:  {sable_output!r}\n  dotnet: {dotnet_output!r}", file=sys.stderr)
                return 1
            rows.append((name, min(dotnet_all), min(sable_all)))

    width = max([len("benchmark")] + [len(row[0]) for row in rows])
    print(f"{'benchmark':<{width}}  {'.NET ms':>9}  {'Sable ms':>9}  {'ratio':>6}")
    for name, dotnet_ms, sable_ms in rows:
        print(f"{name:<{width}}  {dotnet_ms:9.1f}  {sable_ms:9.1f}  {sable_ms / dotnet_ms:6.2f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
