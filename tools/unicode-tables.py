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
- upperCase, lowerCase, foldingCase: the simple (one scalar to one) case
  mappings and case folding (C + S), staged (below): what is added to the
  code point modulo 2^16, a mapping never leaving its plane, plus 2^16 when
  the scalar is also in the special table beside it. <name>Pairs is the same
  for the scalars of two bytes of UTF-8, U+0080 to U+07FF, as the bits of the
  first byte and of the second that the mapping changes, a digit of base 64
  each; 32 and 0 for one that is special, whose mapping is not two bytes, or
  (in lowerCase) that is a capital sigma, which may be a final one.
- upperSpecialTable, lowerSpecialTable: the unconditional mappings to more
  than one scalar from SpecialCasing.txt ('ß' to "SS"). A record is the
  code point (5) and three code points of the mapping (5 each), 0 for none.
  The language-specific and context-specific ones are left out; Final_Sigma,
  the one context every language has, is String.lowercased()'s.
- fullFoldingTable: the case foldings to more than one scalar (status F of
  CaseFolding.txt, 'ß' to "ss"), as the two above: String.caseFolded()'s.

The text tables (Sable.Text):

- graphemeBreak (Grapheme_Cluster_Break, plus 16 for Extended_Pictographic,
  plus 32 times Indic_Conjunct_Break), wordBreak (Word_Break, plus 32 for
  Extended_Pictographic), sentenceBreak (Sentence_Break) and lineBreak
  (Line_Break as rule LB1 resolves it, QU split by its general category, plus
  64 for East_Asian_Width F, W or H, plus 128 for an Extended_Pictographic
  code point not yet assigned), staged. Each value's number is a constant
  written beside them.
- normalization, staged: Canonical_Combining_Class in the low 8 bits; then,
  for a code point with a full canonical decomposition (applied until nothing
  more decomposes; Hangul syllables are computed, not listed), where its
  scalars start in decompositionPool (13 bits) and how many there are (3);
  then a bit (24) for one that NFC changes, and a bit (25) for one that may
  compose with, or be reordered before, what comes before it, and so does
  not begin a piece of text that normalizes alone.
- compatibility, staged: the same for the code points whose compatibility
  decomposition is not their canonical one: where its scalars start (14
  bits), how many (5), and the bit (19) as normalization's 25.
- compositionFirst, staged: for the first scalar of primary composites, where
  its pairs start in compositionPairs (10 bits) and how many there are. A
  pair is the second scalar and the composite, 3 digits of base 64 each, in
  the order of the second; Full_Composition_Exclusion is left out.

A staged table, <name>Index and <name>Blocks, is a value for every code point
in two stages. Its digits are of base 64, '0' (0) to 'o' (63), the lowest
first, so that a value is one load, a mask and shifts. The code point without
its low <name>Shift bits indexes <name>Index, two digits, sixty-four to a
line, which names a block of <name>Blocks, a line of its own; the value is
the block's record for those bits. Blocks alike are kept once, code points
past the last block that is not all zero have none (<name>Limit), and the
blocks end in a line of padding, so that eight bytes can always be read.
"""

import os
import sys

UCD_VERSION = "16.0.0"
RECORDS_PER_LINE = 12
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


def digits64(value, width):
    """The value's digits of base 64, the lowest first."""
    assert 0 <= value < 64 ** width, (value, width)
    return "".join(chr(0x30 + (value >> 6 * i & 63)) for i in range(width))


def lines_literal(lines):
    return '"""\n' + "\n".join("    " + line.replace("\\", "\\\\") for line in lines) + '\n    """'


def staged_table(name, values, comment, shifts=(4, 5, 6, 7, 8)):
    """A table of every code point's value in two stages, in the shape that is smallest."""
    width = next(w for w in range(1, 6) if max(values) < 64 ** w)
    best = None
    for shift in shifts:
        blocks, index = {}, []
        size = 1 << shift
        for start in range(0, 0x110000, size):
            block = tuple(values[start:start + size])
            index.append(blocks.setdefault(block, len(blocks)))
        zero = blocks.get((0,) * size)
        while index and index[-1] == zero:
            index.pop()
        cost = len(index) * 2 + len(blocks) * size * width
        if best is None or cost < best[0]:
            best = cost, shift, index, list(blocks)
    cost, shift, index, blocks = best
    entries = [digits64(i, 2) for i in index]
    index_lines = ["".join(entries[i:i + 64]) for i in range(0, len(entries), 64)]
    block_lines = ["".join(digits64(v, width) for v in block) for block in blocks] + ["0" * 8]
    return (
        f"// {comment}\n"
        f"// {len(index)} blocks of {1 << shift} named in {name}Index, "
        f"{len(blocks)} distinct ones in {name}Blocks, {width} digit(s) a value.\n"
        f"const {name}Shift = {shift}\n"
        f"const {name}Limit = {len(index)}\n"
        f"const {name}Index = {lines_literal(index_lines)}\n"
        f"const {name}Blocks = {lines_literal(block_lines)}\n"
    )


def case_table(name, mapping, special, what):
    values = [0] * 0x110000
    for code, mapped in mapping.items():
        assert code >> 16 == mapped >> 16, code
        values[code] = (mapped - code) & 0xFFFF
    for code in special:
        values[code] |= 0x10000
    pairs = []
    for code in range(0x80, 0x800):
        mapped = mapping.get(code, code)
        if code in special or not 0x80 <= mapped < 0x800 or name == "lowerCase" and code == 0x3A3:
            pairs.append(digits64(0x20, 1) + "0")
        else:
            pairs.append(digits64((code ^ mapped) >> 6, 1) + digits64((code ^ mapped) & 63, 1))
    # One shape for the three, so that one loop reads whichever it is given.
    return staged_table(name, values, f"{what}: what is added, modulo 2^16, + 2^16 for a special one.", shifts=(6,)) + (
        "// The same for U+0080 to U+07FF, as the bits of the two bytes that change.\n"
        f"const {name}Pairs = {lines_literal([''.join(pairs) + '0' * 8])}\n"
    )


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


def named(prefix, names):
    return "".join(f"const {prefix}{n.replace('_', '')} = {i}\n" for i, n in enumerate(names))


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


HANGUL_VOWELS = range(0x1161, 0x1176)
HANGUL_TRAILING = range(0x11A8, 0x11C3)


def recomposed(scalars, combining, pairs):
    """Canonical composition of ordered scalars (Unicode 3.11, D117)."""
    kept, starter = [], -1
    for scalar in scalars:
        if starter >= 0:
            last = -1 if len(kept) - 1 == starter else combining[kept[-1]]
            if last == -1 or last != 0 and last < combining[scalar]:
                composite = pairs.get((kept[starter], scalar))
                if composite is not None:
                    kept[starter] = composite
                    continue
        if combining[scalar] == 0:
            starter = len(kept)
        kept.append(scalar)
    return kept


def normalization_tables(combining, canonical, compatible, compositions):
    pairs = {(a, b): c for a, b, c in compositions}
    seconds = {b for _, b, _ in compositions} | set(HANGUL_VOWELS) | set(HANGUL_TRAILING)
    joins = lambda c: combining[c] != 0 or c in seconds
    pool, values = [], list(combining)
    for code, scalars in canonical.items():
        values[code] |= len(pool) << 8 | len(scalars) << 21
        assert len(pool) < 1 << 13 and len(scalars) < 8
        pool.extend(scalars)
        if recomposed(scalars, combining, pairs) != [code]:
            values[code] |= 1 << 24
    for code in range(0x110000):
        if joins(code) or code in canonical and joins(canonical[code][0]):
            values[code] |= 1 << 25
    compat = [0] * 0x110000
    for code, scalars in compatible.items():
        if scalars != canonical.get(code):
            assert len(pool) < 1 << 14 and len(scalars) < 32
            compat[code] = len(pool) | len(scalars) << 14 | int(joins(scalars[0])) << 19
            pool.extend(scalars)
    firsts, records = [0] * 0x110000, []
    for a, b, c in compositions:
        if firsts[a] == 0:
            firsts[a] = len(records)
        firsts[a] += 1 << 10
        records.append(digits64(b, 3) + digits64(c, 3))
    assert len(records) < 1 << 10
    per_line = 16
    lines = lambda rs: ["".join(rs[i:i + per_line]) for i in range(0, len(rs), per_line)] + ["0" * 8]
    return [
        staged_table("normalization", values,
                     "Canonical_Combining_Class and full canonical decompositions, as the script describes."),
        staged_table("compatibility", compat,
                     "Full compatibility decompositions where they are not the canonical ones, as the script describes."),
        f"// The scalars decompositions point into, {per_line} to a line, 3 digits of base 64 each.\n"
        f"const decompositionPool = {lines_literal(lines([digits64(s, 3) for s in pool]))}\n",
        staged_table("compositionFirst", firsts, "Where a first scalar's composites are, + 2^10 times how many."),
        f"// Primary composites: (second, composite), {per_line} to a line.\n"
        f"const compositionPairs = {lines_literal(lines(records))}\n",
    ]


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
                     "(emoji-data.txt), + 32 x Indic_Conjunct_Break (DerivedCoreProperties.txt)."),
        named("graphemeBreak", GRAPHEME_BREAK),
        "".join(f"const indicConjunct{n} = {i}\n" for i, n in enumerate(INCB)),
        staged_table("wordBreak", word_values,
                     "Word_Break (WordBreakProperty.txt), + 32 for Extended_Pictographic."),
        named("wordBreak", WORD_BREAK),
        staged_table("sentenceBreak", sentence_values,
                     "Sentence_Break (SentenceBreakProperty.txt)."),
        named("sentenceBreak", SENTENCE_BREAK),
        staged_table("lineBreak", line_values,
                     "Line_Break (LineBreak.txt) as LB1 resolves it, QU split by general category, "
                     "+ 64 for East_Asian_Width F, W or H, + 128 for an unassigned Extended_Pictographic."),
        named("lineBreak", LINE_BREAK),
        *normalization_tables(combining, full_canonical, full_compatible, compositions),
    ]

    header = (
        "module Sable.Text\n\n"
        f"// Generated by tools/unicode-tables.py from the Unicode Character Database\n"
        f"// {UCD_VERSION}; do not edit. To regenerate, or to move to another Unicode\n"
        "// version, see that script: it says where the files come from and how\n"
        "// these tables are laid out. UnicodeLookup.sb reads them.\n\n"
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

    full_folding = {}
    for fields in data_lines(os.path.join(ucd, "CaseFolding.txt")):
        if fields[1] == "F":
            full_folding[int(fields[0], 16)] = [int(c, 16) for c in fields[2].split()]

    for name, mapping, special, what in [
        ("upperCase", upper, special_upper, "Simple_Uppercase_Mapping (UnicodeData.txt)"),
        ("lowerCase", lower, special_lower, "Simple_Lowercase_Mapping (UnicodeData.txt)"),
        ("foldingCase", fold, full_folding, "Simple case folding, statuses C and S (CaseFolding.txt)"),
    ]:
        parts.append(case_table(name, mapping, special, what))

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
