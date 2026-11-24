# 9. Globalization

`Sable.Globalization` writes and reads numbers, money, dates and lists as the
speakers of a language do, says which form their plurals take, and orders
words as their dictionaries do. This says what it is built on and why
(issue #35).

## 9.1 A locale is always given

Nothing anywhere in the library reads a locale nobody passed. `"\(1.5)"`,
`F64.parse`, `Date.format(pattern:)`, `<` on strings and `uppercased()` mean
the same on every machine, whatever its settings; .NET's culture by default,
which makes `1.5` unreadable on a German machine, is the mistake avoided.
`Locale.current` is the system's and is only ever an argument.

`Locale.invariant` (the tag `und`) writes as no locale does: no grouping, a
`.`, ISO 8601's dates, English names, the root collation. `format(spec,
locale: .invariant)` is `format(spec)` exactly. A language there is no data
for has it too, rather than another language's; `Locale.dataLocale` says
whose data a locale has.

## 9.2 The backend: CLDR as tables

| | The system's ICU or CFLocale | A CLDR snapshot in tables |
|---|---|---|
| The same result on every system | No: each has its own version, Apple's and Windows's are changed, and a user may change more | Yes |
| The interpreter as native code | Only by calling the same library both ways | Yes: it is Sable code |
| Where there is none (WASI, musl, a container) | Fails or falls back | Works |
| Size, of a program that uses no locale | Nothing | Nothing: what is not reached is not linked |
| Size, of one that does | Little, where the system has it | 0.3 MB for formatting, 0.4 MB more for collation |
| Locales | All | Those compiled in |
| Being up to date | The system's doing | A new Sable |

Decided: **tables**, as the Unicode tables of `Sable.Core`, `Sable.Text` and
IDNA are, and ICU4X's. Programs whose output depends on the machine cannot be
tested, and the interpreter and native code must agree; both weigh more than
having every locale. The tables are string literals, as `tools/unicode-tables.py` explains, so a
program carries them only when something it calls reads them. Measured on
arm64 macOS (default build): a program that prints a line is 52 KB, as it
was; one that formats a number by a locale 394 KB; one that compares two
strings by one 774 KB. What a locale's record costs is read when the
`Locale` is made (a record is split into fields once); a `NumberFormatter`,
`DateTimeFormatter` or `Collator` made once serves many values.

The module is `Sable.Globalization` rather than part of `Sable.Text`: it is
built on `Sable.Text` (normalization), `Sable.Time` and `Sable.Math`
(`Decimal`, `BigInt`), which must not depend on one another through it, and
Unicode's algorithms are no locale's.

### Where the data is from

`tools/locale-tables.py` reads it through the C API of an ICU4C installed on
the developer's machine (Homebrew's, unmodified upstream: ICU 79, CLDR 49)
and writes `LocaleTables.sb` and `CollationTables.sb`, which are committed.
It fetches nothing and no build runs it. `LOCALES` in it is the list of
locales, 62 now; `--all` takes every one ICU has. macOS's `libicucore` is not
used, as Apple changes its data and its licence is not Unicode's.

The data is under the Unicode License V3, which allows copying it as long as
its notice goes with it. Both generated files begin with the notice, and they
are part of the compiler and its packages as source.

`tools/locale-tests.mjs` writes what Node's `Intl` — the same ICU, by another
way than the tables — makes of 8,500 cases, to `tests/lib/Sable.Globalization/data/`,
and the tests hold the library to it: 4,200 numbers (and read back), 2,000
dates (and read back), 17,000 plurals, 2,200 lists, and 64 sorted lists of
160 to 900 texts in 14 scripts. V8 alters one thing, writing U+202F in dates
as a space, which the tests allow for. The UCA's own conformance files
(CollationTest) are not on the machine and were not run.

## 9.3 What there is

- **`Locale`**: BCP 47 tags parsed and put in canonical form (case, order of
  variants, extensions and keywords, grandfathered and replaced codes);
  `fromPosix`; `current`, from `LC_ALL`, `LC_MESSAGES` and `LANG`, then what
  macOS and Windows are set to; the `nu`, `co`, `kn`, `kf`, `ks` and `ka`
  keywords.
- **Numbers**: plain, percentages, scientific, money, for every integer and
  float, `Decimal` and `BigInt` (`LocalizableNumber`), by a `NumberFormat` or
  a `FormatSpec`. Digits and rounding are the library's own formatting (§6.4,
  ties to even, exact); the locale gives the digits' shapes, groups (Indian
  ones too), signs and symbols. Reading turns the text to plain digits and
  hands it to the type's own `parse`. It is lenient about which space or
  minus is typed and strict about where separators are: German `"1.5"` is an
  error, not 15.
- **Dates and times** (Gregorian): full, long, medium, short, and CLDR
  patterns, for Sable.Time's types, written and read.
- **`PluralRules`** (cardinal and ordinal), interpreted from the CLDR's rule
  text; **`ListFormatter`** (and, or, unit; three widths; Spanish *e*/*u* and
  Hebrew's hyphen).
- **`Collator`**: the UCA over the CLDR root and 36 tailorings (with
  Swedish, Danish, German phonebook, Spanish traditional, Czech, Hungarian,
  Thai, …): contractions near and far, expansions, script reordering,
  backwards accents, strengths, case first, numeric, punctuation ignored;
  `compare`, and sort keys that compare as it does.

## 9.4 What there is not

- Time zone names: a full or long time ends with the offset as the locale
  writes it ("GMT+1"), where ICU may write "CET".
- Calendars other than the Gregorian (the default in Thai, Persian and some
  Arabic locales is another); date patterns by skeleton; relative times and
  intervals; compact notation ("1.2K"); names of currencies, languages and
  regions.
- Han order: ideographs sort in UTS #10's implicit order, not by radical, and
  there is no pinyin, stroke, JIS or Hanja tailoring, each a table larger
  than all the rest.
- Case mapping by locale (Turkish İ), and locale-aware search.
- Serbian in Latin letters, and every locale not in `Locale.available`.
- Characters newer than Unicode 16, the version of Sable.Text's tables, sort
  as unassigned.
- The C runtime asks Windows for the user's locale
  (`GetUserDefaultLocaleName`) and macOS (CoreFoundation, loaded as it is
  asked); on Linux and WASI the environment alone says.
