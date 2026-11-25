"""What the suites' scripts share: building the Sable program that answers
for the library, and asking it."""

import os
import pathlib
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]


def sable():
    """The command that runs this repository's compiler, or $SABLE."""
    if "SABLE" in os.environ:
        return os.environ["SABLE"].split()
    for configuration in ("Release", "Debug"):
        driver = ROOT / "src/Sable.Driver/bin" / configuration / "net11.0/sable.dll"
        if driver.exists():
            return ["dotnet", str(driver)]
    sys.exit("build Sable.slnx first, or set SABLE")


def build(program):
    """The program built, optimised; the executable's path. It is gone when
    the script ends."""
    directory = tempfile.TemporaryDirectory(prefix="sable-conformance-")
    build.kept.append(directory)
    output = pathlib.Path(directory.name) / program.stem
    subprocess.run(sable() + ["build", str(program), "-o", str(output)], check=True, cwd=directory.name)
    return output


build.kept = []


def ask(executable, lines):
    """The program's answers, a line each, to the lines of requests."""
    text = "".join(line + "\n" for line in lines)
    done = subprocess.run([str(executable)], input=text.encode(), stdout=subprocess.PIPE)
    answers = done.stdout.decode().split("\n")[:-1]
    if done.returncode != 0 or len(answers) != len(lines):
        at = lines[len(answers)][:300] if len(answers) < len(lines) else "the end"
        sys.exit(f"{executable} ended with {done.returncode} after {len(answers)} of {len(lines)} answers, at: {at}")
    return answers


class Tally:
    """Counts what passed, failed and was skipped, by the part of a suite."""

    def __init__(self):
        self.parts = {}
        self.shown = 0

    def part(self, name):
        return self.parts.setdefault(name, [0, 0, {}])

    def check(self, part, passed, describe):
        counts = self.part(part)
        counts[0 if passed else 1] += 1
        if not passed and self.shown < 200:
            self.shown += 1
            print(f"FAIL {part}: {describe}")

    def skip(self, part, reason):
        reasons = self.part(part)[2]
        reasons[reason] = reasons.get(reason, 0) + 1

    def finish(self):
        width = max((len(name) for name in self.parts), default=0)
        total = [0, 0, 0]
        for name, (passed, failed, skipped) in sorted(self.parts.items()):
            reasons = "; ".join(f"{count} {reason}" for reason, count in sorted(skipped.items()))
            print(f"{name:{width}}  {passed:6} passed  {failed:4} failed  {sum(skipped.values()):5} skipped" + (f"  ({reasons})" if reasons else ""))
            total = [total[0] + passed, total[1] + failed, total[2] + sum(skipped.values())]
        print(f"{'all':{width}}  {total[0]:6} passed  {total[1]:4} failed  {total[2]:5} skipped")
        sys.exit(1 if total[1] else 0)
