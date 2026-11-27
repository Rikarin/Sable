# Working on Sable: notes for coding agents

Sable is a Swift-like language. The compiler is C# (.NET 11) emitting LLVM IR;
the runtime is C (`runtime/`); the standard library is Sable (`lib/`).
[CONTRIBUTING.md](CONTRIBUTING.md) has the layout and the commands; this file
is what to hold to while changing things.

## Ground rules

- **Everything is the standard library.** No third-party libraries, no
  packages, even where an issue suggests binding one. C in `runtime/` only
  where an instruction or a system call needs it.
- **A compiler gap is fixed in the compiler,** never worked around in the
  library. If the fix is too big, stop and say so.
- **Breaking changes are fine:** there are no users. No deprecation shims.
- **One spelling for each thing.** Before adding API, look for what exists
  (and in other modules); extend it rather than add a rival.
- **Debug builds stay specialised by default;** unspecialised generics
  (`SABLE_GENERICS=unspecialised`) are tested, not the default.
- Data from official sources (test vectors, RFCs, specs) may be downloaded
  into a scratch directory, treated as untrusted and never executed. Bulk
  data is not committed: a repeatable script goes under
  `tools/conformance/<area>/` and small tests under `tests/lib/`.

## Building and testing

```bash
dotnet build Sable.slnx -m:2
dotnet test Sable.slnx --filter "Category!=Fuzz&Category!=Timing"
src/Sable.Driver/bin/Debug/net11.0/sable test tests/lib/Sable.Core
src/Sable.Driver/bin/Debug/net11.0/sable test --doc lib/Sable.Core
src/Sable.Driver/bin/Debug/net11.0/sable fmt --check lib tests/lib examples tools/fuzz
tools/stdlib-coverage.sh Sable.Core
```

- Run from the repository root: tests find their data from there.
- A library test module is run in three modes: default,
  `SABLE_GENERICS=unspecialised`, and `SABLE_CHECK_SHARED=1` for anything that
  stores references (docs/02 §2.7).
- After changing `src/Sable.Syntax/Syntax.xml`, regenerate with
  `dotnet run --project tools/Sable.Syntax.Generator`.
- Before calling a change done, run unfiltered the CodeGen test classes it
  can touch and every test whose expected text or messages it changed (grep
  `tests/` for the old text). Selected tests alone have let red changes in.
  `tools/affected-tests.py` prints which classes, library modules (in the
  three modes), doc examples and coverage a branch's change reaches, and
  `--run` runs them: start there rather than with a whole project, and add
  what it cannot know (the old text's tests).
- Native tests (`SABLE_TEST_NATIVE=all`) build the program and compare it with
  the SIL interpreter; both must agree.
- The machine may be shared: `nice`, `dotnet build -m:2`, one test run at a
  time, never the fuzzers except short bounded smoke runs.

## Tests

- New style (#107): `expect`, `require`, `expectThrows`, cases with
  `@test(arguments:)`, fixtures, `forAll`. `tests/lib/Sable.Text` is a good
  model.
- **No test may depend on the clock, the network or scheduling.** Use a
  `TestClock` for time; open gates (channels) in the order wanted instead of
  sleeping for an order; retry a bounded number of times where an external
  process must catch up. Loopback only, never the Internet.
- What a runner may lack (root, a pty, multicast, a time zone database,
  `sh`) is a `skip(if:)` with the reason, not a silent pass.
- Each library type has tests, doc comments with ```sable examples, and
  coverage of at least 85% of lines and 70% of branches
  (`tools/stdlib-coverage.thresholds` lists the exceptions, each with a
  reason).

## Benchmarks

- `benchmarks/library/<name>/` has the `@bench` program and its ports:
  `.cs` (.NET), and `.rs`/`.c` where those languages have an equal.
  `benchmarks/README.md` says how.
- The target is .NET at least, ideally Rust, in time **and memory**
  (`tools/bench/compare.py` prints both tables). Library code should not
  allocate more than .NET's without a reason.
- Judge by instruction and allocation counts (`sable bench --counters`) on a
  loaded machine, and by the Benchmarks CI table; no change may make a
  benchmark slower, bigger or allocate more.

## The library's interface

New library code must stay usable from the library's interface
(docs/05 §5.4): a generator, `lazy`, `comptime`, inline layout, hidden `some`
or an attribute of its own in `lib/` makes the whole library fall back to
source. `LibrarySymbolsTests`, `LibrarySourceTests` and
`BuildCacheTests.AWarmBuildParsesNoFileOfTheStandardLibrary` catch it.

## Platforms

Runtime C must build and behave on macOS, Linux and Windows, and report
unsupported on WASI, even if only macOS is at hand; CI is the first to run the
others. Mistakes that have broken CI before:

- Windows headers define macros (`small`, `hyper`, `near`, `far`,
  `interface`, `ERROR`, `min`, `max`…): include them where nothing else follows
  or `#undef` after.
- `fopen` with `"w"`/`"r"` is text mode on Windows: use `"wb"`/`"rb"`. Read
  a child's output as UTF-8 explicitly.
- Paths join with `\` on Windows: build keys and compare through `FilePath`,
  not by re-parsing text as POSIX.
- WASI is wasm32: pointers are 4 bytes, so layout assertions are per pointer
  width.
- Every function a C file calls is declared in `platform.h` or before use:
  the runtime is compiled as one unit, in a fixed order.

## Language habits

- `_ = expr` discards a value; `let _ = expr` is a lint warning.
- An `if let`/`while let` binding ends with the statement or the loop turn.

## Commits and issues

- No `Co-Authored-By` lines in commit messages.
- `Fixes #N` only when every "Done when" item of the issue is truly met;
  otherwise `Part of #N`.
- A report or issue says what was not run or not verified, plainly.
- The issues are the roadmap: #1 (standard library parity) and #328 (what
  the Vixen port needs).

## CI

`ci.yml` (every platform, sharded CodeGen tests, WASI, the VS Code extension,
the NumPy conformance job) runs on every push to master and every pull
request. `coverage.yml` (the thresholds), `benchmarks.yml` (the comparison
tables) and `fuzz.yml` run nightly at 04:00 UTC on master. All can be
dispatched on a pushed branch (`gh workflow run <file> --ref <branch>`)
before merging to master: dispatch coverage for library changes and
benchmarks for changes that can move them, rather than waiting for the night.
