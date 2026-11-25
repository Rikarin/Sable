#!/usr/bin/env python3
"""The library's generators against PractRand.

    python3 -I tools/conformance/random/run.py <RNG_test> [--size 8GB] [generator...]

One generator at a time, two threads in all: the Sable program that streams
it, and PractRand. Its report is printed, and a generator fails if PractRand
says FAIL of it. tools/conformance/README.md says how PractRand is built."""

import argparse
import pathlib
import subprocess
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, build

GENERATORS = ["xoshiro256", "pcg64", "chacha", "splitmix64", "system", "floats", "xoshiro256-jumps", "xoshiro256-seeds", "pcg64-streams", "chacha-streams"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("practrand", type=pathlib.Path)
    parser.add_argument("--size", default="8GB")
    parser.add_argument("generators", nargs="*", default=GENERATORS)
    arguments = parser.parse_args()
    program = build(pathlib.Path(__file__).resolve().parent / "stream.sb")
    tally = Tally()
    for name in arguments.generators:
        start = time.time()
        stream = subprocess.Popen([str(program), name], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        done = subprocess.run([str(arguments.practrand.resolve()), "stdin64", "-singlethreaded", "-tlmin", "1GB", "-tlmax", arguments.size], stdin=stream.stdout, stdout=subprocess.PIPE, text=True)
        stream.stdout.close()
        stream.kill()
        stream.wait()
        print(f"==== {name}, {arguments.size}, {time.time() - start:.0f} s\n{done.stdout}", flush=True)
        tally.check(name, done.returncode == 0 and "length=" in done.stdout and "FAIL" not in done.stdout, "PractRand says FAIL, or did not run")
    tally.finish()


main()
