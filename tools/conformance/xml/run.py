#!/usr/bin/env python3
"""Sable.Serialize.Xml against the W3C XML Conformance Test Suite.

    python3 -I tools/conformance/xml/run.py <xmlconf, unpacked>

The suite is https://www.w3.org/XML/Test/xmlts20130923.tar.gz (SHA-256
9b61db9f5dbffa545f4b8d78422167083a8568c59bd1129f94138f936cf6fc1f), whose
directory 'xmlconf' is given. Each case of XML 1.0 (fifth edition) and
Namespaces 1.0 that reads no external entity is read: a 'valid' or
'invalid' document must be well-formed, and read the same once it is
written again; a 'not-wf' one must be an error. The reader does not
validate, so 'invalid' only asks that a document be well-formed. Cases of
XML 1.1, of editions before the fifth, of 'error's, and those that need an
external entity or subset read are skipped, by their reason."""

import pathlib
import re
import sys
import xml.etree.ElementTree as ElementTree

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build


def collections(root):
    """The test files xmlconf.xml includes, by the entities it declares."""
    text = (root / "xmlconf.xml").read_text(encoding="utf-8")
    declared = dict(re.findall(r'<!ENTITY\s+(\S+)\s+SYSTEM\s+"([^"]+)"', text))
    used = re.findall(r"&([\w.-]+);", text.split("<TESTSUITE", 1)[1])
    return [root / declared[name] for name in used if name in declared]


def skipped(test):
    """Why the case is not run, or None."""
    kind = test.get("TYPE")
    if kind not in ("valid", "invalid", "not-wf"):
        return "an 'error' case: what it asks is optional"
    if test.get("VERSION", "1.0") != "1.0" or test.get("RECOMMENDATION", "XML1.0") in ("XML1.1", "NS1.1"):
        return "XML 1.1 or Namespaces 1.1"
    edition = test.get("EDITION")
    if edition and "5" not in edition.split():
        return "an edition before the fifth"
    if test.get("ENTITIES", "none") != "none":
        return "reads an external entity, which is never fetched"
    return None


def main():
    root = pathlib.Path(sys.argv[1]).resolve()
    cases = []
    tally = Tally()
    for collection in collections(root):
        # Some are fragments, of TEST elements and no one root.
        text = collection.read_bytes().decode("utf-8", "replace")
        text = re.sub(r"<\?xml[^>]*\?>|<!DOCTYPE[^>\[]*(\[[^\]]*\])?\s*>", "", text)
        tree = ElementTree.fromstring("<all>" + text + "</all>")
        for test in tree.iter("TEST"):
            part = test.get("TYPE")
            reason = skipped(test)
            if reason:
                tally.skip(part or "?", reason)
                continue
            path = (collection.parent / test.get("URI")).resolve()
            mode = "plain" if test.get("NAMESPACE") == "no" else "ns"
            cases.append((part, test.get("ID"), mode, path))
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), [f"{mode} {path}" for _, _, mode, path in cases])
    for (part, name, mode, path), answer in zip(cases, answers):
        wanted_error = part == "not-wf"
        passed = answer.startswith("error") if wanted_error else answer == "ok"
        tally.check(part, passed, f"{name} ({path.relative_to(root)}): {answer}")
    tally.finish()


if __name__ == "__main__":
    main()
