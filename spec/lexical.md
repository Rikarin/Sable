# Lexical Structure

## 1. Source text

Source files are UTF-8, no BOM. The compiler rejects a BOM rather than skipping
it. Line terminators: `LF`, `CRLF`, or `CR` — all normalised to `LF` by the
lexer, with the original preserved in trivia so the formatter round-trips.

## 2. Trivia

Everything that is not a token is **trivia**, and trivia is attached to tokens
rather than discarded. This is the Roslyn model and it is why the formatter,
the IDE, and the refactoring engine can all operate on the same tree.

- Leading trivia attaches to the following token.
- Trailing trivia (up to and including the end of line) attaches to the
  preceding token.
- `token.ToFullString()` for every token in a tree, concatenated, reproduces the
  source file byte for byte. This is a tested invariant, not an aspiration.

Trivia kinds: `Whitespace`, `NewLine`, `LineComment`, `BlockComment`,
`DocComment`, `DisabledText` (inactive `#if` regions), `ShebangLine`.

## 3. Identifiers

```
Identifier     = IdentStart { IdentContinue }
IdentStart     = Letter | "_"                      (* Unicode XID_Start + '_' *)
IdentContinue  = Letter | Digit | "_"              (* Unicode XID_Continue    *)
```

Identifiers are compared after NFC normalisation. A leading `_` is conventional
for "unused" but carries no special meaning except that `_` alone is the
wildcard and is not an identifier.

Escaped identifiers for keyword collisions: `` `class` ``, `` `match` ``.

## 4. Statement terminators (the newline rule)

Sable does not require semicolons. A newline ends a statement **unless** the
line cannot yet be a complete statement. Concretely, the lexer suppresses the
statement terminator when:

1. The last token on the line is one of:
   `( [ { , . : = => -> | & && || + - * / % << == != < <= >= ?? as is where`
   or any assignment operator, or any keyword that must be followed by more
   (`return` on its own line *does* terminate — see below). `>`, `>>` and `?`
   are deliberately absent: they close a type, so they can end a statement.
2. The first token on the next line is one of:
   `. ?. ? : => -> , ) ]`, `else`, `catch`, `where`, or a binary operator — or
   `while` directly after a `}`, which continues `repeat { }`. A `while`
   anywhere else starts a loop of its own.
3. The lexer is inside unbalanced `(` or `[`. A `{` inside them opens a body of
   statements, where newlines end statements again until its `}`.

`return`, `break`, `continue`, `throw` and `fallthrough` always terminate at the
newline — a value must be on the same line. This avoids JavaScript's ASI hazard.

Three consequences are deliberate and worth stating:

- **`}` is not a continuation token.** A closing brace ends the statement before
  it, so `let x = 1` inside a block terminates at its own newline. `}` *can*
  itself end a statement (`fn f() { }` followed by a newline terminates).
- **`{` must be on the same line as its header.** An opening brace that starts a
  line would be ambiguous with a trailing closure, so Sable takes Go's rule
  rather than Swift's and rejects Allman style. The diagnostic says so.
- **`>`, `>>` and `?` can end a statement**, so that `var xs: List<Int>`,
  `List<List<Int>>` and `var x: Int?` terminate at the newline. The cost is
  that a multi-line expression may not end a line on `>`, `>>` or `?`;
  parenthesise it instead.

A `;` may be written explicitly to put several statements on one line. The
formatter removes redundant ones.

## 5. Keywords

### Reserved

```
abstract   actor      alias      any        as         async      await
borrowing  break      case       catch      class      const
consuming  continue   default    defer      deinit     do         else
enum       extension  fallthrough false     final      fn         for
guard      if         import     in         init       inout      internal
is         lazy       let        match      module     move       mutating
none       nonisolated nonmutating open     operator   out        override
package    postfix    prefix     private    protocol   public
ref        region     repeat     required   rethrows   return     sealed
self
Self       some       static     struct     subscript  super      switch
throw      throws     true       try        typealias  union      unowned
unsafe     using      var        weak       where      while      yield
```

`typealias` declares nothing: Sable spells it `alias`, which also gives a
protocol's `associatedtype` its type in a conforming one. It is reserved so
that code written in Swift's spelling is told so (S1012).

`ref` and `borrowing` declare nothing either: a reference is written after
its type, `T&`, as a parameter's (`a: Node&`), a result's (`-> Node&`) or a
variable's. They are reserved so that code written in either spelling is
told so, and pointed at `T&` (S1013).

### Contextual

Usable as identifiers except in their syntactic position:

```
associatedtype  convenience  didSet  get  in (capture lists)
set  willSet  comptime  unchecked
```

`newValue` and `oldValue` are ordinary identifiers that a property's `set`,
`willSet` and `didSet` bodies have implicitly bound; they are not keywords.

### Reserved for future use

```
trait  impl  where_clause  become  do_notation  effect  handler
```

## 6. Literals

### Integer

```
IntegerLiteral = DecimalLit | HexLit | OctalLit | BinaryLit
DecimalLit     = Digit { Digit | "_" }
HexLit         = "0x" HexDigit { HexDigit | "_" }
OctalLit       = "0o" OctalDigit { OctalDigit | "_" }
BinaryLit      = "0b" BinDigit { BinDigit | "_" }
```

Underscores are separators and may not lead. Default type is `Int`; the type
is adjusted by context (`let x: U8 = 200`). A literal that does not fit its
inferred type is a compile error, not a wrap.

Suffixes are **not** supported — use a type annotation or `as`.

### Floating point

```
FloatLiteral   = DecimalLit "." DecimalLit [ Exponent ]
               | DecimalLit Exponent
               | HexLit "." HexDigits "p" [ Sign ] DecimalLit
Exponent       = ( "e" | "E" ) [ "+" | "-" ] DecimalLit
```

Default type `F64`. `1.` and `.5` are **not** valid — a digit is required on
both sides, so that `1..<5` and `x.0` lex unambiguously.

### Character

```
CharLiteral = "'" ( SourceChar | EscapeSeq ) "'"
```

A `Char` is one Unicode scalar value (not a UTF-16 code unit, not a grapheme
cluster). `'\u{1F600}'` is a single `Char`.

### String

Three forms:

```sable
"simple, with \n escapes and \(interpolation)"

"""
multi-line. The indentation of the closing delimiter is stripped from
every line. A trailing backslash joins lines. \(interpolation) works.
"""

#"raw: \n is two characters, \(x) is literal, " needs no escape"#
##"raw with more hashes, so "# can appear literally"##
```

Escapes: `\0 \\ \t \n \r \" \' \u{...}` and `\(expr)` for interpolation.
Interpolation nests arbitrarily; the lexer tracks a brace-depth stack and
keeps the whole string as one token. The parser then takes that token apart so
that each hole is an ordinary expression in the tree, slicing the original
characters so the pieces tile the token exactly. A colon after a hole's
expression, `\(x:08x)`, starts its format spec (SYNTAX.md §16), which is kept
as one token of raw text up to the closing paren.

Strings are UTF-8. `String` indexing is by `String.Index`, not by integer —
byte offsets are available through `.utf8`, scalars through `.scalars`, and
grapheme clusters through `.characters`.

### Array, dictionary, tuple, record

Not lexical — see the grammar. Note `[:]` lexes as three tokens and means the
empty dictionary in expression position.

## 7. Operators and punctuation

```
( ) [ ] { }
. ?. .. ..< ...
, : ; ?
-> =>
= += -= *= /= %= &= |= ^= <<= >>= ??= &+= &-= &*=
+ - * / %   &+ &- &*
& | ^ ~ << >>
== != === !== < <= > >=
&& || !
?? @ # _ \
```

`&` is overloaded three ways and resolved by position: binary bitwise-and,
prefix address-of (which, before a call's argument, marks one the call may
write: `inc(&n)`), and postfix reference-type marker. The parser disambiguates
by whether it is in type or expression position. An `&` immediately followed
by `+`, `-` or `*` is not one of these: the lexer takes the pair (with an `=`
after, the three) as a wrapping operator, `&+ &- &*` (`&+= &-= &*=`), longest
match first, so `a&-b` is `a &- b` and a bitwise-and with a negated operand
needs the space, `a & -b`.

`<` and `>` are overloaded as comparison and as generic brackets. Resolution is
parser-directed: on seeing `<` immediately after a type-position identifier with
no intervening whitespace, the parser speculatively scans for a matching `>` not
crossing a statement terminator. This is the C# rule and it has the same
pathological cases, which are accepted as the cost of familiar syntax.

## 8. Comments

```sable
// line
/* block /* nested */ still in the block */
/// doc comment for the following declaration
/** block doc comment */
//! module-level doc comment (first thing in a file)
```

Block comments nest. An unterminated block comment is an error reported at the
opening delimiter, not at EOF.

## 9. Compiler directives

```
#if #elseif #else #endif
#file #line #column #function #module
#error("message") #warning("message")
```

`#if` regions that are not taken are lexed into `DisabledText` trivia — they are
not parsed, but they are preserved in the tree so the formatter does not destroy
them and the IDE can offer to switch configurations. The directive lines
themselves are `Directive` trivia.

A directive must be the first thing on its line, which is what separates it
from the `#file`-style builtins — those five names are read as builtins
wherever they appear, and any other `#name` at the start of a line is a
directive, reported as unknown if it is not one of the six above. The condition after `#if` and `#elseif` may
use identifiers, `true`, `false`, `os(...)`, `arch(...)`, `feature(...)`, `!`,
`&&`, `||` and parentheses, and nothing else: it is evaluated before any
semantic analysis exists. A malformed condition disables its region and reports
a diagnostic rather than derailing the parse.

Which symbols are defined is part of the parse configuration, so two parses of
one file can produce different trees. Both round-trip.

## 10. Shebang

A file may begin with `#!...` on line 1. It is trivia. This exists so that
`sable run script.sb` can be used with `#!/usr/bin/env sable` for single-file
scripts.
