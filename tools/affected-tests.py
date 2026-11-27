#!/usr/bin/env python3
"""Says which tests a change can affect, and runs them.

    tools/affected-tests.py                  the plan for this branch's change
    tools/affected-tests.py --run            ...and runs it
    tools/affected-tests.py --base REF       changed since REF (default: the
                                             merge base with origin/master)
    tools/affected-tests.py --files A B ...  for these files instead
    tools/affected-tests.py --native         CodeGen tests built natively too
                                             (SABLE_TEST_NATIVE=all)
    tools/affected-tests.py --modes default  library modes to run (default:
                                             default,unspecialised,shared)

A change is what differs from the base: commits, staged and unstaged edits,
and new files. Each changed file is mapped to what tests it:

- a test class's file (tests/Sable.<P>.Tests/<Class>.cs) to that class; a
  helper file there (one declaring no *Tests class) to the whole project;
- a compiler file (src/Sable.<P>/...) to the classes named for a type it
  declares (<Type>Tests), the classes whose name shares a word of its file's
  name, and the classes whose source names a type it declares; and, since a
  compiler change reaches every program, to a fixed set of CodeGen classes
  that build the library and the examples;
- a runtime file to the CodeGen classes that build native code, those its
  name matches, and the library modules it serves;
- a library module's file (lib/Sable.<M>/...) to that module's tests in each
  mode, the tests of the modules that import it (default mode), its doc
  examples, its coverage and the
  CodeGen classes that hold the library's interface; Sable.Core is imported
  by every module, so a change there runs Sable.Core alone;
- docs, the spec, examples, benchmarks and CI scripts to the classes that
  check them.

The plan is a floor, not a ceiling: AGENTS.md still asks for every test whose
expected text a change alters (grep tests/ for the old text), and CI runs the
rest. A file nothing maps is listed, so it is seen rather than skipped.
Runs under `nice`, one after another; exits 1 if anything fails.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECTS = ["Syntax", "Symbols", "SIL", "CodeGen", "LanguageServer"]
# The test projects that can see a compiler project's types: its own and those
# built on it.
DOWNSTREAM = {
    "Syntax": PROJECTS,
    "Symbols": ["Symbols", "SIL", "CodeGen", "LanguageServer"],
    "SIL": ["SIL", "CodeGen"],
    "CodeGen": ["CodeGen"],
    "LanguageServer": ["LanguageServer"],
    "Driver": ["CodeGen"],
}
SABLE = ROOT / "src/Sable.Driver/bin/Debug/net11.0/sable"
EXCLUDE = "Category!=Fuzz&Category!=Timing"
MODES = {
    "default": {},
    "unspecialised": {"SABLE_GENERICS": "unspecialised"},
    "shared": {"SABLE_CHECK_SHARED": "1"},
}

# What every compiler change can break: the library built and checked, and
# whole programs (AGENTS.md: LibrarySymbolsTests, LibrarySourceTests and
# BuildCacheTests hold the library's interface).
COMPILER_SMOKE = ["LibraryTests", "LibrarySymbolsTests", "LibrarySourceTests",
                  "BuildCacheTests", "ExampleProgramTests"]
LIBRARY_INTERFACE = ["LibrarySymbolsTests", "LibrarySourceTests", "BuildCacheTests"]
# Classes that build and run native code, for any runtime change.
RUNTIME_SMOKE = ["NativeTests", "PanicTests", "RuntimeProfileTests", "LibraryTests"]

# The library modules a runtime file serves (by its name, after 'platform_'
# and the OS are taken off). Files not here serve Sable.Core.
RUNTIME_MODULES = {
    "net": ["Sable.Net"],
    "ipc": ["Sable.IO.Ipc", "Sable.IO.Ports"],
    "fs": ["Sable.IO"], "files": ["Sable.IO"], "watch": ["Sable.IO"],
    "io": ["Sable.IO", "Sable.Core"],
    "terminal": ["Sable.IO"],
    "process": ["Sable.Process"],
    "tasks": ["Sable.Concurrency"], "frames": ["Sable.Concurrency"],
    "threads": ["Sable.Concurrency"], "locks": ["Sable.Concurrency"],
    "parallel": ["Sable.Concurrency"], "watchdog": ["Sable.Concurrency"],
    "foreign": ["Sable.Concurrency", "Sable.Interop"],
    "topology": ["Sable.Concurrency"],
    "crypto": ["Sable.Crypto"], "ciphers": ["Sable.Crypto"], "cipher": ["Sable.Crypto"],
    "trust": ["Sable.Crypto.X509"],
    "checksums": ["Sable.IO.Hashing"],
    "codecs": ["Sable.IO.Compression"],
    "vectors": ["Sable.Math.Tensors", "Sable.Core"], "dlpack": ["Sable.Math.Tensors"],
    "validation": ["Sable.Text", "Sable.Core"],
}

# Other paths, by prefix, to CodeGen (or other project) classes.
PATH_CLASSES = [
    ("docs/diagnostics/", [("CodeGen", "ExplainTests")]),
    ("spec/SYNTAX.md", [("Syntax", "SyntaxSpecTests")]),
    ("src/Sable.Syntax/Syntax.xml", [("Syntax", "GeneratedCodeTests"), ("Syntax", "SyntaxSpecTests")]),
    ("examples/ssg", [("CodeGen", "StaticSiteTests")]),
    ("examples/", [("CodeGen", "ExampleProgramTests")]),
    ("benchmarks/", [("CodeGen", "BenchRunnerTests")]),
    ("tools/ci/codegen-shard.sh", [("CodeGen", "ShardTests")]),
    ("tools/lldb/", [("CodeGen", "DebuggerFormatterTests"), ("CodeGen", "DebuggerTests")]),
    ("tests/programs/", [("CodeGen", "LibraryTests")]),
    ("tests/negative/", [("Symbols", "NegativeCorpusTests")]),
    ("tests/corpus/", [("Syntax", "CorpusTests"), ("Syntax", "RoundTripCorpusTests"),
                       ("Symbols", "CorpusSemanticTests"), ("SIL", "CorpusLoweringTests")]),
    ("tests/sil/", [("SIL", "SilProgramTests")]),
]

# Words too common in class and file names to say two things are related.
COMMON_WORDS = {"tests", "test", "lowering", "library", "more", "sable", "builder",
                "binder", "model", "kind", "check", "checker", "pass", "passes",
                "info", "data", "node", "nodes", "type", "types", "value", "values",
                "helpers", "extensions", "options", "context", "syntax", "symbol",
                "symbols", "program", "programs", "native", "build", "error", "errors",
                "platform", "posix", "win32", "wasi", "runtime", "sable_runtime"}

TYPE_DECL = re.compile(r"^\s*(?:public |internal |private |protected |sealed |static |abstract |partial |readonly |record |ref )*"
                       r"(?:class|struct|record|enum|interface)\s+([A-Z][A-Za-z0-9_]*)", re.M)
TEST_CLASS = re.compile(r"^public ((sealed|static|abstract|partial) )*class ([A-Za-z_][A-Za-z0-9_]*)", re.M)
IMPORT = re.compile(r"^import (Sable\.[A-Za-z0-9.]+)", re.M)


def words(name: str) -> set[str]:
    """'StackPromotionTests' -> {'stack', 'promotion'}; 'platform_ipc_posix' -> {'ipc'}."""
    parts = re.findall(r"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", name.replace("_", " ").replace(".", " "))
    return {p.lower() for p in parts if len(p) >= 3} - COMMON_WORDS


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True).stdout


def changed_files(base: str | None) -> list[str]:
    if base is None:
        for candidate in ("origin/master", "master"):
            try:
                base = git("merge-base", "HEAD", candidate).strip()
                break
            except subprocess.CalledProcessError:
                continue
    files = set(git("diff", "--name-only", f"{base}...HEAD").split()) if base else set()
    files |= set(git("diff", "--name-only", "HEAD").split())
    files |= set(git("ls-files", "--others", "--exclude-standard").split())
    return sorted(files)


class Index:
    """The test classes of each project, with their source, and the library's imports."""

    def __init__(self) -> None:
        self.classes: dict[str, dict[str, str]] = {}   # project -> class -> source text
        self.file_class: dict[str, tuple[str, str | None]] = {}
        for project in PROJECTS:
            found: dict[str, str] = {}
            for path in sorted((ROOT / f"tests/Sable.{project}.Tests").glob("*.cs")):
                text = path.read_text(encoding="utf-8", errors="replace")
                names = [m.group(3) for m in TEST_CLASS.finditer(text) if m.group(3).endswith("Tests")]
                rel = str(path.relative_to(ROOT))
                self.file_class[rel] = (project, names[0] if names else None)
                for name in names:
                    found[name] = found.get(name, "") + text
            self.classes[project] = found
        self.imports: dict[str, set[str]] = defaultdict(set)
        self.modules = sorted(p.name for p in (ROOT / "lib").iterdir() if p.is_dir() and p.name.startswith("Sable."))
        for module in self.modules:
            for path in (ROOT / "lib" / module).rglob("*.sb"):
                for m in IMPORT.finditer(path.read_text(encoding="utf-8", errors="replace")):
                    if m.group(1) != module:
                        self.imports[module].add(m.group(1))

    def importers(self, module: str) -> set[str]:
        """Every module that imports `module`, directly or through another."""
        found: set[str] = set()
        frontier = {module}
        while frontier:
            nxt = {m for m in self.modules if self.imports[m] & frontier} - found - {module}
            found |= nxt
            frontier = nxt
        return found

    def by_words(self, name: str, projects: list[str]) -> set[tuple[str, str]]:
        want = words(name)
        return {(p, c) for p in projects for c in self.classes[p] if want & words(c)}

    def naming(self, type_names: set[str], projects: list[str], limit: int = 6) -> tuple[set[tuple[str, str]], bool]:
        """Classes whose source names one of the types, and whether the file is
        central: a type named by more than `limit` classes says nothing about
        which, so it is left out and the file is taken to reach them all."""
        out: set[tuple[str, str]] = set()
        central = False
        for t in type_names:
            pattern = re.compile(rf"\b{re.escape(t)}\b")
            hits = {(p, c) for p in projects for c, text in self.classes[p].items() if pattern.search(text)}
            if len(hits) > limit:
                central = True
            elif hits:
                out |= hits
        return out, central


class Plan:
    def __init__(self) -> None:
        self.classes: dict[tuple[str, str], set[str]] = defaultdict(set)
        self.projects: dict[str, set[str]] = defaultdict(set)     # whole projects
        self.modules: dict[str, set[str]] = defaultdict(set)
        self.importers: dict[str, set[str]] = defaultdict(set)   # default mode only
        self.docs: set[str] = set()
        self.coverage: set[str] = set()
        self.fmt = False
        self.unmapped: list[str] = []

    def add_class(self, project: str, name: str, why: str) -> None:
        self.classes[(project, name)].add(why)

    def add_module(self, module: str, why: str) -> None:
        if (ROOT / "tests/lib" / module).is_dir():
            self.modules[module].add(why)

    def add_importer(self, module: str, why: str) -> None:
        if (ROOT / "tests/lib" / module).is_dir():
            self.importers[module].add(why)


def plan_for(files: list[str], index: Index) -> Plan:
    plan = Plan()
    for f in files:
        path = Path(f)
        mapped = False
        if f.endswith(".sb"):
            plan.fmt = True

        for prefix, targets in PATH_CLASSES:
            if f.startswith(prefix):
                for project, name in targets:
                    if name in index.classes[project]:
                        plan.add_class(project, name, f)
                mapped = True

        m = re.match(r"tests/Sable\.([A-Za-z]+)\.Tests/(.+\.cs)$", f)
        if m and m.group(1) in PROJECTS:
            project, cls = index.file_class.get(f, (m.group(1), None))
            if cls:
                plan.add_class(project, cls, f)
            elif (ROOT / f).exists():
                plan.projects[project].add(f"{f} (a helper: every class may use it)")
            mapped = True

        m = re.match(r"src/Sable\.([A-Za-z]+)/(.+)\.cs$", f)
        if m:
            source = ROOT / f
            declared = set(TYPE_DECL.findall(source.read_text(encoding="utf-8", errors="replace"))) if source.exists() else set()
            stem = path.stem.split(".")[0]
            seen = DOWNSTREAM.get(m.group(1), PROJECTS)
            named, central = index.naming(declared, seen)
            # The file's own project's tests, and the classes named for it.
            own = {"Syntax": "Syntax", "Symbols": "Symbols", "SIL": "SIL", "CodeGen": "CodeGen",
                   "LanguageServer": "LanguageServer"}.get(m.group(1))
            if central and own:
                plan.projects[own].add(f"{f} (central: its types are used by most of the project's tests)")
            for project, cls in (index.by_words(stem, seen)
                                 | named
                                 | {(p, t + "Tests") for p in seen for t in declared | {stem}
                                    if t + "Tests" in index.classes[p]}):
                plan.add_class(project, cls, f)
            if m.group(1) in ("Syntax", "Symbols", "SIL", "CodeGen"):
                for cls in COMPILER_SMOKE:
                    plan.add_class("CodeGen", cls, f"{f} (a compiler change)")
            if m.group(1) == "Driver":
                plan.add_class("CodeGen", "CliTests", f)
            mapped = True

        m = re.match(r"runtime/(.+)\.[ch]$", f)
        if m:
            name = re.sub(r"^platform_|_(posix|win32|wasi)$", "", m.group(1))
            name = re.sub(r"_(posix|win32|wasi)$", "", name)
            for cls in RUNTIME_SMOKE:
                plan.add_class("CodeGen", cls, f"{f} (a runtime change)")
            for project, cls in index.by_words(name, ["CodeGen"]):
                plan.add_class(project, cls, f)
            key = name.split("_")[0]
            for module in RUNTIME_MODULES.get(key, ["Sable.Core"]):
                plan.add_module(module, f)
            if m.group(1).endswith("win32") or m.group(1).endswith("wasi") or "_posix" in m.group(1):
                plan.unmapped.append(f"{f}: only another OS builds this; CI is the check")
            mapped = True

        m = re.match(r"lib/(Sable\.[A-Za-z0-9.]+?)/(.+)$", f)
        if m and m.group(1) in index.modules:
            module = m.group(1)
            plan.add_module(module, f)
            plan.docs.add(module)
            plan.coverage.add(module)
            if module != "Sable.Core":
                for other in index.importers(module):
                    plan.add_importer(other, f"{f} (it imports {module})")
            for cls in LIBRARY_INTERFACE:
                plan.add_class("CodeGen", cls, f"{f} (the library's interface)")
            for project, cls in index.by_words(module.removeprefix("Sable."), ["CodeGen"]):
                plan.add_class(project, cls, f)
            if "/generators/" in f:
                for cls in ("GeneratorTests", "SourceGeneratorTests"):
                    plan.add_class("CodeGen", cls, f)
            mapped = True

        m = re.match(r"tests/lib/(Sable\.[A-Za-z0-9.]+?)/", f)
        if m:
            plan.add_module(m.group(1), f)
            mapped = True

        if f in ("tools/stdlib-coverage.sh", "tools/stdlib-coverage.thresholds"):
            mapped = True   # checked by the coverage runs the plan has, if any
        if not mapped and not f.endswith(".md"):
            plan.unmapped.append(f"{f}: nothing here maps it to tests")
    for module in list(plan.importers):
        if module in plan.modules:
            del plan.importers[module]
    # A class named twice by a whole-project run needs no run of its own.
    for (project, cls) in list(plan.classes):
        if project in plan.projects:
            del plan.classes[(project, cls)]
    return plan


def show(plan: Plan, modes: list[str]) -> None:
    def section(title: str) -> None:
        print(f"\n{title}")

    if plan.projects:
        section("Whole test projects:")
        for project, why in sorted(plan.projects.items()):
            print(f"  Sable.{project}.Tests   <- {sorted(why)[0]}")
    by_project: dict[str, list[tuple[str, set[str]]]] = defaultdict(list)
    for (project, cls), why in plan.classes.items():
        by_project[project].append((cls, why))
    for project in PROJECTS:
        if by_project[project]:
            section(f"Sable.{project}.Tests classes:")
            for cls, why in sorted(by_project[project]):
                more = f" (+{len(why) - 1} more)" if len(why) > 1 else ""
                print(f"  {cls:<36} <- {sorted(why)[0]}{more}")
    if plan.modules:
        section(f"Library tests ({', '.join(modes)}):")
        for module, why in sorted(plan.modules.items()):
            more = f" (+{len(why) - 1} more)" if len(why) > 1 else ""
            print(f"  {module:<28} <- {sorted(why)[0]}{more}")
    if plan.importers:
        section("Library tests of the modules importing those (default mode):")
        for module, why in sorted(plan.importers.items()):
            print(f"  {module:<28} <- {sorted(why)[0]}")
    if plan.docs:
        section("Doc examples:  " + " ".join(sorted(plan.docs)))
    if plan.coverage:
        section("Coverage:      " + " ".join(sorted(plan.coverage)))
    if plan.fmt:
        section("Formatting:    sable fmt --check")
    if plan.unmapped:
        section("Not mapped, or not checked here:")
        for line in plan.unmapped:
            print(f"  {line}")


def filter_for(project: str, classes: list[str]) -> str:
    # 'Name.' so that one class's name is never taken for the start of another's.
    names = "|".join(f"FullyQualifiedName~Sable.{project}.Tests.{c}." for c in sorted(classes))
    return f"({names})&{EXCLUDE}"


def run(plan: Plan, modes: list[str], native: bool) -> int:
    results: list[tuple[str, bool]] = []

    def step(label: str, cmd: list[str], env: dict[str, str] | None = None) -> bool:
        print(f"\n==> {label}", flush=True)
        full_env = {**os.environ, **(env or {})}
        ok = subprocess.run(["nice", *cmd], cwd=ROOT, env=full_env).returncode == 0
        results.append((label, ok))
        return ok

    if not step("build", ["dotnet", "build", "Sable.slnx", "-m:2", "-v", "quiet", "--nologo"]):
        return 1
    for project in sorted(plan.projects):
        step(f"Sable.{project}.Tests", ["dotnet", "test", f"tests/Sable.{project}.Tests", "--no-build",
                                         "--filter", EXCLUDE])
    by_project: dict[str, list[str]] = defaultdict(list)
    for project, cls in plan.classes:
        by_project[project].append(cls)
    for project in PROJECTS:
        if by_project[project]:
            env = {"SABLE_TEST_NATIVE": "all"} if native and project == "CodeGen" else None
            step(f"Sable.{project}.Tests: {len(by_project[project])} classes",
                 ["dotnet", "test", f"tests/Sable.{project}.Tests", "--no-build",
                  "--filter", filter_for(project, by_project[project])], env)
    for module in sorted(plan.modules):
        for mode in modes:
            step(f"{module} ({mode})", [str(SABLE), "test", f"tests/lib/{module}"], MODES[mode])
    for module in sorted(plan.importers):
        step(f"{module} (default, imports a changed module)", [str(SABLE), "test", f"tests/lib/{module}"])
    for module in sorted(plan.docs):
        step(f"{module} doc examples", [str(SABLE), "test", "--doc", f"lib/{module}"])
    if plan.coverage:
        step("coverage", ["tools/stdlib-coverage.sh", *sorted(plan.coverage)], {"SABLE": str(SABLE)})
    if plan.fmt:
        step("formatting", [str(SABLE), "fmt", "--check", "lib", "tests/lib", "examples", "tools/fuzz"])

    print("\nSummary:")
    for label, ok in results:
        print(f"  {'ok    ' if ok else 'FAILED'} {label}")
    return 0 if all(ok for _, ok in results) else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", help="what the change is measured from (default: merge base with origin/master)")
    parser.add_argument("--files", nargs="+", help="these files instead of the branch's change")
    parser.add_argument("--run", action="store_true", help="run the plan")
    parser.add_argument("--native", action="store_true", help="CodeGen tests built natively too")
    parser.add_argument("--modes", default="default,unspecialised,shared",
                        help="library test modes, comma-separated: default, unspecialised, shared")
    args = parser.parse_args()

    modes = [m for m in args.modes.split(",") if m]
    unknown = [m for m in modes if m not in MODES]
    if unknown:
        parser.error(f"unknown mode {unknown[0]!r}: the modes are {', '.join(MODES)}")
    files = args.files if args.files else changed_files(args.base)
    if not files:
        print("Nothing has changed.")
        return 0
    plan = plan_for(files, Index())
    print(f"{len(files)} changed file(s).", end="")
    show(plan, modes)
    return run(plan, modes, args.native) if args.run else 0


if __name__ == "__main__":
    sys.exit(main())
