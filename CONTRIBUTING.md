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

That runs the five test projects. The randomised fuzz tests (`Category=Fuzz`)
are CPU-heavy, and the timing tests (`Category=Timing`) are sensitive to a
loaded machine; CI and the nightly fuzz job run both, so locally you can leave
them out:

```bash
dotnet test Sable.slnx --filter "Category!=Fuzz&Category!=Timing"
```

To run the `sable` you just built without
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
               and the scripts for benchmarks, fuzzing, Linux tests, the debugger
               and packaging a release (release/package.sh)
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

`Sable.CodeGen.Tests` is the slow project: most of its tests compile a program
and run it, natively and in the interpreter. What keeps it quicker:

- **The standard library checked once.** Once a build has checked the
  library's every body, and lowered and verified its every function, with a
  program that could not change any of that, the next such build checks only
  its own bodies and lowers only the library functions it reaches
  (`LibraryCheck`): a small program binds a few of the library's files, not
  all of them. It is remembered for the process and in
  `$SABLE_HOME/cache/library-checked`, under a hash of the compiler. A
  program that extends a library type, subclasses a library class or brings
  its own file of a library module has the whole library checked with it,
  as before; `SABLE_CHECK_LIBRARY=1` does that for every program, to compare
  against.
- **Both ways of declaring the library.** A build against the cache takes the
  standard library's declarations from its entry rather than its source
  (docs/05 §5.4, "Declarations from the interface"); a compilation made in a
  test declares them from source. `SABLE_LIBRARY_SYMBOLS=interface` makes every
  compilation that can take them from symbols made in the process — CI runs
  `Sable.Symbols.Tests` both ways — and `SABLE_LIBRARY_SYMBOLS=source` makes
  builds declare the source, to compare against.
- **Native builds where they matter.** A test that holds native code and the
  interpreter to the same answer (`Programs.AssertAgree`) builds the program
  natively only if it, or its class, is marked `[Native]`: tests of layout,
  the ABI, reference counting, the runtime, the platform, traps. The rest
  run in the interpreter alone, which compiles the program once rather than
  twice and starts no clang. `SABLE_TEST_NATIVE=all` builds every one
  natively too, as CI does, so CI's coverage is the whole of it:

  ```bash
  SABLE_TEST_NATIVE=all dotnet test tests/Sable.CodeGen.Tests --filter "Category!=Fuzz&Category!=Timing"
  ```

  A new test class about native code gets `[Native]`; `--filter
  Category=Native` runs the marked ones. Tests that only build natively
  (`Programs.BuildAndRun`, `Harnesses.Native`) always do.
- **Both ways of compiling generics.** `SABLE_GENERICS=unspecialised` (or
  `specialised`) builds every program of the run with its generic functions
  compiled once for every type, or specialised for each, whatever the build
  would otherwise do (docs/06 §6.2); a test that is about one of the two
  says so with `SilGen.UseGenerics`. `SABLE_GENERICS_TRACE=1` prints, for
  each generic function, whether it was compiled once and, if not, the
  expression that kept it specialised.
- **Shards in CI.** CI runs the project in four jobs per OS, split by test
  class ([tools/ci/codegen-shard.sh](tools/ci/codegen-shard.sh)).
  `ShardTests` fails if a class with tests is in no shard — which is what a
  test class not declared `public sealed class` at the top level of its file
  would be — or if the workflow's matrix does not list every shard.

New diagnostics get a code in `src/Sable.Syntax/Diagnostics.cs` and a page under
`docs/diagnostics/`.

## Standard library

The library is Sable, in [lib/](lib/), one directory per module, embedded in
the compiler (`Sable.Core` by Sable.Symbols, the rest by Sable.Driver, every
`.sb` file of the directory). Its layout ([docs/06 §6.4](docs/06-runtime-and-stdlib.md)):

- **One public type per file**, named after it: `lib/Sable.Collections/Stack.sb`.
  A small helper type private to one type lives in its file; a family that is
  one concept (`Iterator` with `Sequence`, `Channel` with `ChannelIterator`)
  may share one; free functions go in a file named for what they do
  (`Sort.sb`, `Math.sb`). A large extension of a type for one feature may
  have its own file, `<Type>+<Feature>.sb` (`String+Sequence.sb`). Never
  several unrelated public types in one file. `lib/Sable.Core/` is flat.
- **Tests** are Sable, with `@test`: `tests/lib/<Module>/<Type>Tests.sb`, one
  file per type, one behaviour per test, named as a sentence. Each module's
  tests are built as one program; `TestRunnerTests` runs every module's.
- **Doc comments** on every public declaration, with a ```` ```sable ````
  example where useful. `sable test --doc lib/<Module>` runs a module's
  examples as tests, as CI does for every module, and `sable test --doc
  lib/<Module>/<File>.sb` one file's: the files read from disk stand in
  for the embedded ones of their names.

Every file of the library is held to a coverage threshold by its own tests:

```bash
tools/stdlib-coverage.sh                     # every module
tools/stdlib-coverage.sh Sable.Collections   # some
```

For each module it runs `sable test --cover lib/<Module> tests/lib/<Module>/`,
which counts lines and branches in the library's code rather than the
tests', and prints each file's share of both. A file under its threshold in
[tools/stdlib-coverage.thresholds](tools/stdlib-coverage.thresholds) fails
it, as CI does: 85% of lines and 70% of branches by default, which every new
file must meet. An override is a line naming the file with its own minimums
and a `#` reason; those marked "raise me" are files that were under the
default when the check began — raise them as their tests grow, never lower
them. A module with no tests yet is measured as run by nothing. `SABLE=...`
picks the `sable` to use (by default src/Sable.Driver is built), and each
module's LCOV is left in `target/stdlib-coverage/`, with a `report.tsv` that
`tools/ci/coverage-report.py` prints as tables: the summary of CI's *Standard
library coverage* runs. To see one file's lines
not run, run the command above yourself: `sable test --cover
lib/Sable.Collections/Stack.sb tests/lib/Sable.Collections/`.

## Benchmarks

```bash
tools/bench/compare.py                  # against .NET, Rust and C
tools/bench/compare.py map-int words    # some
```

It prints a table of each benchmark's time in each language, measured the same
way in all of them (Sable's by `sable bench`), and CI's
*Benchmarks* runs have the same tables as their summary. Each benchmark is a
folder with its ports in it, under `benchmarks/library` (the standard library)
or `benchmarks/programs`; [benchmarks/README.md](benchmarks/README.md) says how
to add one. CI fails on a port that does not build or computes something else,
never on a time.

```bash
# Before and after, with a sable of before, built elsewhere:
sable bench benchmarks/library/map-int --job "before=sable=$BEFORE/sable" --job after=
sable bench benchmarks/library sha --job O2=-O2 --job O3=-O3
```

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

## Fuzzing

Two kinds of fuzzer look for inputs the compiler mishandles.

The random ones are tests, run small by every build and large every night
([.github/workflows/fuzz.yml](.github/workflows/fuzz.yml)): token soup through
the parser (`Sable.Syntax.Tests`, `Soup`), the repository's programs mutated by
whole syntax nodes ([SyntaxMutator](src/Sable.Syntax/SyntaxMutator.cs)) and at
token boundaries through the whole front end (`CompilerFuzzTests`), random
registries through the package resolver (`ResolverFuzzTests`), and generated
programs run every way they can be, which must agree (`DifferentialFuzzTests`).
`SABLE_FUZZ_SEED` and
`SABLE_FUZZ_SCALE` run them from another seed, many times over;
`tools/fuzz/overnight.sh 8h` runs them all in rounds until a deadline and keeps
every distinct failure.

The coverage-guided one, [tools/fuzz/coverage.sh](tools/fuzz/coverage.sh),
sees the code: SharpFuzz rewrites the Sable assemblies to record the branches
they take, and an input that takes a new one is kept and mutated further. Its
targets ([tools/fuzz/Sable.Fuzz](tools/fuzz/Sable.Fuzz)) are the parser (round
trips, every node's children covering it exactly, incremental reparse equal to
a full one), the editor (the language server's documents, edited by line and
column as an editor sends them, equal to a full parse), the front end (what
`CompilerFuzzTests` asks: through the passes, the printed SIL reading back,
async lowering and the optimising passes), SIL as text (read or refused with
the parser's error, printed and read back, verified SIL kept verified by the
passes), the resolver (on a registry written as text, checked against trying
every assignment), the package files (manifest, lock file, registry index),
library interfaces, and generated programs run several ways in the
interpreter. The parser and the front end have node-level mutants too. The mutations use dictionaries generated from the lexer's own
tables, and the corpus starts from the repository's `.sb` and `.sil` files;
new syntax or a new kind of lowering wants a seed in `tests/corpus`, as
`20-coroutines.sb` is for generators, throwing iteration and async functions.

```bash
tools/fuzz/coverage.sh 30m                 # every target for half an hour
tools/fuzz/coverage.sh 2h parser frontend  # some of them
tools/fuzz/coverage.sh --summary           # the latest run's results so far
```

On Linux, with `LIBFUZZER_DOTNET` set to a built libfuzzer-dotnet, the engine
is libFuzzer, as the nightly jobs run it, one job and an hour a target (the first third of
the parser's and the front end's in the fuzzer's own loop, which alone can call
the node-level mutator). On macOS the targets run under that in-process loop,
on the same coverage. The corpus persists in
`artifacts/fuzz-corpus/`. Results go to `artifacts/fuzz/<start>/`, as
`overnight.sh`'s do: `summary.txt` lists each distinct failure with the inputs
that hit it, and each input is kept with what replaying it says and a
minimised copy (`.min`). `<run>/bin/Sable.Fuzz <target> replay <file>`
reproduces one. A fixed bug's minimised input becomes a regression test in the
layer's own project.

`sable fuzz` is the toolchain's own fuzzer, for any package's `@fuzz`
functions (docs/05 §5.1), and what replaces the C# fuzzers once the compiler
is written in Sable. The repository's targets are the library's readers, in
[tools/fuzz/targets](tools/fuzz/targets) with their seeds and dictionaries
(`fuzz/<Module.target>/`, `.dict`), and `fromBytes` in
[tools/fuzz/inline_layout.sb](tools/fuzz/inline_layout.sb), each fuzzed nightly;
a new reader in the library wants a target and a seed or two there. Locally, as
with the other fuzzers, keep runs short:

```bash
export CC=$(brew --prefix llvm)/bin/clang                     # macOS: a clang with libFuzzer
sable fuzz tools/fuzz/targets --list
sable fuzz tools/fuzz/targets FuzzText.utf8 --runs 10000      # one process, to the first failure
sable fuzz tools/fuzz/targets FuzzText.utf8 --duration 5m
sable fuzz tools/fuzz/targets --replay                        # the saved inputs, with any clang
sable test tools/fuzz/targets                                 # the same, as tests
```

`gh workflow run "Nightly fuzz" --ref <branch> -f only=sable-fuzz -f sable_fuzz_duration=30s`
runs the nightly targets shortly on a branch.

## Design documents

[docs/04-implementation-plan.md](docs/04-implementation-plan.md) is the build
plan, with each phase as built, its measurements, and the exit criteria still
open. [docs/07-references.md](docs/07-references.md) is the annotated reading
list behind the design.

## Commits

Write commit subjects as short plain-English sentences saying what is now true
(`git log --format=%s` shows the style), with a body that explains why. Reference
issues with `Fixes #N` or `Part of #N`.
