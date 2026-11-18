# Sable

> A protocol-oriented systems language with C#-shaped syntax, Swift-shaped memory
> management, Rust-shaped safety, and a Goose-shaped data layout model.
> Compiled to native code via LLVM.

**`Sable` is a placeholder name.** It is short, unclaimed in the major package
registries, and the file extension `.sb` is free. Rename at will — the only
places the name is load-bearing are the toolchain binary (`sable`), the manifest
(`package.sb`, docs/05 §5.2) and the module namespace root.

---

## The one-paragraph pitch

Sable is what you get when you take C#'s syntax and tooling discipline, Swift's
value semantics and ARC, Rust's move checking, and Goose's insight that *most
object graphs do not need to be graphs at all*, and then refuse to pick only one.
It compiles ahead-of-time to native binaries through LLVM. It has protocols with
associated types, closed unions with exhaustive matching, generics that are
monomorphised by default and boxed on demand, optional runtime reflection that
you pay for only where you ask for it, and a single tool — `sable` — that builds,
tests, formats, documents and resolves packages.

## Documents in this repository

| File | What it is |
|---|---|
| [docs/01-overview.md](docs/01-overview.md) | Design pillars, influences, and the things deliberately left out |
| [docs/02-memory-model.md](docs/02-memory-model.md) | The tiered memory model: inline values, regions, ARC, and root inference |
| [docs/03-type-system.md](docs/03-type-system.md) | Protocols, unions, generics, variance, effects |
| [docs/04-implementation-plan.md](docs/04-implementation-plan.md) | The actual build plan: 7 phases, ~30 months, Roslyn architecture |
| [docs/05-toolchain.md](docs/05-toolchain.md) | `sable` CLI, manifest format, resolver, build graph |
| [docs/06-runtime-and-stdlib.md](docs/06-runtime-and-stdlib.md) | Runtime ABI, reflection, metadata, framework layout |
| [docs/07-references.md](docs/07-references.md) | Annotated reading list — papers, compilers, and prior art worth stealing from |
| [docs/08-source-generators.md](docs/08-source-generators.md) | Source generators: Sable functions that write code while a package compiles, and why Sable has no macros |
| [spec/SYNTAX.md](spec/SYNTAX.md) | **The full syntax reference**, with examples for every construct |
| [spec/grammar.ebnf](spec/grammar.ebnf) | The complete formal grammar |
| [spec/lexical.md](spec/lexical.md) | Tokens, literals, trivia, and the newline rules |
| [examples/](examples/) | Working-shaped sample programs |

## Read in this order

1. `docs/01-overview.md` — why the language exists
2. `spec/SYNTAX.md` — what it looks like
3. `docs/02-memory-model.md` — the part that is actually novel
4. `docs/04-implementation-plan.md` — how to build it

---

## Repository layout

```
docs/        design documents; docs/diagnostics/ is what `sable explain` prints
spec/        the language specification
examples/    sample programs, and a static site generator (examples/ssg)
src/         the compiler, written in C# (see docs/04 for why)
               Sable.Syntax          lexer, parser, trees, formatter
               Sable.Symbols         symbols, binding, type checking, lints
               Sable.SIL             the mid-level IR: lowering, memory-model checks, passes, interpreter
               Sable.CodeGen         LLVM IR and DWARF
               Sable.Driver          the `sable` command, packages, the build
               Sable.LanguageServer  `sable lsp`
lib/         the standard library, in Sable
runtime/     the C runtime native programs link
editors/     the VS Code extension
benchmarks/  the benchmark suite and its history
tools/       Sable.Syntax.Generator  generates the node classes
               Sable.Visualiser        the syntax-tree visualiser
               and the scripts for benchmarks, Linux tests and the debugger
tests/       the test suite
```

Node classes are generated from [src/Sable.Syntax/Syntax.xml](src/Sable.Syntax/Syntax.xml).
After changing the schema:

```bash
dotnet run --project tools/Sable.Syntax.Generator
```

The generated files are committed, and a test fails if they drift from the schema.

### Seeing what the compiler made of a file

`sable-visualise` is the syntax-tree visualiser — Roslyn's Syntax Visualiser,
for Sable. It shows every node, token and trivia, including the missing
tokens and skipped text error recovery produces, and what the semantic model
says about each node: its type and the symbol it binds to.

```bash
dotnet run --project tools/Sable.Visualiser -- examples/01-hello.sb
```

```bash
dotnet run --project tools/Sable.Visualiser -- examples/01-hello.sb --html hello.html
```

The first prints the tree as indented text (`--trivia` adds whitespace and
comments). The second writes one self-contained page: the source beside a
collapsible tree and a property pane, where selecting a node highlights its
span and clicking the source selects the deepest node under the caret.
`--syntax-only` skips binding, and `--sil` prints what the file lowers to,
verified, instead of the tree.

## Building

Requires the .NET 11 SDK. Building native programs also needs `clang`
(`$CC` if set); the native tests pass vacuously without it.

```bash
dotnet build Sable.slnx
```

```bash
dotnet test Sable.slnx
```

### Compiling a program

`sable` compiles through SIL and LLVM IR to a native executable, linking the
C runtime in [runtime/](runtime/). A package is a directory with a manifest,
`package.sb`, written in Sable ([docs/05 §5.2](docs/05-toolchain.md)):

```bash
dotnet run --project src/Sable.Driver -- new hello
cd hello
dotnet run --project ../src/Sable.Driver -- run
```

`build`, `check`, `test`, `bench`, `fmt`, `doc`, `add`, `publish` and the
rest work on the package; [docs/05 §5.1](docs/05-toolchain.md) lists them.
A lone file builds too:

```bash
dotnet run --project src/Sable.Driver -- run hello.sb
```

`--emit sil` and `--emit llvm-ir` print a stage's output instead, and `-g`
builds with DWARF debug information for lldb. When the compiler rejects a
program, `sable explain S1234` says more about the diagnostic.

## Status

**Phases 0 to 5 are built** — the syntax layer, the type checker, SIL and
native code, the memory model, the runtime and standard library, and the
toolchain with its package manager and language server. Phases 6
(self-hosting) and 7 (hardening) have not started.
[docs/04-implementation-plan.md](docs/04-implementation-plan.md) has each
phase as built, with its measurements, and the exit criteria still open:

| Phase | Open exit criteria |
|---|---|
| 0 — Syntax | every construct of `SYNTAX.md` parsing, a 2,000-file round-trip corpus, a 24-hour fuzz run (a nightly run starts from a new seed each night) |
| 1 — Symbols and type checking | none: the go/no-go's 10,000 lines of real Sable are ~12,900 that type-check (`lib/` 6,600, `tests/corpus/` 2,900, `tests/programs/` 2,100, `examples/` 1,300) |
| 2 — SIL and native code | none |
| 3 — The memory model | none |
| 4 — Runtime, reflection, core library | daily use of a real program (`examples/ssg` is written) |
| 5 — Toolchain, packages, IDE | a third party publishing a package: there is no network registry yet |

`dotnet test Sable.slnx` runs the five test projects. Every file in
[examples/](examples/) parses as a complete compilation unit with no
diagnostics and an exact round-trip; [tests/corpus/](tests/corpus/) and
[tests/programs/](tests/programs/) hold programs that must compile with
nothing to say, and [tests/negative/](tests/negative/) holds programs that
must fail, each claiming the exact diagnostic its line produces.

### The syntax layer

The lexer covers all of [spec/lexical.md](spec/lexical.md): full-fidelity
trivia, nested block comments, every literal form including interpolated and
raw strings, and the statement-terminator rule.

The tree is Roslyn's red/green design: immutable, position-independent green
nodes shared between tree versions, with thin red facades created on demand.
The parser is hand-written recursive descent with error recovery that never
drops text — unusable tokens become skipped-token trivia and absent ones are
inserted as zero-width missing tokens.

The full-fidelity round-trip invariant — concatenating every token's full text
reproduces the source byte for byte — is enforced over the `examples/` corpus,
a table of hand-written cases, deliberately malformed input, and several
thousand rounds of random token soup that mixes every token the parser treats
specially with newlines. Inputs the soup has found losing or duplicating text
are kept, minimised, as regression tests. Nesting past 256 levels is reported
and skipped rather than overflowing the stack.

The newline rule of [spec/lexical.md §4](spec/lexical.md) is applied by the
parser, not only computed by the lexer: a line that ends a statement ends it,
so `foo` followed by `(1, 2)` on the next line is two statements, and two
statements on one line need a `;`.

`SyntaxTree.Parse` reads a whole file; `ParseExpression`, `ParseType` and
`ParseStatement` read a single construct, which is what most of the tests use.
`SyntaxTree.WithChange` applies an edit, reusing everything it did not touch:
a one-character edit in a 10,000-line file reparses in about 1 ms (Release:
0.9 ms at best, 1.2-1.5 ms median on a heavily loaded machine), against
about 20-35 ms for a full parse — inside the plan's 2 ms. The contract
is that the result is exactly the tree a full parse would have produced, token
flags and diagnostics included, and every incremental test asserts it — among
them chains of edits applied one after another, to files that already have
errors.

`SyntaxFormatter.Format` renders a tree as canonical source. It has no options,
by design. Two properties are tested over every Sable file in the repository
rather than asserted on sample output: formatting is idempotent, and
formatting never changes the tree. `lib/`, `examples/` and the test corpora
are kept formatted, and a test fails when one is not.
