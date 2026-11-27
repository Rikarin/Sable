#!/usr/bin/env python3
"""Sable against .NET, Rust, C and Go, where each has an equal.

    tools/bench/compare.py [--samples N] [--warm-up-ms N] [--sample-ms N] [--budget-ms N]
                           [--markdown FILE] [--require LANGUAGES] [name ...]

A benchmark is a folder, benchmarks/<set>/<name>, with <name>.sb and the
ports there are of it: <name>.cs, <name>.rs, <name>.c and <name>.go. The sets are
'library', the standard library's types and functions, each ported where a
language's own standard library has an equal, and 'programs', the language.

Sable's numbers are 'sable bench's: each folder's @bench functions, built as
a harness, whose JSON is read; a @bench(arguments:) function is a benchmark
for each of its cases, and a port is asked for each by the same
name. The ports measure themselves the same way (benchmarks/README.md): in a
process of its own, warmed up, as many iterations to a sample as it takes to
last --sample-ms, and --samples of them, or as many as --budget-ms allows
once there are five. What is compared is an operation, the work without the
making of its input, in processor time, the median of the samples: that of
every thread and of the processes waited for, as it is less at the mercy of
what else the machine does, and what a program costs. (.NET's has its
collector's and its JIT's threads' in it, as they are its cost.)

What an operation allocates is a table of its own: of every thread, the bytes
and how many allocations, of which .NET says the bytes alone. Rust's and C's are
counted by a build of the port apart, so that no time has the counting in it;
Go's by its runtime (runtime.ReadMemStats), in a run of its own.

A program's whole process is a table of its own: its start-up (programs/startup,
which does nothing), and each of 'programs' run once, start to end, cold.

A folder that does not build, or is in the old format, is left out and
reported as a failure, and the rest go on.

A benchmark without a port in a language is a dash in its column: one of
'library' may have none, its time there to be seen. Ports must compute what
the Sable program does; one that computes something else, or does not build
or run, is reported in the tables and fails the script, after the rest have
run. Being slower never does. Rust and C trap on integer overflow and check
bounds, as Sable does; Go checks bounds but wraps on overflow.

A name is 'map-int', or 'library/sort' where two sets have one. SABLE names
the `sable` to use (else src/Sable.Driver is built); RUSTC, CC and GO the other
compilers. A compiler that is not there leaves its column empty, unless
--require (a comma-separated list: dotnet,rust,c,go) has it. --markdown appends
the tables to a file, as for $GITHUB_STEP_SUMMARY.
"""

import argparse
import concurrent.futures
import json
import math
import os
import pathlib
import platform
import resource
import shutil
import statistics
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
BENCHMARKS = ROOT / "benchmarks"
DOTNET = BENCHMARKS / "support" / "dotnet"
GO_SUPPORT = BENCHMARKS / "support" / "go" / "bench.go"
LANGUAGES = {"dotnet": ".NET", "rust": "Rust", "c": "C", "go": "Go"}
SETS = {"library": "Standard library", "programs": "Programs"}
WHOLE_RUNS = 7
# Under this, an allocated size is mostly the line a benchmark returns.
LEAST_BYTES = 1024


def build_dotnet(project, directory, name):
    subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", "-o", str(directory), "-v", "quiet", "-nologo"],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    return directory / name


def run_command(command, **options):
    return subprocess.run([str(part) for part in command], check=True, capture_output=True, text=True, **options)


class Failure(Exception):
    """What went wrong with one side of a benchmark, as the table has it."""


class Benchmark:
    """One Sable benchmark, or a case of one, and the ports there are of it."""

    def __init__(self, folder, case="", harness_name="", harness=None, has_wall=False):
        self.group, self.folder, self.case = folder.parent.name, folder, case
        self.harness_name, self.harness = harness_name, harness
        # 'aes-gcm(64, 100000)', as the harness names a case.
        self.name = folder.name + case
        self.ports = {}
        for language, extension in (("dotnet", "cs"), ("rust", "rs"), ("c", "c"), ("go", "go")):
            port = folder / f"{folder.name}.{extension}"
            self.ports[language] = port if port.exists() else None
        self.ns = {}  # language -> processor nanoseconds an operation
        self.has_wall = has_wall  # tagged "wall": the time it takes is of interest too
        self.wall = {}  # language -> nanoseconds an operation by the clock
        self.bytes = {}  # language -> bytes an operation allocates
        self.allocations = {}  # language -> how many allocations those are
        self.whole = {}  # language -> processor milliseconds of the whole process
        self.errors = {}  # language -> what went wrong

    @property
    def key(self):
        return f"{self.group}/{self.name}"

    @property
    def title(self):
        return self.name.replace('"', "")


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


def build_harness(sable, scratch, folder):
    """The folder's harness, and the benchmarks it describes: a program of its own, as what the optimiser makes of one depends on what else it has in it."""
    harness = scratch / f"{folder.parent.name}-{folder.name}-harness"
    run_command([sable, "bench", folder, "--harness-out", harness], cwd=ROOT)
    benchmarks = []
    for line in run_command([harness, "--describe"]).stdout.splitlines():
        name, *_, tags = line.split("\t")
        if name != "filtered out":
            paren = name.find("(")
            benchmarks.append(Benchmark(folder, name[paren:] if paren >= 0 else "", name, harness, "wall" in tags.split(",")))
    return benchmarks


class Runner:
    def __init__(self, tools, scratch, arguments):
        self.tools, self.scratch = tools, scratch
        self.options = [
            "--samples", arguments.samples, "--warm-up-ms", arguments.warm_up_ms, "--sample-ms", arguments.sample_ms, "--budget-ms", arguments.budget_ms,
        ]
        self.built = {}

    def sable(self, benchmark):
        out = self.scratch / "sable.json"
        run_command([benchmark.harness, "--exact", benchmark.harness_name, "--json", out, "--metric", "processor", *self.options], cwd=self.scratch)
        (result,) = json.loads(out.read_text())["benchmarks"]
        return result["processor"], result["wall"], (result["allocatedBytes"], result["allocations"]), result["check"]

    def executable(self, benchmark, language, counting=False):
        """The port's command, or that of it which counts: built the first time, as every case of it is one executable."""
        if language == "dotnet":
            return [self.tools["dotnet"], benchmark.key]
        # Go's runtime counts what it allocates: the one build does both.
        counting = counting and language != "go"
        path = self.scratch / f"{benchmark.group}-{benchmark.folder.name}-{language}{'-counting' if counting else ''}"
        if language == "sable":
            command = [self.tools["sable"], "build", benchmark.folder / f"{benchmark.folder.name}.sb", "-o", path]
        elif language == "rust":
            command = [self.tools["rust"], "--edition", "2021", "-C", "opt-level=3", "-C", "overflow-checks=on", "-A", "warnings", *(["--cfg", "bench_count"] if counting else []), benchmark.ports["rust"], "-o", path]
        elif language == "go":
            # The port and its harness, as files: no module is needed.
            command = [self.tools["go"], "build", "-o", path, benchmark.ports["go"], GO_SUPPORT]
        else:
            command = [self.tools["c"], "-O2", "-pthread", *(["-DBENCH_COUNT"] if counting else []), benchmark.ports["c"], "-o", path, "-lm"]
        if path not in self.built:
            run_command(command)
            self.built[path] = [path]
        return self.built[path]

    def port(self, benchmark, language):
        output = run_command([*self.executable(benchmark, language), "--measure", *self.options], cwd=self.scratch).stdout
        samples, walls, allocated, check = [], [], None, None
        for line in output.splitlines():
            kind, *fields = line.split("\t")
            if kind == "sample":
                iterations, wall, processor = map(float, fields)
                walls.append(wall / iterations)
                samples.append(processor / iterations)
            elif kind == "allocated":
                allocated = float(fields[0]), None
            elif kind == "check":
                check = fields[0].replace("\\n", "\n").replace("\\t", "\t").replace("\\\\", "\\")
        if not samples or check is None:
            raise Failure("measured nothing")
        if language in ("rust", "c", "go"):
            output = run_command([*self.executable(benchmark, language, counting=True), "--allocations"], cwd=self.scratch).stdout
            allocated = next((tuple(map(float, line.split("\t")[1:])) for line in output.splitlines() if line.startswith("allocated\t")), None)
            if allocated is None:
                raise Failure("counted no allocations")
        return samples, walls, allocated, check

    def measure(self, benchmark):
        expected = None
        for language in ["sable", *LANGUAGES]:
            if language not in self.tools or (language != "sable" and benchmark.ports[language] is None):
                continue
            try:
                samples, walls, allocated, check = self.sable(benchmark) if language == "sable" else self.port(benchmark, language)
                if language == "sable":
                    expected = check
                elif check.split() != expected.split():
                    print(f"{benchmark.key}, {language}: what they compute differs\n  sable: {expected!r}\n  {language}: {check!r}", file=sys.stderr)
                    raise Failure("output differs")
            except subprocess.CalledProcessError as error:
                benchmark.errors[language] = f"failed ({error.returncode})"
                print(f"{benchmark.key}, {language}: {' '.join(map(str, error.cmd))}\n{error.stdout or ''}{error.stderr or ''}", file=sys.stderr)
                if language == "sable":
                    return
                continue
            except Failure as failure:
                benchmark.errors[language] = str(failure)
                continue
            benchmark.ns[language] = statistics.median(samples)
            if benchmark.has_wall:
                benchmark.wall[language] = statistics.median(walls)
            if allocated is not None:
                benchmark.bytes[language], count = allocated
                if count is not None:
                    benchmark.allocations[language] = count

    def whole(self, benchmark):
        """The programs' whole processes, each language's in turn."""
        times = {}
        commands = {}
        for language in ["sable", *LANGUAGES]:
            if language not in self.tools or (language != "sable" and benchmark.ports[language] is None):
                continue
            try:
                commands[language] = self.executable(benchmark, language)
            except subprocess.CalledProcessError as error:
                benchmark.errors.setdefault(language, f"failed ({error.returncode})")
                print(f"{benchmark.key}, {language}: {' '.join(map(str, error.cmd))}\n{error.stdout or ''}{error.stderr or ''}", file=sys.stderr)
        for _ in range(WHOLE_RUNS):
            for language, command in commands.items():
                before = resource.getrusage(resource.RUSAGE_CHILDREN)
                done = subprocess.run([str(part) for part in command], cwd=self.scratch, capture_output=True)
                after = resource.getrusage(resource.RUSAGE_CHILDREN)
                if done.returncode != 0:
                    benchmark.errors.setdefault(language, f"failed ({done.returncode})")
                    continue
                times.setdefault(language, []).append((after.ru_utime + after.ru_stime - before.ru_utime - before.ru_stime) * 1000)
        benchmark.whole = {language: statistics.median(each) for language, each in times.items()}


def duration(ns):
    for scale, unit in ((1e9, "s"), (1e6, "ms"), (1e3, "µs")):
        if ns >= scale:
            return f"{ns / scale:.3g} {unit}" if ns / scale < 100 else f"{ns / scale:.0f} {unit}"
    return f"{ns:.3g} ns"


def size(count):
    for scale, unit in ((1 << 30, "GB"), (1 << 20, "MB"), (1 << 10, "KB")):
        if count >= scale:
            return f"{count / scale:.3g} {unit}" if count / scale < 100 else f"{count / scale:.0f} {unit}"
    return f"{count:.0f} B"


def count(number):
    for scale, unit in ((1e9, "G"), (1e6, "M"), (1e3, "k")):
        if number >= scale:
            return f"{number / scale:.3g}{unit}" if number / scale < 100 else f"{number / scale:.0f}{unit}"
    return f"{number:.3g}" if number < 100 else f"{number:.0f}"


def ratio(values, language):
    if "sable" in values and values.get(language, 0) > 0:
        return values["sable"] / values[language]
    return None


def table(benchmarks, languages, values, show, columns, extra=(), least=0):
    """Header, then a row a benchmark, then the geometric mean of each ratio.

    'values' gives a benchmark's of each language, and 'show' writes one;
    'extra' is columns more at the end, each a title and what gives its cell.
    The means are of the ratios of values over 'least'.
    """
    rows = [["benchmark", f"Sable{columns}", *(f"{LANGUAGES[x]}{columns}" for x in languages), *(f"vs {LANGUAGES[x]}" for x in languages), *(title for title, _ in extra)]]
    best = []
    for benchmark in benchmarks:
        each = values(benchmark)
        row = [benchmark.title]
        for language in ["sable", *languages]:
            row.append(show(each[language]) if language in each else benchmark.errors.get(language, "—"))
        for language in languages:
            value = ratio(each, language)
            row.append("—" if value is None else f"{value:.2f}×")
        row += [cell(benchmark) for _, cell in extra]
        rows.append(row)
        ordered = ["sable", *languages]
        fastest = min(each.values()) if len(each) > 1 else None
        best.append([i + 1 for i, language in enumerate(ordered) if fastest is not None and each.get(language) == fastest])
    means = ["geometric mean", *[""] * (1 + len(languages))]
    for language in languages:
        each = [values(benchmark) for benchmark in benchmarks]
        ratios = [value for v in each if (value := ratio(v, language)) and min(v["sable"], v[language]) > least]
        means.append(f"{math.exp(sum(map(math.log, ratios)) / len(ratios)):.2f}× of {len(ratios)}" if ratios else "—")
    rows.append(means + [""] * len(extra))
    return rows, best


def text(rows):
    widths = [max(len(row[i]) for row in rows) for i in range(len(rows[0]))]
    return "\n".join("  ".join(c.ljust(w) if i == 0 else c.rjust(w) for i, (c, w) in enumerate(zip(row, widths))) for row in rows)


def markdown(rows, best):
    header, *body, means = rows
    lines = ["| " + " | ".join(header) + " |", "|:--" + "|--:" * (len(header) - 1) + "|"]
    for row, fastest in zip(body, best):
        cells = [f"**{c}**" if i in fastest else c for i, c in enumerate(row)]
        lines.append("| " + " | ".join(cells) + " |")
    lines.append("| " + " | ".join(f"**{c}**" if c else "" for c in means) + " |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--samples", type=int, default=15)
    parser.add_argument("--warm-up-ms", type=int, default=1000, help="of each benchmark: .NET's JIT needs most of it")
    parser.add_argument("--sample-ms", type=int, default=20)
    parser.add_argument("--budget-ms", type=int, default=1000)
    parser.add_argument("--jobs", type=int, default=min(4, os.cpu_count() or 1), help="how many harnesses to build at once")
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
        "go": shutil.which(os.environ.get("GO", "go")),
    }
    for language, path in found.items():
        if path is None:
            print(f"no compiler for {LANGUAGES[language]}: its column is left out", file=sys.stderr)
            if language in required:
                return 1

    folders = sorted({path.parent for group in SETS for path in (BENCHMARKS / group).glob("*/*.sb")})
    if arguments.names:
        chosen = [f for f in folders if f.name in arguments.names or f"{f.parent.name}/{f.name}" in arguments.names]
        for name in arguments.names:
            if not any(name in (f.name, f"{f.parent.name}/{f.name}") for f in folders):
                parser.error(f"no benchmark '{name}'")
        folders = chosen

    failures = []
    with tempfile.TemporaryDirectory(prefix="sable-compare-") as scratch:
        scratch = pathlib.Path(scratch)
        tools = {language: path for language, path in found.items() if path}
        sable = os.environ.get("SABLE")
        # As a path from here: the benchmarks are run from elsewhere.
        tools["sable"] = pathlib.Path(sable).resolve() if sable else build_dotnet(ROOT / "src" / "Sable.Driver", scratch / "compiler", "sable")
        if "dotnet" in tools:
            tools["dotnet"] = build_dotnet(DOTNET, scratch / "dotnet", "Benchmarks")

        runner = Runner(tools, scratch, arguments)
        measured = [f for f in folders if "@bench" in (f / f"{f.name}.sb").read_text()]
        # What has no @bench is a program measured whole; in the library, one
        # from before there was a harness, whose main prints what it computed.
        unmeasured = [f for f in folders if f not in measured]
        old = [f for f in unmeasured if f.parent.name == "library"]
        broken = [(f, "old format: 'main' in place of '@bench' (benchmarks/README.md, \"From the old format\")") for f in old]
        for folder, why in broken:
            print(f"library/{folder.name}: {why}; left out", file=sys.stderr)
        benchmarks = []
        # The harnesses first, several at once; then nothing is built while anything is measured.
        print(f"building {len(measured)} harnesses", file=sys.stderr, flush=True)
        with concurrent.futures.ThreadPoolExecutor(arguments.jobs) as pool:
            for folder, building in [(f, pool.submit(build_harness, tools["sable"], scratch, f)) for f in measured]:
                try:
                    benchmarks += building.result()
                except subprocess.CalledProcessError as error:
                    print(f"{folder.parent.name}/{folder.name}, sable: does not build; left out\n{error.stdout or ''}{error.stderr or ''}", file=sys.stderr)
                    broken.append((folder, "does not build"))
        benchmarks.sort(key=lambda b: b.group)

        for benchmark in benchmarks:
            print(benchmark.key, file=sys.stderr, flush=True)
            runner.measure(benchmark)
        programs = [b for b in benchmarks if b.group == "programs" and any(b.ports.values())]
        startup = [Benchmark(f) for f in unmeasured if f not in old]
        for benchmark in [*startup, *programs]:
            print(f"{benchmark.key}, whole", file=sys.stderr, flush=True)
            runner.whole(benchmark)

    languages = [language for language in LANGUAGES if language in tools]
    versions = {
        "dotnet": lambda: ".NET SDK " + version([found["dotnet"], "--version"]),
        "rust": lambda: version([found["rust"], "--version"]),
        "c": lambda: version([found["c"], "--version"]),
        "go": lambda: version([found["go"], "version"]),
    }
    about = [machine(), *(versions[x]() for x in languages)]
    notes = (
        f"Processor time an operation, warm: the median of up to {arguments.samples} samples of {arguments.sample_ms} ms or an iteration, "
        f"after {arguments.warm_up_ms} ms of warm-up, measured the same way in each language. 'vs' is Sable's time over the other's, "
        "so under 1 Sable is faster. "
        "A dash is a benchmark with no port in that language."
    )
    print("; ".join(about) + f"\n{notes}")
    page = ["## Sable against .NET, Rust, C and Go", "", "; ".join(about), "", notes]

    for group, title in SETS.items():
        if chosen := [b for b in benchmarks if b.group == group]:
            rows, best = table(chosen, languages, lambda b: b.ns, duration, "")
            print(f"\n{title}\n{text(rows)}")
            page += ["", f"### {title}", "", markdown(rows, best)]
    if walled := [b for b in benchmarks if b.wall]:
        rows, best = table(walled, languages, lambda b: b.wall, duration, "")
        note = f"The time an operation takes by the clock, of the benchmarks tagged 'wall', whose point is to take less of it by {os.cpu_count()} processors."
        print(f"\nElapsed\n{note}\n{text(rows)}")
        page += ["", "### Elapsed", "", note, "", markdown(rows, best)]
    def allocations(language):
        return lambda b: count(b.allocations[language]) if language in b.allocations else "—"

    counted = [(title, allocations(language)) for language, title in (("sable", "Sable objects"), ("rust", "Rust allocations"), ("c", "C allocations"), ("go", "Go allocations")) if language in ["sable", *languages]]
    note = (
        "What an operation allocates, of every thread: bytes, and how many objects or allocations, which .NET does not say. 'vs' is Sable's bytes over the other's, "
        f"so under 1 Sable allocates less; a dash where the other allocates nothing. The means are of those where both allocate more than {size(LEAST_BYTES)}. "
        "Sable's and .NET's bytes have an object's header in them; Rust's and C's are those asked for, and C's without what its library allocates for itself; Go's are its runtime's count, goroutines' stacks not among them. Sable's objects are without their buffers, which are in the bytes alone."
    )
    for group, title in SETS.items():
        if chosen := [b for b in benchmarks if b.group == group and b.bytes]:
            rows, best = table(chosen, languages, lambda b: b.bytes, size, "", counted, LEAST_BYTES)
            print(f"\nMemory: {title}\n{note}\n{text(rows)}")
            page += ["", f"### Memory: {title}", "", note, "", markdown(rows, best)]
    if whole := [*startup, *programs]:
        rows, best = table(whole, languages, lambda b: b.whole, lambda ms: f"{ms:.1f}", " ms")
        note = f"Processor time of the process from its start to its end, cold, the median of {WHOLE_RUNS} runs; 'startup' prints a line."
        print(f"\nWhole processes\n{note}\n{text(rows)}")
        page += ["", "### Whole processes", "", note, "", markdown(rows, best)]

    for folder, why in broken:
        left_out = Benchmark(folder)
        left_out.errors["sable"] = why
        benchmarks.append(left_out)
    failures = [(b, language, what) for b in [*benchmarks, *startup] for language, what in b.errors.items()]
    if failures:
        page += ["", "### Failures", ""]
        page += [f"- `{b.key}` ({LANGUAGES.get(language, 'Sable')}): {what}; see the job's log" for b, language, what in failures]
        print(f"\n{len(failures)} failed; see above", file=sys.stderr)
    if arguments.markdown:
        with arguments.markdown.open("a") as out:
            out.write("\n".join(page) + "\n")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
