#!/usr/bin/env python3
"""Writes Sable.Globalization's tables from CLDR, as ICU carries it.

    python3 -I tools/locale-tables.py [--icu <prefix>] [--all]

The data is the Unicode CLDR's, read through the C API of an ICU that is
already on the machine: an unmodified ICU4C with its data, as Homebrew's and
Linux distributions' are. macOS's own libicucore is not used, as Apple changes
its data. <prefix> is where ICU is (default: the newest of Homebrew's
icu4c); the library's version suffix is worked out. Nothing is fetched and
nothing is built, and the compiler never runs this: the tables it writes are
committed. docs/09-globalization.md says why the data is tables rather than
the system's, and what is in them.

--all takes the snapshot of every locale ICU has rather than LOCALES, and
every collation small enough (MAX_TAILORING). To add a locale, add its tag
to LOCALES (and to COLLATIONS when it has a tailoring), run this, and run
the Sable.Globalization tests. tests/lib/Sable.Globalization/data/ is written by
tools/locale-tests.mjs, from Node's own copy of ICU.

It writes lib/Sable.Globalization/LocaleTables.sb:

- localeRecord: a record of text per locale, a line 'key=value' per field,
  '|' between a value's items. The fields are listed in locale_record below.
- localeParents: 'tag=ancestor', where a locale's data comes from when it
  has no record and cutting its subtags off from the right does not find it
  (en-ZW's is en-001's).
- currencyDigits, currencySymbols, numberingSystems.

and lib/Sable.Globalization/CollationTables.sb: the CLDR root collation and the
tailorings. ICU's 32-bit fractional weights, root's and every tailoring's,
are gathered and replaced with their rank among them all, so a tailored
weight falls between the root's that it is between in CLDR. Numbers are in
base 32 with the digits '0' to 'O', as tools/unicode-tables.py writes them
and says why; records are RECORDS_PER_LINE to a line. Characters newer than
the Unicode version of Sable.Text's tables (UNICODE_AGE) are left out, and
sort as unassigned.

- A collation element is 8 digits: the primary (4), the secondary (2), and
  the tertiary (2), which is tertiary rank * 4 + case (0 lower, 1 mixed, 2
  upper).
- collationRuns: a record is the first code point (4), how many (3), and for
  characters of one element whose primaries follow one another, that of the
  first (8); for a character of other than one element, its first element in
  collationPool (4), how many (2) and "OO". Sorted by code point.
  collationIndex gives, by code point >> 8, the first run that ends at or
  after that block (3).
- collationContractions: up to three code points (4 each), the first element
  in the pool (4) and how many (2); sorted.
- collationCommon: for each code point below COMMON, in order, its first
  element (8), how many more it has (1, or 'O' where it is not to be taken
  alone: a mark, or one the tables do not have), where they are in the pool
  (4), and 1 if a contraction may begin in it and go on past it, plus 2 if
  one may go on into it (1). collationCommonDecomposed has the same code
  points' canonical decompositions, three code points (4 each) or zeros.
- collationTailoring: for a collation, a line per string: the string's
  length (1), its code points (4 each), its elements (8 each). Then
  collationReorder: the runs of primaries (first, after the last, 4 each) in
  their order, where a locale puts its own script first. collationSettings:
  'shifted', 'backwards', 'upper'.

What is left out: Han orders other than the root's (pinyin, stroke, JIS,
Hanja), whose tailorings are tens of thousands of characters; search
collations; calendars other than the Gregorian; time zone names; the names of
languages, regions and currencies.
"""

import ctypes as C
import glob
import os
import re
import sys
import unicodedata

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
RECORDS_PER_LINE = 48
UNICODE_AGE = 16
MAX_TAILORING = 2000
COMMON = 0x530

LOCALES = """
    en en-001 en-AU en-CA en-GB en-IE en-IN en-NZ en-ZA
    de de-AT de-CH fr fr-CA fr-CH es es-419 es-MX it it-CH pt pt-PT nl ca
    sv da nb fi is pl cs sk sl hr sr hu ro bg ru uk el tr lt lv et cy ga
    ar ar-EG fa he hi bn mr ta th vi id ko ja zh zh-Hant
""".split()

# Collation locales, and the keyword collations beside the standard ones.
COLLATIONS = """
    sv da nb de-u-co-phonebk es es-u-co-trad fr-CA pl cs sk sl hr sr hu ro bg ru uk
    el tr lt lv et fi is cy mt ar fa he hi bn mr ta th vi
""".split()

FULL = "--all" in sys.argv


def icu_prefix():
    if "--icu" in sys.argv:
        return sys.argv[sys.argv.index("--icu") + 1]
    found = sorted(glob.glob("/opt/homebrew/opt/icu4c@*"), key=lambda p: int(p.rsplit("@", 1)[1]))
    if not found:
        sys.exit("no ICU found; pass --icu <prefix>")
    return found[-1]


PREFIX = icu_prefix()


def load(name):
    paths = sorted(p for e in ("dylib", "so") for p in glob.glob(f"{PREFIX}/lib/{name}*.{e}*"))
    return C.CDLL(paths[0])


load("libicudata")
UC, I18N = load("libicuuc"), load("libicui18n")
VERSION = next(m.group(1) for p in glob.glob(f"{PREFIX}/lib/libicuuc*")
               if (m := re.search(r"libicuuc\.(?:so\.)?(\d+)", p)))
VP, INT, STR = C.c_void_p, C.c_int32, C.c_char_p
error = C.c_int(0)
ERR = C.byref(error)


def icu(name, result, *args, lib=None):
    f = getattr(lib or UC, f"{name}_{VERSION}", None) or getattr(I18N, f"{name}_{VERSION}")
    f.restype = result
    f.argtypes = args
    return f


def check(what):
    code, error.value = error.value, 0
    if code > 0:
        raise RuntimeError(f"{what}: ICU error {code}")


def u16(text):
    b = text.encode("utf-16-le", "surrogatepass")
    return b, len(b) // 2


def from_u16(buffer, n):
    return bytes(buffer)[:n * 2].decode("utf-16-le", "surrogatepass")


def text_of(call, what="text"):
    """The text a 'fill this UChar buffer' function gives."""
    buffer = (C.c_uint16 * 4096)()
    n = call(buffer, 4096, ERR)
    check(what)
    return from_u16(buffer, n)


def icu_locale(tag):
    return tag.replace("-u-co-phonebk", "@collation=phonebook").replace("-u-co-trad", "@collation=traditional").replace("-", "_")


# ---- Resource bundles -------------------------------------------------------

ures_open = icu("ures_open", VP, STR, STR, VP)
ures_openDirect = icu("ures_openDirect", VP, STR, STR, VP)
ures_getByKey = icu("ures_getByKey", VP, VP, STR, VP, VP)
ures_getByIndex = icu("ures_getByIndex", VP, VP, INT, VP, VP)
ures_getSize = icu("ures_getSize", INT, VP)
ures_getKey = icu("ures_getKey", STR, VP)
ures_getString = icu("ures_getString", VP, VP, VP, VP)
ures_getType = icu("ures_getType", INT, VP)
ures_getInt = icu("ures_getInt", INT, VP, VP)
ures_getLocale = icu("ures_getLocaleByType", STR, VP, INT, VP)


def resource_string(bundle):
    n = INT(0)
    p = ures_getString(bundle, C.byref(n), ERR)
    check("string")
    return C.string_at(p, n.value * 2).decode("utf-16-le")


def direct(locale, *path, tree=None):
    """The resource at the path in the locale's own bundle, or None."""
    bundle = ures_openDirect(tree, locale.encode(), ERR)
    if error.value > 0:
        error.value = 0
        return None
    for key in path:
        bundle = ures_getByKey(bundle, key.encode(), None, ERR)
        if error.value > 0:
            error.value = 0
            return None
    return bundle


def parent_of(locale):
    if locale == "root":
        return None
    explicit = direct(locale, "%%Parent")
    if explicit:
        return resource_string(explicit)
    return locale.rsplit("_", 1)[0] if "_" in locale else "root"


def inherited(locale, *path, tree=None):
    while locale:
        found = direct(locale, *path, tree=tree)
        if found:
            return found
        locale = parent_of(locale)
    return None


def canonical(tag):
    """The name of the bundle ICU uses for the locale: an alias resolved."""
    bundle = ures_open(None, icu_locale(tag).encode(), ERR)
    check("open " + tag)
    name = ures_getLocale(bundle, 1, ERR).decode()
    check("locale")
    return name


# ---- A locale's record ------------------------------------------------------

unum_open = icu("unum_open", VP, INT, VP, INT, STR, VP, VP)
unum_toPattern = icu("unum_toPattern", INT, VP, C.c_byte, VP, INT, VP)
unum_getSymbol = icu("unum_getSymbol", INT, VP, INT, VP, INT, VP)
udat_open = icu("udat_open", VP, INT, INT, STR, VP, INT, VP, INT, VP)
udat_toPattern = icu("udat_toPattern", INT, VP, C.c_byte, VP, INT, VP)
udat_format = icu("udat_format", INT, VP, C.c_double, VP, INT, VP, VP)
udat_getSymbols = icu("udat_getSymbols", INT, VP, INT, INT, VP, INT, VP)
udat_countSymbols = icu("udat_countSymbols", INT, VP, INT)
ucal_open = icu("ucal_open", VP, VP, INT, STR, INT, VP)
ucal_getAttribute = icu("ucal_getAttribute", INT, VP, INT)
unumsys_open = icu("unumsys_open", VP, STR, VP)
unumsys_openByName = icu("unumsys_openByName", VP, STR, VP)
unumsys_getName = icu("unumsys_getName", STR, VP)
unumsys_getDescription = icu("unumsys_getDescription", INT, VP, VP, INT, VP)
ulistfmt_openForType = icu("ulistfmt_openForType", VP, STR, INT, INT, VP)
ulistfmt_format = icu("ulistfmt_format", INT, VP, VP, VP, INT, VP, INT, VP)
ucurr_getName = icu("ucurr_getName", VP, VP, STR, INT, VP, VP, VP)
ucurr_getDigits = icu("ucurr_getDefaultFractionDigits", INT, VP, VP)
ucurr_openISO = icu("ucurr_openISOCurrencies", VP, C.c_uint32, VP)
uenum_next = icu("uenum_next", STR, VP, VP, VP)
uloc_addLikely = icu("uloc_addLikelySubtags", INT, STR, VP, INT, VP)
uloc_getAvailable = icu("uloc_getAvailable", STR, INT)
uloc_count = icu("uloc_countAvailable", INT)


def likely(locale):
    buffer = C.create_string_buffer(100)
    uloc_addLikely(locale.encode(), buffer, 100, ERR)
    check("likely")
    return buffer.value.decode()


def numbering_of(locale, kind=None):
    name = locale + (f"@numbers={kind}" if kind else "")
    ns = unumsys_open(name.encode(), ERR)
    if error.value > 0:
        error.value = 0
        return "latn"
    return unumsys_getName(ns).decode()


def number_fields(locale, ns):
    name = f"{locale}@numbers={ns}".encode()

    def pattern(style):
        f = unum_open(style, None, 0, name, None, ERR)
        check("unum_open")
        return f, text_of(lambda b, n, e: unum_toPattern(f, 0, b, n, e))

    decimal, decimal_pattern = pattern(1)
    # unum_getSymbol: 0 decimal, 1 group, 3 percent, 6 minus, 7 plus, 10 the
    # currency's decimal, 11 exponent, 12 permille, 14 infinity, 15 NaN, 17
    # the currency's group.
    symbols = [text_of(lambda b, n, e, s=s: unum_getSymbol(decimal, s, b, n, e))
               for s in (0, 1, 6, 7, 3, 12, 11, 15, 14, 10, 17)]
    # The currency format's own symbols, where a locale's money differs.
    patterns = [decimal_pattern] + [pattern(s)[1] for s in (3, 4, 2, 12)]
    # Hindi's and Marathi's are in brackets, which nothing writes.
    patterns[2] = patterns[2].strip("[]")
    return symbols, patterns


def date_symbols(locale):
    f = udat_open(2, 2, (locale + "@calendar=gregorian;numbers=latn").encode(), None, 0, None, 0, ERR)
    check("udat_open")

    def get(kind, first):
        n = udat_countSymbols(f, kind)
        return [text_of(lambda b, m, e, i=i: udat_getSymbols(f, kind, i, b, m, e)) for i in range(first, n)]

    # UDateFormatSymbolType: 0 eras, 1 months, 2 short months, 3 weekdays, 4
    # short weekdays, 5 am/pm, 10 standalone months, 11 short, 12 narrow, 13
    # standalone weekdays, 14 short, 15 narrow.
    days = lambda kind: (lambda d: d[1:] + d[:1])(get(kind, 1))
    return {
        "era": get(0, 0), "months": get(1, 0)[:12], "months.short": get(2, 0)[:12],
        "months.alone": get(10, 0)[:12], "months.alone.short": get(11, 0)[:12], "months.narrow": get(12, 0)[:12],
        "days": days(3), "days.short": days(4), "days.alone": days(13), "days.alone.short": days(14), "days.narrow": days(15),
        "ampm": get(5, 0),
    }


def date_patterns(locale):
    name = (locale + "@calendar=gregorian").encode()

    def pattern(time, date):
        f = udat_open(time, date, name, None, 0, None, 0, ERR)
        check("udat_open")
        return text_of(lambda b, n, e: udat_toPattern(f, 0, b, n, e))

    dates = [pattern(-1, s) for s in range(4)]
    times = [pattern(s, -1) for s in range(4)]
    glue = []
    for s in range(4):
        both = pattern(3, s)
        assert dates[s] in both, (locale, both)
        both = both.replace(dates[s], "{1}", 1)
        assert times[3] in both, (locale, both)
        glue.append(both.replace(times[3], "{0}", 1))
    return dates, times, glue


def day_parts(locale):
    """What each hour's part of the day is called ('B')."""
    pattern, n = u16("B")
    f = udat_open(-2, -2, (locale + "@calendar=gregorian").encode(), u16("UTC")[0], 3, pattern, n, ERR)
    check("udat_open")
    return [text_of(lambda b, n, e, h=h: udat_format(f, h * 3600000.0, b, n, None, e)) for h in range(24)]


def list_patterns(locale):
    fields = {}
    for kind, kind_name in enumerate(("and", "or", "unit")):
        for width, width_name in enumerate(("wide", "short", "narrow")):
            f = ulistfmt_openForType(locale.encode(), kind, width, ERR)
            check("ulistfmt")

            def joined(items):
                buffers = [C.create_string_buffer(u16(i)[0] + b"\0\0") for i in items]
                strings = (VP * len(items))(*[C.cast(b, VP) for b in buffers])
                lengths = (INT * len(items))(*[len(i) for i in items])
                return text_of(lambda b, n, e: ulistfmt_format(f, strings, lengths, len(items), b, n, e))

            two = joined(["{0}", "{1}"])
            four = joined(["", "", "", ""])
            m = re.fullmatch("(.*)(.*)(.*)(.*)(.*)", four)
            # ICU has put Hebrew's hyphen before what is not Hebrew; the
            # library does so itself.
            two = two.replace("\u05D5-{1}", "\u05D5{1}")
            assert m and not m.group(1), (locale, four)
            start, middle, end, after = m.group(2), m.group(3), m.group(4).replace("\u05D5-", "\u05D5"), m.group(5)
            fields[f"list.{kind_name}.{width_name}"] = [two, "{0}" + start + "{1}", "{0}" + middle + "{1}", "{0}" + end + "{1}" + after]
    return fields


def plural_rules(language, kind):
    sets = direct("plurals", "locales" if kind == "c" else "locales_ordinals", language)
    if not sets:
        return ""
    name = resource_string(sets)
    if not name:
        return ""
    table = direct("plurals", "rules", name)
    rules = []
    for i in range(ures_getSize(table)):
        rule = ures_getByIndex(table, i, None, ERR)
        check("rule")
        key = ures_getKey(rule).decode()
        text = resource_string(rule).split("@")[0].strip()
        if key != "other":
            rules.append(f"{key}:{text}")
    order = ["zero", "one", "two", "few", "many"]
    rules.sort(key=lambda r: order.index(r.split(":")[0]))
    return ";".join(rules)


def currencies(kind):
    e = ucurr_openISO(kind, ERR)
    check("currencies")
    found = []
    while True:
        code = uenum_next(e, None, ERR)
        if not code:
            break
        found.append(code.decode())
    return sorted(set(found))


def currency_name(code, locale, style):
    b, _ = u16(code + "\0")
    n = INT(0)
    p = ucurr_getName(b, locale.encode(), style, None, C.byref(n), ERR)
    check("currency")
    return C.string_at(p, n.value * 2).decode("utf-16-le")


CURRENCIES = currencies(1 | 8)  # UCURR_COMMON | UCURR_NON_DEPRECATED
SYMBOLS_ROOT = {c: currency_name(c, "root", 0) for c in CURRENCIES}
NARROW_ROOT = {c: currency_name(c, "root", 2) for c in CURRENCIES}


def locale_record(tag):
    locale = canonical(tag)
    default = numbering_of(locale)
    native = numbering_of(locale, "native")
    record = {"tag": tag, "likely": likely(locale).replace("_", "-"), "numbers": [default, native]}
    for ns in dict.fromkeys([default, native, "latn"]):
        symbols, patterns = number_fields(locale, ns)
        record[f"symbols.{ns}"], record[f"patterns.{ns}"] = symbols, patterns
    grouping = inherited(locale, "NumberElements", "minimumGroupingDigits")
    record["minimumGrouping"] = resource_string(grouping) if grouping else "1"
    symbols = [f"{c}={s}" for c in CURRENCIES if (s := currency_name(c, locale, 0)) != SYMBOLS_ROOT[c]]
    narrow = [f"{c}={s}" for c in CURRENCIES if (s := currency_name(c, locale, 2)) != NARROW_ROOT[c]]
    record["currencies"], record["currencies.narrow"] = symbols, narrow
    record.update(date_symbols(locale))
    record["dates"], record["times"], record["joins"] = date_patterns(locale)
    if any("B" in t for t in record["times"]):
        record["parts"] = day_parts(locale)
    calendar = ucal_open(None, 0, locale.encode(), 1, ERR)
    check("calendar")
    first = ucal_getAttribute(calendar, 1)  # 1 is Sunday
    record["week"] = [str((first + 5) % 7 + 1), str(ucal_getAttribute(calendar, 2))]
    zone_tree = f"icudt{VERSION}l-zone".encode()

    def zone(key):
        found = inherited(locale, "zoneStrings", key, tree=zone_tree)
        # CLDR 49 derives the zero format from the other where it has none.
        return resource_string(found) if found else zone("gmtFormat").replace("{0}", "").strip()

    record["gmt"] = [zone("gmtFormat"), zone("gmtZeroFormat"), zone("hourFormat")]
    record.update(list_patterns(locale))
    language = locale.split("_")[0]
    record["plurals"] = plural_rules(language if tag != "pt-PT" else "pt_PT", "c")
    record["ordinals"] = plural_rules(language, "o")
    return record


# ---- Writing -----------------------------------------------------------------

def literal(text):
    out = []
    for c in text:
        if c == "\\":
            out.append("\\\\")
        elif c == '"':
            out.append('\\"')
        elif c != " " and c != "\n" and (not c.isprintable() or 0x300 <= ord(c) < 0x370):
            out.append(f"\\u{{{ord(c):X}}}")
        else:
            out.append(c)
    return "".join(out)


def matched(cases):
    """A 'match' of tags, its arrows aligned as 'sable fmt' aligns them."""
    width = max(len(tag) for tag, _ in cases) + 2
    arms = [f'        case {chr(34) + tag + chr(34):<{width}} => {value}\n' for tag, value in cases]
    return "    return match tag {\n" + "".join(arms) + f'        case {"_":<{width}} => none\n    }}\n}}\n\n'


def block(name, lines, prefix="const "):
    body = "\n".join("    " + literal(l) if l else "" for l in lines)
    return f'{prefix}{name} = """\n{body}\n    """\n'


def notice():
    text = open(f"{PREFIX}/LICENSE", encoding="utf-8").read().split("-----")[0]
    return "".join(("// " + l).rstrip() + "\n" for l in text.strip().split("\n"))


def header(what):
    return (f"module Sable.Globalization\n\n// Generated by tools/locale-tables.py from {what}; do not\n"
            f"// edit. The script says how these tables are laid out, and how to regenerate\n"
            f"// them. The data is under the Unicode License, whose notice goes with it:\n//\n{notice()}\n")


def cldr_version():
    v = (C.c_uint8 * 4)()
    icu("ulocdata_getCLDRVersion", None, VP, VP)(v, ERR)
    check("cldr")
    return f"CLDR {v[0]}" + (f".{v[1]}" if v[1] else "")


def available():
    return [uloc_getAvailable(i).decode() for i in range(uloc_count())]


def parents(tags):
    """Where each locale without a record inherits from, when that is not
    what cutting subtags off gives."""
    known = {t.replace("-", "_") for t in tags}
    canonical_known = {canonical(t): t for t in tags}
    languages = {t.split("-")[0] for t in tags}
    found = {}
    # ICU's locales, and for each language the regions it has a script for.
    names = set(available())
    for name in list(names):
        parts = name.split("_")
        if len(parts) == 3 and len(parts[1]) == 4:
            names.add(parts[0] + "_" + parts[2])
    for name in sorted(names):
        if name.split("_")[0] not in languages:
            continue
        full = name
        parts = name.split("_")
        if len(parts) == 2 and len(parts[1]) == 2:
            script = likely(name).split("_")[1]
            if script != likely(parts[0]).split("_")[1]:
                full = f"{parts[0]}_{script}_{parts[1]}"
        at = full
        while at and at not in canonical_known and at not in known:
            at = parent_of(at)
            if at == "root":
                at = None
        cut = name
        while cut and cut not in known:
            cut = cut.rsplit("_", 1)[0] if "_" in cut else None
        if at != cut:
            found[name] = canonical_known.get(at, at) if at else "und"
    return [f"{k.replace('_', '-')}={v.replace('_', '-')}" for k, v in sorted(found.items())]


def write_locales():
    tags = LOCALES
    if FULL:
        tags = [n.replace("_", "-") for n in available() if n != "en_US_POSIX"]
    out = [header(f"{cldr_version()} (ICU {VERSION})")]
    out.append(f'/// The version of the Unicode CLDR the locales\' data is from.\npublic const cldrVersion = "{cldr_version().split()[1]}"\n\n')
    out.append("/// The locales there is a record for.\n")
    out.append(block("localeTags", [" ".join(tags[i:i + 12]) for i in range(0, len(tags), 12)]) + "\n")
    out.append("// Where a locale with no record of its own inherits from.\n")
    out.append(block("localeParents", parents(tags)) + "\n")
    out.append("// Currencies with other than two decimals.\n")
    digits = [f"{c}={d}" for c in currencies(0x7FFFFFFF) if (d := ucurr_getDigits(u16(c + '\0')[0], ERR)) != 2]
    out.append(block("currencyDigits", digits) + "\n")
    out.append("// Currencies' symbols where a locale has none of its own; then the narrow ones.\n")
    out.append(block("currencySymbols", [f"{c}={s}" for c, s in SYMBOLS_ROOT.items() if s != c]) + "\n")
    out.append(block("currencyNarrowSymbols", [f"{c}={s}" for c, s in NARROW_ROOT.items() if s != SYMBOLS_ROOT[c]]) + "\n")
    out.append("// The digits 0 to 9 of each numbering system.\n")
    systems = []
    for ns in """latn adlm ahom arab arabext beng cakm cham deva fullwide gujr guru hanidec khmr knda lana
                 laoo lepc limb mlym mong mtei mymr newa nkoo olck orya saur sinh sund talu tamldec telu thai tibt vaii""".split():
        n = unumsys_openByName(ns.encode(), ERR)
        check(ns)
        systems.append(ns + "=" + text_of(lambda b, m, e: unumsys_getDescription(n, b, m, e)))
    out.append(block("numberingSystems", systems) + "\n")
    out.append("/// The record of a locale that has one.\nfn localeRecord(_ tag: String) -> String? {\n")
    records, cases = [], []
    for tag in tags:
        name = "record" + "".join(p.capitalize() for p in tag.split("-"))
        record = locale_record(tag)
        lines = []
        for key, value in record.items():
            items = [value] if isinstance(value, str) else value
            assert not any("|" in i or "\n" in i for i in items), (tag, key, items)
            lines.append(f"{key}=" + "|".join(items))
        records.append(block(name, lines))
        cases.append((tag, name))
    out.append(matched(cases) + "\n".join(records))
    path = os.path.join(ROOT, "lib/Sable.Globalization/LocaleTables.sb")
    open(path, "w", encoding="utf-8").write("".join(out))
    print(path, os.path.getsize(path))


# ---- Collation ---------------------------------------------------------------

ucol_open = icu("ucol_open", VP, STR, VP)
ucol_openElements = icu("ucol_openElements", VP, VP, STR, INT, VP)
ucol_next = icu("ucol_next", INT, VP, VP)
ucol_closeElements = icu("ucol_closeElements", None, VP)
ucol_getTailoredSet = icu("ucol_getTailoredSet", VP, VP, VP)
ucol_getContractions = icu("ucol_getContractionsAndExpansions", None, VP, VP, VP, C.c_byte, VP)
ucol_getReorderCodes = icu("ucol_getReorderCodes", INT, VP, VP, INT, VP)
ucol_setReorderCodes = icu("ucol_setReorderCodes", None, VP, VP, INT, VP)
ucol_getAttribute = icu("ucol_getAttribute", INT, VP, INT, VP)
ucol_getSortKey = icu("ucol_getSortKey", INT, VP, STR, INT, VP, INT)
ucol_getVariableTop = icu("ucol_getVariableTop", C.c_uint32, VP, VP)
uset_openEmpty = icu("uset_openEmpty", VP)
uset_getItemCount = icu("uset_getItemCount", INT, VP)
uset_getItem = icu("uset_getItem", INT, VP, INT, VP, VP, VP, INT, VP)
u_charAge = icu("u_charAge", None, INT, VP)
unorm2_getNFD = icu("unorm2_getNFDInstance", VP, VP)
unorm2_normalize = icu("unorm2_normalize", INT, VP, STR, INT, VP, INT, VP)
NFD = unorm2_getNFD(ERR)


def nfd(text):
    b, n = u16(text)
    return text_of(lambda o, m, e: unorm2_normalize(NFD, b, n, o, m, e))


def base32(value, width):
    out = ""
    for _ in range(width):
        out = chr(48 + value % 32) + out
        value //= 32
    assert value == 0
    return out


def age(cp):
    a = (C.c_uint8 * 4)()
    u_charAge(cp, a)
    return a[0]


def elements(collator, text):
    """The text's collation elements: (primary, secondary, tertiary, case)."""
    b, n = u16(text)
    it = ucol_openElements(collator, b, n, ERR)
    check("elements")
    out = []
    while True:
        order = ucol_next(it, ERR)
        if order == -1:
            break
        order &= 0xFFFFFFFF
        p, s, t = order >> 16, order >> 8 & 0xFF, order & 0xFF
        if t & 0xC0 == 0xC0 and out:  # the second half of a long element
            q, r, u, case = out[-1]
            out[-1] = (q | p, r | s, u | t & 0x3F, case)
        else:
            out.append((p << 16, s << 8, (t & 0x3F) << 8, t >> 6))
    ucol_closeElements(it)
    check("elements")
    return [e for e in out if e[:3] != (0, 0, 0)]


def set_strings(uset):
    """Each of the set's strings and characters."""
    out = []
    for i in range(uset_getItemCount(uset)):
        start, end = INT(0), INT(0)
        buffer = (C.c_uint16 * 100)()
        n = uset_getItem(uset, i, C.byref(start), C.byref(end), buffer, 100, ERR)
        check("set")
        if n >= 2:
            out.append(from_u16(buffer, n))
        else:
            out.extend(chr(c) for c in range(start.value, end.value + 1))
    return out


def is_han(cp):
    return (0x4E00 <= cp <= 0x9FFF or 0x3400 <= cp <= 0x4DBF or 0x20000 <= cp <= 0x2A6DF or 0x2A700 <= cp <= 0x2EE5D
            or 0x30000 <= cp <= 0x323AF or cp in (0xFA0E, 0xFA0F, 0xFA11, 0xFA13, 0xFA14, 0xFA1F, 0xFA21, 0xFA23, 0xFA24, 0xFA27, 0xFA28, 0xFA29))


def han_rank(cp):
    """The primary Sable.Globalization gives an ideograph: UTS #10's implicit
    order, which needs no table (ICU4X's 'implicithan'; ICU's own root orders
    them by radical and stroke, from a table as large as all the rest): the
    Unified Ideographs block, the twelve compatibility characters that are
    unified, then the extensions."""
    if 0x4E00 <= cp <= 0x9FFF:
        return 0x10000 + cp - 0x4E00
    if 0xFA0E <= cp <= 0xFA29:
        return 0x15200 + cp - 0xFA0E
    if cp < 0x4E00:
        return 0x15300 + cp - 0x3400
    return 0x17000 + cp - 0x20000


def write_collation():
    def opened(name):
        c = ucol_open(name.encode(), ERR)
        check(name)
        return c

    root = opened("root")
    plain = {}
    for cp in range(0x110000):
        if 0xD800 <= cp < 0xE000 or 0xAC00 <= cp < 0xD7A4 or 0xE000 <= cp < 0xF900 or cp >= 0xE1000 or is_han(cp):
            continue
        if cp & 0xFFFE == 0xFFFE or cp == 0xFFFD or 0xFDD0 <= cp < 0xFDF0 or not 1 <= age(cp) <= UNICODE_AGE:
            continue
        if nfd(chr(cp)) == chr(cp):
            es = elements(root, chr(cp))
            if len(es) == 1 and es[0][0] >= 0xFE000000:
                print("sorts as unassigned:", hex(cp))
                continue
            plain[cp] = es
    contractions = uset_openEmpty()
    ucol_getContractions(root, contractions, None, 1, ERR)
    check("contractions")
    contracted = {}
    usable = lambda s: all(not 0xFDD0 <= ord(c) < 0xFDF0 and 1 <= age(ord(c)) <= UNICODE_AGE for c in s)
    for s in set_strings(contractions):
        d = nfd(s)
        if len(d) > 1 and usable(d):
            contracted[d] = elements(root, s)
    tailored, settings, reorders = {}, {}, {}
    names = COLLATIONS
    if FULL:
        names = [n for n in available() if "_" not in n] + ["de-u-co-phonebk", "es-u-co-trad", "fr-CA"]
    for tag in names:
        collator = opened(icu_locale(tag))
        strings = set_strings(ucol_getTailoredSet(collator, ERR))
        check("tailored")
        if len(strings) > MAX_TAILORING:
            continue
        entries = {}
        for s in strings:
            d = nfd(s)
            if usable(d) and len(d) < 16:
                entries[d] = elements(collator, s)
        # Contractions of the root's it has not, or has as another.
        for d, es in contracted.items():
            if d not in entries and (own := elements(collator, d)) != es:
                entries[d] = own
        flags = []
        if ucol_getAttribute(collator, 1, ERR) == 20:
            flags.append("shifted")
        if ucol_getAttribute(collator, 0, ERR) == 17:
            flags.append("backwards")
        if ucol_getAttribute(collator, 2, ERR) == 25:
            flags.append("upper")
        codes = (INT * 60)()
        reordered = ucol_getReorderCodes(collator, codes, 60, ERR)
        check("attributes")
        if entries or flags or reordered:
            tailored[tag], settings[tag] = entries, flags
            if reordered:
                # The root with its scripts in that order, untailored.
                reorders[tag] = opened("root")
                ucol_setReorderCodes(reorders[tag], codes, reordered, ERR)
                check("reorder")
    every = [e for t in [plain, contracted, *tailored.values()] for es in t.values() for e in es]
    # Primaries begin at 2: 0 is none and 1 is U+FFFE's.
    primary = {p: i + 2 for i, p in enumerate(sorted({e[0] for e in every if 0 < e[0] < 0xFA000000}))}
    primary[0] = 0
    han = {cp: elements(root, chr(cp))[0][0] for cp in range(0x3400, 0x323B0) if is_han(cp) and 1 <= age(cp) <= UNICODE_AGE}
    primary.update({p: han_rank(cp) for cp, p in han.items()})
    secondary = {s: i for i, s in enumerate(sorted({e[1] for e in every} | {0, 0x500}))}
    tertiary = {t: i for i, t in enumerate(sorted({e[2] for e in every} | {0, 0x500}))}
    print("weights:", len(primary), len(secondary), len(tertiary))

    def packed(e):
        assert e[0] in primary, hex(e[0])
        assert e[2] == 0 or e[3] < 3
        return base32(primary[e[0]], 4) + base32(secondary[e[1]], 2) + base32(tertiary[e[2]] * 4 + e[3], 2)

    pool, pool_at = [], {}

    def pooled(es):
        key = tuple(packed(e) for e in es)
        if key not in pool_at:
            pool_at[key] = len(pool)
            pool.extend(key)
        return base32(pool_at[key], 4) + base32(len(key), 2)

    runs = []  # (first, length, text of the first, (primary, rest) to go on from)
    for cp in sorted(plain):
        es = plain[cp]
        if len(es) == 1:
            p, rest = primary[es[0][0]], packed(es[0])[4:]
            if runs:
                first, length, text, go = runs[-1]
                if go and go[1] == rest and first + length == cp and length < 32767 and p != 0 and p == go[0] + length:
                    runs[-1] = (first, length + 1, text, go)
                    continue
            runs.append((cp, 1, packed(es[0]), (p, rest)))
        else:
            text = pooled(es) + "OO"
            if runs and runs[-1][0] + runs[-1][1] == cp and runs[-1][2] == text and runs[-1][3] == (-1, ""):
                runs[-1] = (runs[-1][0], runs[-1][1] + 1, text, (-1, ""))
            else:
                runs.append((cp, 1, text, (-1, "")))
    index = []
    at = 0
    for b in range(0x1100):
        while at < len(runs) and runs[at][0] + runs[at][1] <= b << 8:
            at += 1
        index.append(base32(at, 3))
    table = [base32(f, 4) + base32(n, 3) + t for f, n, t, _ in runs]
    pairs = []
    for s in sorted(contracted, key=lambda s: [ord(c) for c in s]):
        assert len(s) <= 3, s
        codes = [ord(c) for c in s] + [0] * (3 - len(s))
        pairs.append("".join(base32(c, 4) for c in codes) + pooled(contracted[s]))

    begins = {c for s in contracted for c in s[:-1]}
    goes_on = {c for s in contracted for c in s[1:]}
    common, decomposed = [], []
    for cp in range(COMMON):
        d = nfd(chr(cp))
        es = elements(root, chr(cp))
        alone = all(c in plain for c in map(ord, d)) and unicodedata.combining(d[0]) == 0 and len(es) <= 4
        assert len(d) <= 3 and d[0] != "\0" or cp == 0
        flags = any(c in begins for c in d) + 2 * (d[0] in goes_on)
        rest = pooled(es[1:]) if alone and len(es) > 1 else base32(0, 6)
        first = packed(es[0]) if alone and es else base32(0, 8)
        common.append(first + (rest[5] if alone else "O") + rest[:4] + base32(flags, 1))
        decomposed.append("".join(base32(ord(c), 4) for c in d) + base32(0, 4 * (3 - len(d))))

    def lines(records):
        return ["".join(records[i:i + RECORDS_PER_LINE]) for i in range(0, len(records), RECORDS_PER_LINE)]

    variable = ucol_getVariableTop(root, ERR) & 0xFFFFFFFF
    check("variable")
    out = [header(f"the root collation and tailorings of\n// {cldr_version()} (ICU {VERSION})")]
    out.append(f"const collationRecordsPerLine = {RECORDS_PER_LINE}\n")
    out.append("// The greatest primary of punctuation and white space, which 'ignorePunctuation'\n// ignores.\n")
    out.append(f"const collationVariableTop = {max(r for p, r in primary.items() if p <= variable)}\n")
    digit = primary[elements(root, "0")[0][0]]
    out.append(f"// The primary of zero: the weights of digits.\nconst collationZero = {digit}\n")
    nine = primary[elements(root, "9")[0][0]]
    out.append(f"const collationNine = {nine}\n")
    out.append("// The secondary and the tertiary most elements have.\n")
    out.append(f"const collationCommonSecondary = {secondary[0x500]}\nconst collationCommonTertiary = {tertiary[0x500]}\n")
    out.append(f"const collationRunCount = {len(table)}\nconst collationContractionCount = {len(pairs)}\n")
    out.append(f"const collationCommonLimit = {COMMON}\n\n")
    for name, records in (("collationCommon", common), ("collationCommonDecomposed", decomposed), ("collationIndex", index), ("collationRuns", table), ("collationPool", pool), ("collationContractions", pairs)):
        out.append(block(name, lines(records)) + "\n")
    tag_names = list(tailored)
    var = lambda prefix, tag: prefix + "".join(p.capitalize() for p in tag.split("-"))
    for function, what in (("collationTailoring", "strings a collation orders its own way"), ("collationReorder", "order of scripts a collation has"),
                           ("collationSettings", "ways a collation differs in its settings")):
        out.append(f"/// The {what}.\nfn {function}(_ tag: String) -> String? {{\n")
        cases = []
        for tag in tag_names:
            if function == "collationTailoring":
                cases.append((tag, var("tailoring", tag)))
            elif function == "collationReorder" and tag in reorders:
                cases.append((tag, var("reorder", tag)))
            elif function == "collationSettings" and settings[tag]:
                cases.append((tag, '"' + " ".join(settings[tag]) + '"'))
        out.append(matched(cases))
    for tag in tag_names:
        entries = [base32(len(s), 1) + "".join(base32(ord(c), 4) for c in s) + "".join(packed(e) for e in es)
                   for s, es in sorted(tailored[tag].items())]
        out.append(block(var("tailoring", tag), entries or [""]) + "\n")
    # A character for each primary of the root, to ask the order of.
    sample = {}
    for cp, es in plain.items():
        if len(es) == 1 and es[0][0] and es[0][0] < 0xFA000000:
            sample.setdefault(primary[es[0][0]], chr(cp))
    ranks = sorted(sample)
    for tag, collator in reorders.items():

        def key(rank):
            b, n = u16(sample[rank])
            buffer = (C.c_uint8 * 200)()
            m = ucol_getSortKey(collator, b, n, buffer, 200)
            return bytes(buffer)[:m].split(b"\x01")[0]
        ordered = sorted(ranks, key=key)
        place = {r: i for i, r in enumerate(ranks)}
        found, i = [], 0
        while i < len(ordered):
            j = i
            while j + 1 < len(ordered) and place[ordered[j + 1]] == place[ordered[j]] + 1:
                j += 1
            end = ranks[place[ordered[j]] + 1] if place[ordered[j]] + 1 < len(ranks) else 0x10000
            found.append((ordered[i] if place[ordered[i]] else 2, end))
            i = j + 1
        out.append(block(var("reorder", tag), ["".join(base32(a, 4) + base32(b, 4) for a, b in found[i:i + 12]) for i in range(0, len(found), 12)]) + "\n")
    path = os.path.join(ROOT, "lib/Sable.Globalization/CollationTables.sb")
    open(path, "w", encoding="utf-8").write("".join(out).rstrip() + "\n")
    print(path, os.path.getsize(path), "runs", len(table), "pool", len(pool), "contractions", len(pairs))


os.makedirs(os.path.join(ROOT, "lib/Sable.Globalization"), exist_ok=True)
write_locales()
write_collation()
