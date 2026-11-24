#!/usr/bin/env python3
"""Sable against .NET, Rust and C, where each has an equal.

    tools/bench/compare.py [--runs N] [--markdown FILE] [--require LANGUAGES] [name ...]

Two sets of benchmarks:

- library: benchmarks/versus-dotnet/*.sb, the standard library's types and
  functions. The .NET ports are benchmarks/dotnet/Program.cs, the Rust ones
  benchmarks/rust/library/*.rs and the C ones benchmarks/c/library/*.c: one
  for each benchmark that language's own standard library can do (C has no
  hash map, Rust no decimal).
- programs: benchmarks/*.sb with a port, the language itself. The .NET ports
  are benchmarks/dotnet/Programs.cs, and the others benchmarks/rust/*.rs and
  benchmarks/c/*.c.

A benchmark without a port in a language is a dash in its column. Ports must
print what the Sable program does; one that prints something else, or does
not build or run, is reported in the tables and fails the script, after the
rest have run. Being slower never does.

Times are processor time, the median of N runs: not the fastest, which with
threads is the run where they happened not to meet. For Sable, Rust and C that
is the whole process; for .NET the benchmark alone, inside a process that has
warmed the JIT (versus_dotnet.py says why). Rust and C trap on integer
overflow and check bounds, as Sable does.

A name is 'map-int', or 'library/sort' where two sets have one. SABLE names
the `sable` to use (else src/Sable.Driver is built); RUSTC and CC the other
compilers. A compiler that is not there leaves its column empty, unless
--require (a comma-separated list: dotnet,rust,c) has it. --markdown appends
the tables to a file, as for $GITHUB_STEP_SUMMARY.
"""

import argparse
import math
import os
import pathlib
import platform
import shutil
import statistics
import subprocess
import sys
import tempfile

from versus_dotnet import DOTNET, ROOT, build, dotnet_times, sable_times

LANGUAGES = {"dotnet": ".NET", "rust": "Rust", "c": "C"}
BENCHMARKS = ROOT / "benchmarks"


class Benchmark:
    """One Sable program and the ports there are of it."""

    def __init__(self, group, source, dotnet):
        self.group, self.name, self.source = group, source.stem, source
        folder = "library" if group == "library" else "."
        key = self.name if group == "library" else f"programs/{self.name}"
        self.ports = {"dotnet": key if key in dotnet else None}
        for language, extension in (("rust", "rs"), ("c", "c")):
            port = BENCHMARKS / language / folder / f"{self.name}.{extension}"
            self.ports[language] = port if port.exists() else None
        self.ms = {}  # language -> milliseconds
        self.errors = {}  # language -> what went wrong


def dotnet_names(executable):
    """The benchmarks the .NET program has, as its usage line lists them."""
    usage = subprocess.run([executable], capture_output=True, text=True).stderr
    return usage[usage.index("<") + 1 : usage.index(">")].split("|")


def version(command, line=0):
    result = subprocess.run(command, capture_output=True, text=True)
    return result.stdout.splitlines()[line].strip() if result.returncode == 0 and result.stdout else "?"


def machine():
    name = platform.processor() or platform.machine()
    try:
        with open("/proc/cpuinfo") as cpus:
            name = next((line.split(":", 1)[1].strip() for line in cpus if line.startswith("model name")), name)
    except OSError:
        if sys.platform == "darwin":
            name = version(["sysctl", "-n", "machdep.cpu.brand_string"])
    return f"{name}, {os.cpu_count()} cores, {platform.system()} {platform.machine()}"


def measure(benchmark, language, tools, scratch, runs):
    """The times of one side of a benchmark, and what it printed."""
    executable = scratch / f"{benchmark.group}-{benchmark.name}-{language}"
    if language == "dotnet":
        return dotnet_times(tools["dotnet"], benchmark.ports["dotnet"], runs)
    if language == "sable":
        command = [tools["sable"], "build", benchmark.source, "-o", executable]
    elif language == "rust":
        command = [tools["rust"], "--edition", "2021", "-C", "opt-level=3", "-C", "overflow-checks=on", benchmark.ports["rust"], "-o", executable]
    else:
        command = [tools["c"], "-O2", "-pthread", benchmark.ports["c"], "-o", executable, "-lm"]
    subprocess.run([str(part) for part in command], check=True, capture_output=True, text=True)
    return sable_times(executable, runs)


def run(benchmark, tools, scratch, runs):
    expected = None
    for language in ["sable", *LANGUAGES]:
        if language not in tools or (language != "sable" and benchmark.ports[language] is None):
            continue
        try:
            times, output = measure(benchmark, language, tools, scratch, runs)
        except subprocess.CalledProcessError as error:
            benchmark.errors[language] = f"failed ({error.returncode})"
            print(f"{benchmark.group}/{benchmark.name}, {language}: {' '.join(map(str, error.cmd))}\n{error.stdout or ''}{error.stderr or ''}", file=sys.stderr)
            if language == "sable":
                return
            continue
        if language == "sable":
            expected = output
        elif output.split() != expected.split():
            benchmark.errors[language] = "output differs"
            print(f"{benchmark.group}/{benchmark.name}, {language}: the outputs differ\n  sable: {expected!r}\n  {language}: {output!r}", file=sys.stderr)
            continue
        benchmark.ms[language] = statistics.median(times)


def ratio(benchmark, language):
    if "sable" in benchmark.ms and language in benchmark.ms and benchmark.ms[language] > 0:
        return benchmark.ms["sable"] / benchmark.ms[language]
    return None


def table(benchmarks, languages):
    """Header, then a row a benchmark, then the geometric mean of each ratio."""
    rows = [["benchmark", "Sable ms", *(f"{LANGUAGES[x]} ms" for x in languages), *(f"vs {LANGUAGES[x]}" for x in languages)]]
    for benchmark in benchmarks:
        row = [benchmark.name]
        for language in ["sable", *languages]:
            if language in benchmark.ms:
                row.append(f"{benchmark.ms[language]:.1f}")
            else:
                row.append(benchmark.errors.get(language, "—"))
        for language in languages:
            value = ratio(benchmark, language)
            row.append("—" if value is None else f"{value:.2f}×")
        rows.append(row)

    means = ["geometric mean", *[""] * (1 + len(languages))]
    for language in languages:
        values = [value for benchmark in benchmarks if (value := ratio(benchmark, language))]
        means.append(f"{math.exp(sum(map(math.log, values)) / len(values)):.2f}× of {len(values)}" if values else "—")
    return [*rows, means]


def text(rows):
    widths = [max(len(row[i]) for row in rows) for i in range(len(rows[0]))]
    return "\n".join("  ".join(c.ljust(w) if i == 0 else c.rjust(w) for i, (c, w) in enumerate(zip(row, widths))) for row in rows)


def markdown(rows):
    header, *body, means = rows
    lines = ["| " + " | ".join(header) + " |", "|:--" + "|--:" * (len(header) - 1) + "|"]
    for row in body:
        cells = list(row)
        # The fastest time of the row in bold.
        times = [(float(c), i) for i, c in enumerate(cells) if 1 <= i <= (len(header) - 2) // 2 + 1 and c.replace(".", "").isdigit()]
        if len(times) > 1:
            fastest = min(times)[0]
            for value, i in times:
                if value == fastest:
                    cells[i] = f"**{cells[i]}**"
        lines.append("| " + " | ".join(cells) + " |")
    lines.append("| " + " | ".join(f"**{c}**" if c else "" for c in means) + " |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--markdown", type=pathlib.Path, metavar="FILE")
    parser.add_argument("--require", default="", metavar="LANGUAGES")
    parser.add_argument("names", nargs="*")
    arguments = parser.parse_args()
    required = [x for x in arguments.require.split(",") if x]
    if unknown := [x for x in required if x not in LANGUAGES]:
        parser.error(f"--require: no language {', '.join(unknown)} (there are {', '.join(LANGUAGES)})")

    found = {
        "dotnet": shutil.which("dotnet"),
        "rust": shutil.which(os.environ.get("RUSTC", "rustc")),
        "c": shutil.which(os.environ.get("CC", "clang")),
    }
    for language, path in found.items():
        if path is None:
            print(f"no compiler for {LANGUAGES[language]}: its column is left out", file=sys.stderr)
            if language in required:
                return 1

    with tempfile.TemporaryDirectory(prefix="sable-compare-") as scratch:
        scratch = pathlib.Path(scratch)
        tools = {language: path for language, path in found.items() if path}
        tools["sable"] = os.environ.get("SABLE") or build(ROOT / "src" / "Sable.Driver", scratch / "compiler", "sable")
        dotnet = []
        if "dotnet" in tools:
            tools["dotnet"] = build(DOTNET, scratch / "dotnet", "VersusDotnet")
            dotnet = dotnet_names(tools["dotnet"])

        sources = [("library", path) for path in sorted((BENCHMARKS / "versus-dotnet").glob("*.sb"))]
        sources += [("programs", path) for path in sorted(BENCHMARKS.glob("*.sb"))]
        benchmarks = [Benchmark(group, path, dotnet) for group, path in sources]
        benchmarks = [b for b in benchmarks if any(b.ports.values())]
        if arguments.names:
            for name in arguments.names:
                if not any(name in (b.name, f"{b.group}/{b.name}") for b in benchmarks):
                    parser.error(f"no benchmark '{name}'")
            benchmarks = [b for b in benchmarks if b.name in arguments.names or f"{b.group}/{b.name}" in arguments.names]

        for benchmark in benchmarks:
            print(f"{benchmark.group}/{benchmark.name}", file=sys.stderr, flush=True)
            run(benchmark, tools, scratch, arguments.runs)

    languages = [language for language in LANGUAGES if language in tools]
    versions = {
        "dotnet": lambda: ".NET SDK " + version([found["dotnet"], "--version"]),
        "rust": lambda: version([tools["rust"], "--version"]),
        "c": lambda: version([tools["c"], "--version"]),
    }
    about = [machine(), *(versions[x]() for x in languages)]
    notes = (
        f"Processor time in milliseconds, the median of {arguments.runs} runs; 'vs' is Sable's time over the other's, so under 1 Sable is faster. "
        "A dash is a benchmark with no port in that language."
    )
    print("; ".join(about) + f"\n{notes}")
    page = ["## Sable against .NET, Rust and C", "", "; ".join(about), "", notes]
    for group, title in (("library", "Standard library"), ("programs", "Programs")):
        if chosen := [b for b in benchmarks if b.group == group]:
            rows = table(chosen, languages)
            print(f"\n{title}\n{text(rows)}")
            page += ["", f"### {title}", "", markdown(rows)]

    failures = [(b, language, what) for b in benchmarks for language, what in b.errors.items()]
    if failures:
        page += ["", "### Failures", ""]
        page += [f"- `{b.group}/{b.name}` ({LANGUAGES.get(language, 'Sable')}): {what}; see the job's log" for b, language, what in failures]
        print(f"\n{len(failures)} failed; see above", file=sys.stderr)
    if arguments.markdown:
        with arguments.markdown.open("a") as out:
            out.write("\n".join(page) + "\n")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
