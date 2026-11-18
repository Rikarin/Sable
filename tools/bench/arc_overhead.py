#!/usr/bin/env python3
"""Measures ARC overhead: docs/04 §3.8's "ARC overhead on the suite < 5% vs a
manually-managed equivalent".

    tools/bench/arc_overhead.py [--runs N]

A manually-managed program frees what it allocates, and runs whatever
destroys an object's contents, just as ARC's last release does; what it does
not do is keep counts. So the overhead is the count traffic: every retain,
and every release that does not free. It is measured by doing that traffic
twice — each benchmark is built against the runtime as it is, and against
one where each retain, and each release that leaves an object alive, first
does an equal, out-of-line operation on the object's header — and the
difference is what the traffic costs. Overhead = cost / (time - cost).

The hand-written C ports in benchmarks/c are timed beside it, for the whole
gap to manual C: allocation, layout and code generation included.
"""

import argparse
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
BENCHMARKS = ["binary-trees", "nbody", "shapes", "sort", "words", "orders"]

DOUBLE = """
__attribute__((noinline)) void sable_extra_count(void *pointer) {
    SableObject *object = pointer;
    if (object != NULL && !(object->flags & SABLE_IMMORTAL)) {
        object->strong++;
        __asm__ volatile("" ::: "memory");
        object->strong--;
    }
}
"""


def doubled_runtime(directory):
    """The runtime copied into 'directory', retain and release counting twice; the file to compile."""
    shutil.copytree(ROOT / "runtime", directory)
    memory = directory / "memory.c"
    source = memory.read_text()
    source = source.replace("void sable_retain(void *pointer) {", DOUBLE + "\nvoid sable_retain(void *pointer) {\n    sable_extra_count(pointer);", 1)
    source = source.replace(
        "void sable_release(void *pointer) {",
        "void sable_release(void *pointer) {\n    if (pointer != NULL && ((SableObject *)pointer)->strong > 1) {\n        sable_extra_count(pointer);\n    }",
        1,
    )
    memory.write_text(source)
    return directory / "sable_runtime.c"


def best(executable, runs):
    times = []
    for _ in range(runs):
        start = time.perf_counter()
        subprocess.run([executable], stdout=subprocess.DEVNULL, check=True)
        times.append((time.perf_counter() - start) * 1000)
    return min(times)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", type=int, default=11)
    arguments = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="sable-arc-") as scratch:
        scratch = pathlib.Path(scratch)
        subprocess.run(
            ["dotnet", "build", str(ROOT / "src" / "Sable.Driver"), "-c", "Release", "-o", str(scratch / "compiler"), "-v", "quiet", "-nologo"],
            check=True, stdout=subprocess.DEVNULL,
        )
        doubled = doubled_runtime(scratch / "doubled-runtime")

        total, manual_total = 0.0, 0.0
        print(f"{'benchmark':<13} {'ms':>7} {'counts':>7} {'overhead':>8} {'C ms':>7} {'vs C':>6}")
        for name in BENCHMARKS:
            ir = scratch / f"{name}.ll"
            with ir.open("w") as out:
                subprocess.run([scratch / "compiler" / "sable", "build", ROOT / "benchmarks" / f"{name}.sb", "--emit", "llvm-ir"], check=True, stdout=out)
            plain, twice, c = scratch / name, scratch / f"{name}.twice", scratch / f"{name}.c"
            subprocess.run(["clang", "-O2", "-Wno-override-module", ir, ROOT / "runtime" / "sable_runtime.c", "-o", plain, "-lm"], check=True)
            subprocess.run(["clang", "-O2", "-Wno-override-module", ir, doubled, "-o", twice, "-lm"], check=True)
            subprocess.run(["clang", "-O2", ROOT / "benchmarks" / "c" / f"{name}.c", "-o", c, "-lm"], check=True)

            ms, ms_twice, ms_c = best(plain, arguments.runs), best(twice, arguments.runs), best(c, arguments.runs)
            cost = max(ms_twice - ms, 0.0)
            total += ms
            manual_total += ms - cost
            print(f"{name:<13} {ms:7.1f} {cost:7.1f} {100 * cost / (ms - cost):7.1f}% {ms_c:7.1f} {ms / ms_c:5.2f}x")

        print(f"\nsuite: ARC overhead {100 * (total - manual_total) / manual_total:.1f}%")


if __name__ == "__main__":
    sys.exit(main())
