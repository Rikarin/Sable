#!/usr/bin/env python3
"""Sable.Serialize.Yaml against the yaml-test-suite.

    python3 -I tools/conformance/yaml/run.py <yaml-test-suite, its data branch>

For each case: in.yaml has exactly the events of test.event, or is an error
where there is an 'error' file; it has the same but for style once its events,
and once its nodes, are written; out.yaml and emit.yaml, the same document
written by another, have them too; it is the values in.json says, and still is once
it is decoded and encoded as Serializable values; and a YamlStream gives it back
byte for byte."""

import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build


def plain(events):
    """Events without what a writer chooses: styles, and how a document's
    start and end are marked."""
    made = []
    for event in events:
        if event.startswith(("+DOC", "-DOC")):
            event = event[:4]
        event = re.sub(r"^(\+SEQ) \[\]|^(\+MAP) \{\}", lambda match: match[1] or match[2], event)
        if event.startswith("=VAL"):
            event = re.sub(r"^(=VAL(?: &\S+)?(?: <[^>]*>)?) [:'\"|>]", r"\1 :", event)
        made.append(event)
    return made


# What the library does not do by design, and files of the suite that are not
# the same document.
TWICE = "the same key twice: events, but no nodes"
SPELLED = "a null written 'null', not nothing"
KNOWN = {
    ("printed", "2JQS"): TWICE,
    ("stream", "2JQS"): TWICE,
    ("printed", "X38W"): TWICE,
    ("stream", "X38W"): TWICE,
    ("out", "4ABK"): SPELLED,
    ("emit", "DK95/07"): SPELLED,
    ("encoded", "565N"): "base64 without its line breaks",
}


# What a node's tag decides once its style is gone.
def unstyled(events):
    return [re.sub(r" <!>", "", event) for event in plain(events)]


def values(text):
    decoder, at, all_values = json.JSONDecoder(), 0, []
    while at < len(text):
        if text[at].isspace():
            at += 1
            continue
        value, at = decoder.raw_decode(text, at)
        all_values.append(value)
    return all_values


def main():
    data = pathlib.Path(sys.argv[1]).resolve()
    cases = sorted(path.parent for path in data.rglob("in.yaml") if not {"name", "tags"} & set(path.relative_to(data).parts))
    asked = []
    for case in cases:
        asked += [("events", case / "in.yaml", case)]
        if (case / "error").exists():
            continue
        asked += [(kind, case / "in.yaml", case) for kind in ("emitted", "printed", "stream")]
        asked += [(name, case / f"{name}.yaml", case) for name in ("out", "emit") if (case / f"{name}.yaml").exists()]
        if (case / "in.json").exists():
            asked += [(kind, case / "in.yaml", case) for kind in ("json", "encoded")]
    requests = [f"{'events' if kind in ('out', 'emit') else kind} {path}" for kind, path, _ in asked]
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), requests)
    tally = Tally()
    for (kind, _, case), answer in zip(asked, answers):
        place = str(case.relative_to(data))
        name = place + " " + (case / "===").read_text().strip()
        if (kind, place) in KNOWN:
            tally.skip(kind, KNOWN[kind, place])
            continue
        expected = (case / "test.event").read_text().rstrip("\n").split("\n")
        got = answer.split("\t")
        if kind == "events" and (case / "error").exists():
            tally.check("invalid", answer.startswith("error"), f"{name}: {answer}")
        elif kind == "events":
            tally.check("valid", got == expected, f"{name}: {answer}, not {expected}")
        elif kind == "stream":
            tally.check("stream", answer == "same", f"{name}: {answer}")
        elif kind in ("json", "encoded"):
            want = values((case / "in.json").read_text())
            tally.check(kind, not answer.startswith("error") and json.loads(answer) == want, f"{name}: {answer}, not {want}")
        else:
            tally.check(kind, unstyled(got) == unstyled(expected), f"{name}: {answer}, not {expected}")
    tally.finish()


main()
