#!/usr/bin/env python3
"""Sable.Serialize's TOML against toml-lang/toml-test.

    python3 -I tools/conformance/toml/run.py <toml-test> [--version 1.0.0]

Every valid document must be the value its .json says, be the same once
written and read again, and come back byte for byte from a TomlDocument;
every invalid one must be an error."""

import argparse
import datetime
import json
import math
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build


def same(expected, got):
    if isinstance(expected, list):
        return isinstance(got, list) and len(expected) == len(got) and all(map(same, expected, got))
    if not isinstance(got, dict):
        return False
    leaf = set(expected) == {"type", "value"} and isinstance(expected["value"], str) and isinstance(expected["type"], str)
    if not leaf:
        return set(expected) == set(got) and all(same(expected[key], got[key]) for key in expected)
    if got.get("type") != expected["type"]:
        return False
    a, b = expected["value"], got["value"]
    if expected["type"] == "float":
        a, b = float(a), float(b)
        return math.isnan(a) and math.isnan(b) or a == b
    if expected["type"] in ("datetime", "datetime-local", "time-local"):
        return moment(a, expected["type"]) == moment(b, expected["type"])
    return a == b


def moment(text, kind):
    """A date and time, or a time, with its fraction to the nanosecond, as
    the same two values whatever their notation."""
    text = text.upper().replace(" ", "T")
    zone = ""
    if kind == "datetime":
        zone = "Z" if text.endswith("Z") else text[-6:]
        text = text[: -len(zone)]
    whole, _, fraction = text.partition(".")
    if whole.count(":") == 1:
        whole += ":00"
    if kind == "datetime":
        offset = "+00:00" if zone == "Z" else zone
        whole = datetime.datetime.fromisoformat(whole + offset).astimezone(datetime.timezone.utc)
    return whole, fraction.rstrip("0")[:9]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=pathlib.Path)
    parser.add_argument("--version", default="1.0.0")
    arguments = parser.parse_args()
    tests = arguments.data / "tests"
    names = [name for name in (tests / f"files-toml-{arguments.version}").read_text().split() if name.endswith(".toml")]
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), [str((tests / name).resolve()) for name in names])
    tally = Tally()
    for name, answer in zip(names, answers):
        kind = name.split("/")[0]
        if kind == "invalid":
            tally.check(kind, answer.startswith("error"), f"{name}: {answer[:300]}")
            continue
        expected = json.loads((tests / name).with_suffix(".json").read_text())
        passed = answer.startswith("{") and same(expected, json.loads(answer))
        tally.check(kind, passed, f"{name}: {answer[:300]}, not {json.dumps(expected)[:300]}")
    tally.finish()


main()
