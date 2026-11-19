#!/usr/bin/env python3
"""Writes lib/Sable.Core/UnicodeTables.sb from the Unicode Character Database.

    python3 -I tools/unicode-tables.py [ucd-directory]

The directory (default tools/unicode/) holds the UCD files of the version
named below, fetched by hand once, never at build time:

    UnicodeData.txt  CaseFolding.txt  SpecialCasing.txt  PropList.txt
    DerivedCoreProperties.txt

from https://www.unicode.org/Public/<version>/ucd/. They are not committed
(.gitignore); the tables made from them are. To move to a new Unicode
version: fetch that version's files into tools/unicode/, set UCD_VERSION
below, run this, and run the Char and String tests (tests/lib/Sable.Core/).

Why the tables are strings. A Sable array literal is built at run time, an
append per element, and thousands of them would add seconds to every build
that reads one (#265, which would let these become constant arrays). A
string literal is one immortal object in read-only memory,
made by nothing at run time, so each table is the text of a string literal
that the lookups in Char.sb read a byte at a time ('String.utf8'). Every
number is written in base 32, five bits a character, with the digits '0' (0)
to 'O' (31): characters that need no escape in a literal and are never a
line's indentation. A table is a run of fixed-width records, RECORDS_PER_LINE
to a line, the lines joined by '\n' in a multi-line literal, so record r
starts at byte r * width + r / RECORDS_PER_LINE.

The tables:

- generalCategoryTable: the general category of every scalar, as runs. A
  record is the first code point of a run (5 digits) and the category (1
  digit, its place in CATEGORIES); a run lasts until the next one starts.
- <property>Table (White_Space, Alphabetic, Uppercase, Lowercase,
  Case_Ignorable): where the property turns on and off, alternately, from
  off below the first. A record is a code point (5 digits); a scalar has the
  property when the number of records at or below it is odd.
- upperMappingTable, lowerMappingTable, foldingTable: the simple (one
  scalar to one) case mappings and case folding (C + S), as runs. A record is
  the first code point (5), the run's length (3), the offset to add (5,
  biased by 2^20) and the stride (1): 1 when every scalar of the run maps,
  2 when every other does (Latin Extended's alternating pairs).
- upperSpecialTable, lowerSpecialTable: the unconditional mappings to more
  than one scalar from SpecialCasing.txt ('ß' to "SS"). A record is the
  code point (5) and three code points of the mapping (5 each), 0 for none.
  The language-specific and context-specific ones are left out; Final_Sigma,
  the one context every language has, is String.lowercased()'s.
"""

import os
import sys

UCD_VERSION = "16.0.0"
RECORDS_PER_LINE = 12
DIGITS = "".join(chr(0x30 + i) for i in range(32))  # '0'..'O'
OUTPUT = "lib/Sable.Core/UnicodeTables.sb"

# The order Char.generalCategory's GeneralCategory cases are in.
CATEGORIES = [
    "Lu", "Ll", "Lt", "Lm", "Lo",
    "Mn", "Mc", "Me",
    "Nd", "Nl", "No",
    "Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po",
    "Sm", "Sc", "Sk", "So",
    "Zs", "Zl", "Zp",
    "Cc", "Cf", "Cs", "Co", "Cn",
]


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


def general_categories(ucd):
    category = ["Cn"] * 0x110000
    first = None
    for fields in data_lines(os.path.join(ucd, "UnicodeData.txt")):
        code = int(fields[0], 16)
        name = fields[1]
        if name.endswith(", First>"):
            first = code
            continue
        if name.endswith(", Last>"):
            for c in range(first, code + 1):
                category[c] = fields[2]
            continue
        category[code] = fields[2]
    return category


def simple_mappings(ucd):
    upper, lower = {}, {}
    for fields in data_lines(os.path.join(ucd, "UnicodeData.txt")):
        code = int(fields[0], 16)
        if fields[12]:
            upper[code] = int(fields[12], 16)
        if fields[13]:
            lower[code] = int(fields[13], 16)
    return upper, lower


def folding(ucd):
    fold = {}
    for fields in data_lines(os.path.join(ucd, "CaseFolding.txt")):
        if fields[1] in ("C", "S"):
            fold[int(fields[0], 16)] = int(fields[2], 16)
    return fold


def special_casing(ucd):
    upper, lower = {}, {}
    for fields in data_lines(os.path.join(ucd, "SpecialCasing.txt")):
        # A fifth field is a condition: a language or a context.
        if len(fields) > 4 and fields[4]:
            continue
        code = int(fields[0], 16)
        lowered = [int(c, 16) for c in fields[1].split()]
        uppered = [int(c, 16) for c in fields[3].split()]
        if lowered != [code] and len(lowered) > 1:
            lower[code] = lowered
        if uppered != [code] and len(uppered) > 1:
            upper[code] = uppered
    return upper, lower


def property_set(ucd, file, name):
    members = set()
    for fields in data_lines(os.path.join(ucd, file)):
        if fields[1] == name:
            low, high = code_range(fields[0])
            members.update(range(low, high + 1))
    return members


def toggles(members):
    points = []
    inside = False
    for c in range(0x110000 + 1):
        now = c in members
        if now != inside:
            points.append(c)
            inside = now
    return points


def category_runs(category):
    runs = []
    for c in range(0x110000):
        if not runs or runs[-1][1] != category[c]:
            runs.append((c, category[c]))
    return runs


def mapping_runs(mapping):
    """Runs of (start, length, delta, stride) covering every mapped scalar."""
    runs = []
    for code in sorted(mapping):
        delta = mapping[code] - code
        if runs:
            start, length, run_delta, stride = runs[-1]
            last = start + (length - 1) * stride
            if run_delta == delta:
                if length == 1 and code - last in (1, 2):
                    runs[-1] = (start, 2, delta, code - last)
                    continue
                if code - last == stride:
                    runs[-1] = (start, length + 1, delta, stride)
                    continue
        runs.append((code, 1, delta, 1))
    return runs


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


def main():
    ucd = sys.argv[1] if len(sys.argv) > 1 else "tools/unicode"
    category = general_categories(ucd)
    upper, lower = simple_mappings(ucd)
    fold = folding(ucd)
    special_upper, special_lower = special_casing(ucd)

    parts = []
    runs = category_runs(category)
    parts.append(table(
        "generalCategoryTable",
        [digits(start, 5) + digits(CATEGORIES.index(cat), 1) for start, cat in runs],
        6, "The general category: runs of (first code point, category).",
    ))

    for name, file, prop in [
        ("whiteSpaceTable", "PropList.txt", "White_Space"),
        ("alphabeticTable", "DerivedCoreProperties.txt", "Alphabetic"),
        ("uppercaseTable", "DerivedCoreProperties.txt", "Uppercase"),
        ("lowercaseTable", "DerivedCoreProperties.txt", "Lowercase"),
        ("caseIgnorableTable", "DerivedCoreProperties.txt", "Case_Ignorable"),
    ]:
        points = toggles(property_set(ucd, file, prop))
        parts.append(table(name, [digits(p, 5) for p in points], 5,
                           f"{prop} ({file}): where it turns on and off."))

    for name, mapping, what in [
        ("upperMappingTable", upper, "Simple_Uppercase_Mapping (UnicodeData.txt)"),
        ("lowerMappingTable", lower, "Simple_Lowercase_Mapping (UnicodeData.txt)"),
        ("foldingTable", fold, "Simple case folding, statuses C and S (CaseFolding.txt)"),
    ]:
        records = [
            digits(start, 5) + digits(length, 3) + digits(delta + (1 << 20), 5) + digits(stride, 1)
            for start, length, delta, stride in mapping_runs(mapping)
        ]
        parts.append(table(name, records, 14, f"{what}: runs of (first, length, offset + 2^20, stride)."))

    for name, mapping, what in [
        ("upperSpecialTable", special_upper, "Uppercase"),
        ("lowerSpecialTable", special_lower, "Lowercase"),
    ]:
        records = []
        for code in sorted(mapping):
            scalars = mapping[code] + [0] * (3 - len(mapping[code]))
            records.append(digits(code, 5) + "".join(digits(s, 5) for s in scalars))
        parts.append(table(name, records, 20,
                           f"{what} mappings to more than one scalar, unconditional (SpecialCasing.txt)."))

    header = (
        "module Sable.Core\n\n"
        f"// Generated by tools/unicode-tables.py from the Unicode Character Database\n"
        f"// {UCD_VERSION}; do not edit. To regenerate, or to move to another Unicode\n"
        "// version, see that script: it says where the files come from and how\n"
        "// these tables are laid out. Char.sb reads them.\n\n"
        f'/// The version of Unicode the character properties and case mappings are from.\n'
        f'public const unicodeVersion = "{UCD_VERSION}"\n\n'
        f"// Records per line of each table: record r starts at byte r * width + r / {RECORDS_PER_LINE}.\n"
        f"const unicodeRecordsPerLine = {RECORDS_PER_LINE}\n\n"
    )
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(header + "\n".join(parts))
    size = os.path.getsize(OUTPUT)
    print(f"wrote {OUTPUT}: {size} bytes")


if __name__ == "__main__":
    main()
