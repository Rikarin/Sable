# Contributing to Sable

This is the guide to working on the compiler, runtime, standard library and
tools. For using Sable, start with the [README](README.md).

## Building and testing

You need the .NET 11 SDK. Building native programs also needs `clang` (`$CC`
if set); the native tests pass vacuously without it, and the debugger tests
need `lldb`.

```bash
dotnet build Sable.slnx
```

```bash
dotnet test Sable.slnx
```

That runs the five test projects. To run the `sable` you just built without
publishing it:

```bash
dotnet run --project src/Sable.Driver -- run examples/01-hello.sb
```

`--emit sil` and `--emit llvm-ir` print a stage's output instead of building,
and `-g` builds with DWARF debug information for lldb.

On macOS, `tools/linux/test.sh` runs the test suite in a Linux container.

## Repository layout

```
docs/        design documents; docs/diagnostics/ is what `sable explain` prints
spec/        the language specification
examples/    sample programs, and a static site generator (examples/ssg)
src/         the compiler, written in C# (docs/04 says why)
               Sable.Syntax          lexer, parser, trees, formatter
               Sable.Symbols         symbols, binding, type checking, lints
               Sable.SIL             the mid-level IR: lowering, memory-model checks, passes, interpreter
               Sable.CodeGen         LLVM IR and DWARF
               Sable.Driver          the `sable` command, packages, the build
               Sable.LanguageServer  `sable lsp`
lib/         the standard library, in Sable
runtime/     the C runtime native programs link
editors/     the VS Code extension and setups for other editors
benchmarks/  the benchmark suite and its history
tools/       Sable.Syntax.Generator  generates the node classes
               Sable.Visualiser        the syntax-tree visualiser
               and the scripts for benchmarks, fuzzing, Linux tests and the debugger
tests/       the test suite
```

## The tests

Each test project tests a layer: `Sable.Syntax.Tests`, `Sable.Symbols.Tests`,
`Sable.SIL.Tests`, `Sable.CodeGen.Tests` (native code, the runtime, packages and
the CLI) and `Sable.LanguageServer.Tests`. Some properties are tested over
whole directories, not single files:

- Every file in [examples/](examples/) parses as a complete compilation unit
  with no diagnostics and an exact round-trip.
- [tests/corpus/](tests/corpus/) and [tests/programs/](tests/programs/) hold
  programs that must compile with nothing to say.
- [tests/negative/](tests/negative/) holds programs that must fail. Each line
  that should fail says, in a comment, the exact diagnostic it produces.
- `lib/`, `examples/` and the test corpora must be formatted (`sable fmt`).
- Every diagnostic code must have a page in [docs/diagnostics/](docs/diagnostics/).
- Native programs are also run in the SIL interpreter, and the two must agree.

New diagnostics get a code in `src/Sable.Syntax/Diagnostics.cs` and a page under
`docs/diagnostics/`.

## The syntax tree

Node classes are generated from [src/Sable.Syntax/Syntax.xml](src/Sable.Syntax/Syntax.xml).
After changing the schema:

```bash
dotnet run --project tools/Sable.Syntax.Generator
```

The generated files are committed, and a test fails if they drift from the
schema.

The tree follows Roslyn's red/green design: immutable, position-independent
green nodes shared between tree versions, with thin red facades created on
demand. The parser is hand-written recursive descent with error recovery that
never drops text. Tokens it can't use become skipped-token trivia, and absent
ones are inserted as zero-width missing tokens.

That gives the full-fidelity round-trip invariant: concatenating every token's
full text reproduces the source byte for byte. It is enforced over the
repository's Sable files, hand-written cases, malformed input and random token
soup. Inputs the fuzzers have found losing or duplicating text are kept,
minimised, as regression tests.

`SyntaxTree.WithChange` applies an edit and reuses everything the edit didn't
touch. A one-character edit in a 10,000-line file reparses in about 1 ms. The
result must be exactly the tree a full parse would produce, token flags and
diagnostics included, and every incremental test asserts it.

`SyntaxFormatter.Format` renders a tree as canonical source, with no options.
Over every Sable file in the repository, formatting is tested to be idempotent
and never to change the tree.

## Seeing what the compiler made of a file

`sable-visualise` is the syntax-tree visualiser, Roslyn's Syntax Visualiser for
Sable. It shows every node, token and trivia, including the missing tokens and
skipped text error recovery produces. It also shows what the semantic model
says about each node: its type, and the symbol it binds to.

```bash
dotnet run --project tools/Sable.Visualiser -- examples/01-hello.sb
```

```bash
dotnet run --project tools/Sable.Visualiser -- examples/01-hello.sb --html hello.html
```

The first prints the tree as indented text; `--trivia` adds whitespace and
comments. The second writes one self-contained page: the source beside a
collapsible tree and a property pane. Selecting a node highlights its span, and
clicking the source selects the deepest node under the caret. `--syntax-only`
skips binding, and `--sil` prints what the file lowers to, verified, instead of
the tree.

## Design documents

[docs/04-implementation-plan.md](docs/04-implementation-plan.md) is the build
plan, with each phase as built, its measurements, and the exit criteria still
open. [docs/07-references.md](docs/07-references.md) is the annotated reading
list behind the design.

## Commits

Write commit subjects as short plain-English sentences saying what is now true
(`git log --format=%s` shows the style), with a body that explains why. Reference
issues with `Fixes #N` or `Part of #N`.
