#!/usr/bin/env python3
"""The standard library's coverage as Markdown tables: a row a module, the
files under their thresholds, then every file, folded by module.

    tools/ci/coverage-report.py [target/stdlib-coverage/report.tsv]

Reads what tools/stdlib-coverage.sh left, which says what its columns are,
and prints to standard output, as for $GITHUB_STEP_SUMMARY.
"""

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]


class Row:
    def __init__(self, line):
        module, file, *numbers, verdict = line.split("\t")
        self.module, self.file, self.verdict = module, file, verdict
        self.lines_run, self.lines, self.branches_run, self.branches = map(int, numbers[:4])
        self.min_lines, self.min_branches = numbers[4:]


def share(part, whole):
    return "—" if whole == 0 else f"{100 * part / whole:.1f}%"


def bar(part, whole):
    filled = 0 if whole == 0 else 10 * part // whole
    return "█" * filled + "░" * (10 - filled)


def totals(rows):
    return [sum(getattr(row, name) for row in rows) for name in ("lines_run", "lines", "branches_run", "branches")]


def files_table(rows):
    print("| file | lines | | branches | | least (lines / branches) | |")
    print("|:--|--:|--:|--:|--:|--:|:-:|")
    for row in rows:
        mark = "✅" if row.verdict == "ok" else "❌"
        print(
            f"| `{row.file.removeprefix('lib/')}` | {share(row.lines_run, row.lines)} | {row.lines_run}/{row.lines} "
            f"| {share(row.branches_run, row.branches)} | {row.branches_run}/{row.branches} | {row.min_lines} / {row.min_branches} | {mark} |"
        )


def main():
    path = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "target" / "stdlib-coverage" / "report.tsv"
    rows = [Row(line) for line in path.read_text().splitlines() if line]
    modules = {}
    for row in rows:
        modules.setdefault(row.module, []).append(row)

    print("## Standard library coverage\n")
    print("| module | files | lines | | | branches | | status |")
    print("|:--|--:|:--|--:|--:|--:|--:|:--|")
    for module, its in modules.items():
        if its[0].verdict == "FAILED":
            print(f"| {module} | | | | | | | ❌ tests failed |")
            continue
        run, lines, branches_run, branches = totals(its)
        under = sum(row.verdict == "UNDER" for row in its)
        print(
            f"| {module} | {len(its)} | {bar(run, lines)} | {share(run, lines)} | {run}/{lines} "
            f"| {share(branches_run, branches)} | {branches_run}/{branches} | {f'❌ {under} under' if under else '✅'} |"
        )
    measured = [row for row in rows if row.file]
    run, lines, branches_run, branches = totals(measured)
    print(
        f"| **total** | **{len(measured)}** | {bar(run, lines)} | **{share(run, lines)}** | {run}/{lines} "
        f"| **{share(branches_run, branches)}** | {branches_run}/{branches} | |"
    )

    under = [row for row in rows if row.verdict == "UNDER"]
    if under:
        print("\n### Under their thresholds\n")
        files_table(under)

    print("\n### Every file")
    for module, its in modules.items():
        if its[0].verdict != "FAILED":
            run, lines, *_ = totals(its)
            print(f"\n<details><summary>{module}: {share(run, lines)} of lines</summary>\n")
            files_table(its)
            print("\n</details>")


if __name__ == "__main__":
    sys.exit(main())
