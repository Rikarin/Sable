#!/usr/bin/env python3
"""Writes lib/Sable.Net/IdnaTables.sb: what Sable.Net's IDNA (UTS #46)
needs to turn a domain into ASCII, as the WHATWG URL standard asks.

    tools/idna-tables.py

It reads, from tools/unicode/ (not committed; downloaded by hand):

    IdnaMappingTable.txt  from https://www.unicode.org/Public/idna/<version>/
    UnicodeData.txt       from https://www.unicode.org/Public/<version>/ucd/
                          (tools/unicode-tables.py reads it too)

IdnaTestV2.txt, from the same folder as the mapping table, is committed in
tests/lib/Sable.Net/data/, where Sable.Net's tests read it. To move to a new
Unicode version: fetch both, set IDNA_VERSION, run this, copy the test over.

The tables are strings, as tools/unicode-tables.py's are and for the same
reason (see there): fixed-width records of base-32 digits, '0' (0) to 'O'
(31), RECORDS_PER_LINE to a line, so record r starts at byte
r * width + r / RECORDS_PER_LINE.

- idnaStatusTable: each code point's status, as runs. A record is the
  first code point of a run (5 digits) and its status (1): 0 valid, 1
  ignored, 2 mapped, 3 deviation, 4 disallowed. (Since Unicode 15.1 there
  are no STD3 statuses: UseSTD3ASCIIRules is applied by the code.)
- idnaMappingTable: each mapped code point (5 digits), where its mapping
  starts in the pool (4) and how many code points it has (1).
- idnaMappingPool: the mappings' code points, 5 digits each.
- bidiClassTable: Bidi_Class, as runs: a code point (5) and its class (1),
  its place in BIDI_CLASSES. Unassigned code points are L here; IDNA
  disallows them before it asks.
- viramaTable: the code points of Canonical_Combining_Class 9 (Virama),
  5 digits each, which the CONTEXTJ rules ask for.
- markTable: where General_Category M (Mn, Mc, Me) turns on and off,
  alternately: a code point is a mark when the records at or below it are
  odd in number. A label may not start with one.
"""

import pathlib

IDNA_VERSION = "16.0.0"
ROOT = pathlib.Path(__file__).resolve().parents[1]
UCD = ROOT / "tools" / "unicode"
OUT = ROOT / "lib" / "Sable.Net" / "IdnaTables.sb"
DIGITS = "".join(chr(0x30 + i) for i in range(32))  # '0'..'O'
RECORDS_PER_LINE = 24
STATUSES = ["valid", "ignored", "mapped", "deviation", "disallowed"]
BIDI_CLASSES = ["L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON",
                "LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI"]


def digits(value, width):
    if value < 0 or value >= 32 ** width:
        raise ValueError(f"{value} does not fit {width} base-32 digits")
    out = []
    for _ in range(width):
        out.append(DIGITS[value % 32])
        value //= 32
    return "".join(reversed(out))


def data_lines(path):
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.split("#", 1)[0].strip()
            if line:
                yield [field.strip() for field in line.split(";")]


def code_range(field):
    if ".." in field:
        low, high = field.split("..")
        return int(low, 16), int(high, 16)
    return int(field, 16), int(field, 16)


def literal(records):
    lines = []
    for i in range(0, len(records), RECORDS_PER_LINE):
        lines.append("    " + "".join(records[i:i + RECORDS_PER_LINE]))
    return '"""\n' + "\n".join(lines) + '\n    """'


def table(name, records, width, comment):
    assert all(len(r) == width for r in records)
    return (
        f"// {comment}\n"
        f"// {len(records)} records of {width} digits.\n"
        f"const {name}Count = {len(records)}\n"
        f"const {name} = {literal(records)}\n"
    )


def runs(values):
    out = []
    previous = None
    for code, value in enumerate(values):
        if value != previous:
            out.append((code, value))
            previous = value
    return out


def main():
    status = [None] * 0x110000
    mapping = {}
    for fields in data_lines(UCD / "IdnaMappingTable.txt"):
        low, high = code_range(fields[0])
        kind = fields[1]
        assert kind in STATUSES, kind
        for code in range(low, high + 1):
            status[code] = STATUSES.index(kind)
            if kind == "mapped":
                mapping[code] = [int(c, 16) for c in fields[2].split()]
    assert all(s is not None for s in status), "a code point with no status"

    pool = []
    pooled = {}
    mapping_records = []
    for code in sorted(mapping):
        target = tuple(mapping[code])
        if target not in pooled:
            pooled[target] = len(pool)
            pool.extend(target)
        mapping_records.append(digits(code, 5) + digits(pooled[target], 4) + digits(len(target), 1))

    bidi = [0] * 0x110000
    combining = {}
    marks = [False] * 0x110000
    pending = None
    for fields in data_lines(UCD / "UnicodeData.txt"):
        code = int(fields[0], 16)
        name, category, ccc, klass = fields[1], fields[2], int(fields[3]), fields[4]
        if name.endswith(", First>"):
            pending = code
            continue
        first = pending if name.endswith(", Last>") else code
        pending = None
        for c in range(first, code + 1):
            bidi[c] = BIDI_CLASSES.index(klass)
            combining[c] = ccc
            marks[c] = category.startswith("M")

    status_records = [digits(code, 5) + digits(value, 1) for code, value in runs(status)]
    bidi_records = [digits(code, 5) + digits(value, 1) for code, value in runs(bidi)]
    virama_records = [digits(code, 5) for code in sorted(c for c, v in combining.items() if v == 9)]
    mark_records = []
    on = False
    for code, value in enumerate(marks):
        if value != on:
            mark_records.append(digits(code, 5))
            on = value

    text = (
        "module Sable.Net\n\n"
        f"// Generated by tools/idna-tables.py from Unicode {IDNA_VERSION}'s IdnaMappingTable.txt\n"
        "// and UnicodeData.txt: do not edit; see there for the format.\n\n"
        f"const idnaRecordsPerLine = {RECORDS_PER_LINE}\n\n"
        + table("idnaStatusTable", status_records, 6, "Each code point's IDNA status, as runs: the first code point and the status.")
        + "\n"
        + table("idnaMappingTable", mapping_records, 10, "Each mapped code point, where its mapping starts in the pool, and its length.")
        + "\n"
        + table("idnaMappingPool", [digits(c, 5) for c in pool], 5, "The mappings' code points.")
        + "\n"
        + table("bidiClassTable", bidi_records, 6, "Bidi_Class, as runs: the first code point and the class.")
        + "\n"
        + table("viramaTable", virama_records, 5, "The code points of Canonical_Combining_Class 9 (Virama).")
        + "\n"
        + table("markTable", mark_records, 5, "Where General_Category M turns on and off.")
    )
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(text, encoding="utf-8")
    print(f"{OUT}: {len(status_records)} status runs, {len(mapping_records)} mappings, {len(pool)} pooled, "
          f"{len(bidi_records)} bidi runs, {len(virama_records)} viramas, {len(mark_records)} mark toggles")


if __name__ == "__main__":
    main()
