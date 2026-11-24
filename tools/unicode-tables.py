#!/usr/bin/env python3
"""Writes lib/Sable.Core/UnicodeTables.sb from the Unicode Character Database.

    python3 -I tools/unicode-tables.py [ucd-directory]

The directory (default tools/unicode/) holds the UCD files of the version
named below, fetched by hand once, never at build time:

    UnicodeData.txt  CaseFolding.txt  SpecialCasing.txt  PropList.txt
    DerivedCoreProperties.txt  DerivedNormalizationProps.txt
    LineBreak.txt  EastAsianWidth.txt
    GraphemeBreakProperty.txt  WordBreakProperty.txt
    SentenceBreakProperty.txt  (from auxiliary/)
    emoji-data.txt  (from emoji/)

from https://www.unicode.org/Public/<version>/ucd/. They are not committed
(.gitignore); the tables made from them are. The UCD's conformance tests
(NormalizationTest.txt, and GraphemeBreakTest.txt, WordBreakTest.txt,
SentenceBreakTest.txt and LineBreakTest.txt from auxiliary/) are committed
in tests/lib/Sable.Text/ucd/, where Sable.Text's tests read them. To move to
a new Unicode version: fetch that version's files into tools/unicode/, set
UCD_VERSION below, run this, copy the five tests over, and run the Char,
String and Sable.Text tests (tests/lib/Sable.Core/, tests/lib/Sable.Text/).

It writes two files: lib/Sable.Core/UnicodeTables.sb, the properties of a
scalar Char answers, and lib/Sable.Text/UnicodeTextTables.sb, what text
segmentation and normalization need (described after the first's).

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
- fullFoldingTable: the case foldings to more than one scalar (status F of
  CaseFolding.txt, 'ß' to "ss"), as the two above: String.caseFolded()'s.

The text tables (Sable.Text), TEXT_RECORDS_PER_LINE records to a line:

- <name>Index and <name>Blocks, a property of every code point in two
  stages: the code point's block of 128 (code >> 7) indexes <name>Index, two
  digits, which names a block of <name>Blocks; the value is the (code & 127)th
  record of that block. Blocks alike are kept once. They are:
  graphemeBreak (Grapheme_Cluster_Break, plus 16 for Extended_Pictographic,
  plus 32 times Indic_Conjunct_Break), wordBreak (Word_Break, plus 32 for
  Extended_Pictographic), sentenceBreak (Sentence_Break), lineBreak
  (Line_Break as rule LB1 resolves it, QU split by its general category, plus
  64 for East_Asian_Width F, W or H, plus 128 for an Extended_Pictographic
  code point not yet assigned) and combiningClass (Canonical_Combining_Class).
  Each value's number is a constant written beside them.
- canonicalDecompositionTable, compatibilityDecompositionTable: each code
  point's full decomposition, canonical, or canonical and compatibility both,
  applied until nothing more decomposes (Hangul syllables are computed, not
  listed). A record is the code point (5), where its scalars start in the
  table's pool (4) and how many there are (1); the pool, <name>Pool, is the
  scalars, 5 digits each, one after another.
- compositionTable: each primary composite, by the pair it composes from: a
  record is the first scalar (5), the second (5) and the composite (5),
  sorted by the pair, Full_Composition_Exclusion left out.
"""

import os
import sys

UCD_VERSION = "16.0.0"
RECORDS_PER_LINE = 12
TEXT_RECORDS_PER_LINE = 48
TEXT_OUTPUT = "lib/Sable.Text/UnicodeTextTables.sb"
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


# ---- Sable.Text's tables --------------------------------------------------

GRAPHEME_BREAK = ["Other", "CR", "LF", "Control", "Extend", "ZWJ", "Regional_Indicator", "Prepend",
                  "SpacingMark", "L", "V", "T", "LV", "LVT"]
INCB = ["None", "Linker", "Consonant", "Extend"]
WORD_BREAK = ["Other", "CR", "LF", "Newline", "Extend", "ZWJ", "Regional_Indicator", "Format", "Katakana",
              "Hebrew_Letter", "ALetter", "Single_Quote", "Double_Quote", "MidNumLet", "MidLetter", "MidNum",
              "Numeric", "ExtendNumLet", "WSegSpace"]
SENTENCE_BREAK = ["Other", "CR", "LF", "Extend", "Sep", "Format", "Sp", "Lower", "Upper", "OLetter",
                  "Numeric", "ATerm", "SContinue", "STerm", "Close"]
# Line_Break after LB1: AI, SG and XX are AL, SA is CM or AL, CJ is NS. QU
# is split by general category, as LB15a, LB15b and LB19a ask.
LINE_BREAK = ["AL", "BK", "CR", "LF", "NL", "CM", "ZWJ", "WJ", "ZW", "GL", "SP", "B2", "BA", "BB", "HY",
              "CB", "CL", "CP", "EX", "IN", "NS", "OP", "QU", "QU_Pi", "QU_Pf", "IS", "NU", "PO", "PR",
              "SY", "EB", "EM", "H2", "H3", "HL", "ID", "JL", "JV", "JT", "RI", "AK", "AP", "AS", "VF",
              "VI"]
STAGE_SHIFT = 7


def text_digits(value, width):
    assert 0 <= value < 32 ** width, (value, width)
    return digits(value, width)


def ranged(ucd, file, default, field=1, where=None):
    """A value for every code point from a UCD file of ranges, 'default' where none is given."""
    values = [default] * 0x110000
    for fields in data_lines(os.path.join(ucd, file)):
        if where is not None and fields[1] != where:
            continue
        low, high = code_range(fields[0])
        for c in range(low, high + 1):
            values[c] = fields[field]
    return values


def staged(values):
    """The two stages of a table of every code point's value: an index of
    blocks and the distinct blocks, and the digits a value takes."""
    width = 1 if max(values) < 32 else 2
    blocks, index = {}, []
    size = 1 << STAGE_SHIFT
    for start in range(0, 0x110000, size):
        block = tuple(values[start:start + size])
        if block not in blocks:
            blocks[block] = len(blocks)
        index.append(blocks[block])
    flat = [v for block in sorted(blocks, key=blocks.get) for v in block]
    return [text_digits(i, 2) for i in index], [text_digits(v, width) for v in flat], width


def text_literal(records):
    lines = []
    for i in range(0, len(records), TEXT_RECORDS_PER_LINE):
        lines.append("    " + "".join(records[i:i + TEXT_RECORDS_PER_LINE]))
    return '"""\n' + "\n".join(lines) + '\n    """'


def text_table(name, records, width, comment):
    assert all(len(r) == width for r in records)
    return (
        f"// {comment}\n"
        f"// {len(records)} records of {width} digits.\n"
        f"const {name}Count = {len(records)}\n"
        f"const {name} = {text_literal(records)}\n"
    )


def staged_table(name, values, comment, names=None, prefix=None):
    index, blocks, width = staged(values)
    parts = [
        f"// {comment}\n"
        f"// Two stages: {len(index)} blocks of 128 named in {name}Index, "
        f"{len(blocks) // 128} distinct ones in {name}Blocks, {width} digit(s) a value.\n"
        f"const {name}Width = {width}\n"
        f"const {name}Index = {text_literal(index)}\n"
        f"const {name}Blocks = {text_literal(blocks)}\n"
    ]
    if names:
        parts.append("".join(f"const {prefix}{n.replace('_', '')} = {i}\n" for i, n in enumerate(names)))
    return "".join(parts)


def unicode_data(ucd):
    rows = {}
    for fields in data_lines(os.path.join(ucd, "UnicodeData.txt")):
        rows[int(fields[0], 16)] = fields
    return rows


def decompositions(rows, exclusions):
    canonical, compatible = {}, {}
    for code, fields in rows.items():
        mapping = fields[5]
        if not mapping:
            continue
        if mapping.startswith("<"):
            compatible[code] = [int(c, 16) for c in mapping.split(">", 1)[1].split()]
        else:
            canonical[code] = [int(c, 16) for c in mapping.split()]

    def full(code, compat, seen=()):
        mapping = canonical.get(code) or (compatible.get(code) if compat else None)
        if mapping is None:
            return [code]
        return [s for c in mapping for s in full(c, compat)]

    full_canonical = {c: full(c, False) for c in canonical}
    full_compatible = {c: full(c, True) for c in set(canonical) | set(compatible)}
    compositions = sorted(
        (m[0], m[1], c) for c, m in canonical.items() if len(m) == 2 and c not in exclusions
    )
    return full_canonical, full_compatible, compositions


def decomposition_table(name, mapping, comment):
    records, pool = [], []
    for code in sorted(mapping):
        scalars = mapping[code]
        records.append(digits(code, 5) + text_digits(len(pool), 4) + text_digits(len(scalars), 1))
        pool.extend(scalars)
    return (
        text_table(name, records, 10, comment)
        + text_table(name.replace("Table", "Pool"), [digits(s, 5) for s in pool], 5,
                     f"The scalars {name}'s records point into.")
    )


def text_tables(ucd):
    rows = unicode_data(ucd)
    category = general_categories(ucd)
    pictographic = property_set(ucd, "emoji-data.txt", "Extended_Pictographic")

    grapheme = ranged(ucd, "GraphemeBreakProperty.txt", "Other")
    incb = ranged(ucd, "DerivedCoreProperties.txt", "None", field=2, where="InCB")
    grapheme_values = [
        GRAPHEME_BREAK.index(grapheme[c]) + (16 if c in pictographic else 0) + 32 * INCB.index(incb[c])
        for c in range(0x110000)
    ]

    word = ranged(ucd, "WordBreakProperty.txt", "Other")
    word_values = [WORD_BREAK.index(word[c]) + (32 if c in pictographic else 0) for c in range(0x110000)]

    sentence = ranged(ucd, "SentenceBreakProperty.txt", "Other")
    sentence_values = [SENTENCE_BREAK.index(sentence[c]) for c in range(0x110000)]

    line = ranged(ucd, "LineBreak.txt", "XX")
    width = ranged(ucd, "EastAsianWidth.txt", "N")
    line_values = []
    for c in range(0x110000):
        cls = line[c]
        if cls in ("AI", "SG", "XX"):
            cls = "AL"
        elif cls == "SA":
            cls = "CM" if category[c] in ("Mn", "Mc") else "AL"
        elif cls == "CJ":
            cls = "NS"
        elif cls == "QU" and category[c] in ("Pi", "Pf"):
            cls = "QU_" + category[c]
        value = LINE_BREAK.index(cls)
        if width[c] in ("F", "W", "H"):
            value += 64
        if c in pictographic and category[c] == "Cn":
            value += 128
        line_values.append(value)

    combining = [0] * 0x110000
    for code, fields in rows.items():
        combining[code] = int(fields[3])
    # UnicodeData.txt's ranges (<CJK Ideograph, First> ...) are all class 0.

    exclusions = property_set(ucd, "DerivedNormalizationProps.txt", "Full_Composition_Exclusion")
    full_canonical, full_compatible, compositions = decompositions(rows, exclusions)

    parts = [
        staged_table("graphemeBreak", grapheme_values,
                     "Grapheme_Cluster_Break (GraphemeBreakProperty.txt), + 16 for Extended_Pictographic "
                     "(emoji-data.txt), + 32 x Indic_Conjunct_Break (DerivedCoreProperties.txt).",
                     GRAPHEME_BREAK, "graphemeBreak"),
        "".join(f"const indicConjunct{n} = {i}\n" for i, n in enumerate(INCB)),
        staged_table("wordBreak", word_values,
                     "Word_Break (WordBreakProperty.txt), + 32 for Extended_Pictographic.",
                     WORD_BREAK, "wordBreak"),
        staged_table("sentenceBreak", sentence_values,
                     "Sentence_Break (SentenceBreakProperty.txt).", SENTENCE_BREAK, "sentenceBreak"),
        staged_table("lineBreak", line_values,
                     "Line_Break (LineBreak.txt) as LB1 resolves it, QU split by general category, "
                     "+ 64 for East_Asian_Width F, W or H, + 128 for an unassigned Extended_Pictographic.",
                     LINE_BREAK, "lineBreak"),
        staged_table("combiningClass", combining, "Canonical_Combining_Class (UnicodeData.txt)."),
        decomposition_table("canonicalDecompositionTable", full_canonical,
                            "Full canonical decompositions: (code point, start in the pool, count)."),
        decomposition_table("compatibilityDecompositionTable", full_compatible,
                            "Full compatibility decompositions: (code point, start in the pool, count)."),
        text_table("compositionTable", [digits(a, 5) + digits(b, 5) + digits(c, 5) for a, b, c in compositions],
                   15, "Primary composites: (first, second, composite), sorted by the pair."),
    ]

    header = (
        "module Sable.Text\n\n"
        f"// Generated by tools/unicode-tables.py from the Unicode Character Database\n"
        f"// {UCD_VERSION}; do not edit. To regenerate, or to move to another Unicode\n"
        "// version, see that script: it says where the files come from and how\n"
        "// these tables are laid out. UnicodeLookup.sb reads them.\n\n"
        f"// Records per line of each table: record r starts at byte r * width + r / {TEXT_RECORDS_PER_LINE}.\n"
        f"const textRecordsPerLine = {TEXT_RECORDS_PER_LINE}\n\n"
    )
    with open(TEXT_OUTPUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(header + "\n".join(parts))
    print(f"wrote {TEXT_OUTPUT}: {os.path.getsize(TEXT_OUTPUT)} bytes")


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

    full_folding = {}
    for fields in data_lines(os.path.join(ucd, "CaseFolding.txt")):
        if fields[1] == "F":
            full_folding[int(fields[0], 16)] = [int(c, 16) for c in fields[2].split()]

    for name, mapping, what in [
        ("upperSpecialTable", special_upper, "Uppercase"),
        ("lowerSpecialTable", special_lower, "Lowercase"),
        ("fullFoldingTable", full_folding, "Case folding (status F of CaseFolding.txt)"),
    ]:
        records = []
        for code in sorted(mapping):
            scalars = mapping[code] + [0] * (3 - len(mapping[code]))
            records.append(digits(code, 5) + "".join(digits(s, 5) for s in scalars))
        parts.append(table(name, records, 20, f"{what}: mappings to more than one scalar, unconditional."))

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
    text_tables(ucd)


if __name__ == "__main__":
    main()
