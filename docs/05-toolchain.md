# 5. Toolchain and Package Manager

One binary: `sable`. It builds, tests, formats, documents, resolves, publishes,
and serves the IDE. Cargo is the model, with Go's "no configuration options for
the formatter" discipline and .NET's workspace/solution ergonomics.

---

## 5.1 The CLI

```
sable new <name> [--lib|--bin]     scaffold a package
sable init                         scaffold in the current directory
sable build [--release] [--target] compile
sable run [-- args...]             build and execute
sable test [filter] [--filter expr] [--doc] [-j n] [--timeout s] [--coverage]  run @test functions and doc examples
sable bench [filter] [--filter expr] [--compare ref] [--job how] [--json f]  run @bench functions; compare with a baseline, a commit or another build
sable fuzz [target] [--duration 8h] fuzz a @fuzz function with libFuzzer; --replay, --minimise
sable check                        type-check only, no codegen (fast)
sable fix [--check]                apply the fixes the lints suggest
sable fmt [--check]                format; no options, ever
sable doc [--open] [--json]        generate documentation + symbol graph
sable add <pkg>[@<ver>] [--dev]    add a dependency, update the lock
sable remove <pkg>
sable update [pkg]                 re-resolve within semver bounds
sable tree                         print the resolved dependency graph
sable publish [--dry-run]          publish to the registry
sable search <query>               search the registry
sable login / logout               keep or forget a network registry's token
sable owner [<pkg>] [--add|--remove <key>]  who may publish a package
sable install <pkg>[@<ver>]        build a package's binaries into ~/.sable/bin
sable cache <list|prune|clean|warm> the package and build cache: list, collect, fill
sable registry <serve|token>       run a network registry, issue its tokens
sable clean                        remove the target directory
sable bindgen <header.h>           generate .sbi from a C header
sable repl                         interactive, backed by the const evaluator
sable lsp                          language server (stdio)
sable explain <S1234>              print the full documentation for a diagnostic
sable toolchain <install|list|pin>  manage toolchain versions
sable completions <bash|zsh|fish>  print a shell completion script
sable help <command>               a command's arguments, as <command> --help
```

Every command also takes, before or after its name: `-q` (no status lines),
`-v` (also the commands run), `--color auto|always|never` (`auto` honours
`NO_COLOR`), `--message-format human|json` (json: each diagnostic as one
object a line on standard output — `severity`, `code`, `message`, `file`,
`line`, `column`, `rendered` — for editors and CI), `-j <n>` and
`--offline`; and `sable -V` prints the version. None of them changes what a
build produces.

`sable test` runs each test in a process of its own, as many at once as there
are processors (`-j`). What `@test` may say of one:

```sable
@test(arguments: [("1", 1), ("-7", -7)], tags: ["fast"], timeout: .seconds(5))
fn parses(_ text: String, _ value: Int) throws { expect(try Int.parse(text) == value) }
```

- `arguments:` runs the test for each element of an array of what it takes —
  tuples, for several parameters — or, given an array for each parameter, for
  each pair. Each case is a test: reported, timed, stopped and selected on its
  own, as `Module.parses("1", 1)`. (The arrays are worked out as the tests are
  listed; a case is named for its arguments as Debug writes them, a second one
  of the same name with `#2` after it.)
- `tags:` are what `--filter` may ask for it by, and what the reports carry.
- `timeout:` stops it when it runs longer, and fails it, whatever `--timeout`
  says of the rest: `.milliseconds`, `.seconds` or `.minutes` of a literal.
- `traps`, or `traps: "text"`: it passes by trapping.

A name, or part of one, selects tests (`--exact` all of it; a function's name
is then all its cases). `--filter` takes an expression: `tag:`, `name:` and
`file:` terms — a bare word is a name's — put together with `&`, `|`, `!` and
parentheses, `*` standing for any text: `--filter 'tag:fast & !name:*Slow*'`.

`--junit <path>` and `--trx <path>` write the results as JUnit's XML and as
Visual Studio's TRX, for a CI to show. `--seed <n>` makes the property tests'
cases from a seed, as a failure gives it (SABLE_TEST_SEED does too). `--bless`,
or SABLE_UPDATE_SNAPSHOTS=1, writes the snapshots the tests compare with. The
editor's Test Explorer shows each case under its function. What a test is
written with is Sable.Testing's (docs/06 §6.4).

`sable test --coverage` counts what of the package's own code (not
`tests/`, dependencies or the library) the tests run: each line, and each
outcome of every decision — both ways of an `if`, `guard`, `while` (but a
literal `while true`, which decides nothing) and `?:`; each arm of a `match` and case of a `switch` (and none, without a
`default`); the right side of `&&` and `||` run or skipped; an `if let`
holding a value or not; `??` and `try?` giving the value or the fallback.
It prints the share of lines and of branches run per file, the lines never
run, and each decision some of whose outcomes never happened
(`12:5 'if' never: true`), and writes `target/coverage/lcov.info` — `DA`
records for lines, `BRDA`/`BRF`/`BRH` for branches, numbered by decision
within a line — which genhtml and editors read. The counters go into the
code before the optimiser, so `--release` counts the same, and the SIL
interpreter counts as native code does. Code that was never lowered — a
generic function no test instantiates — has no code to count in, so its
statements are reported as lines not run (their branches are not counted).

`--cover <module>` counts in a standard library module instead of the
package: `Sable.Collections`, `lib/Sable.Collections` or one file,
`lib/Sable.Collections/Stack.sb`, named in the report and the LCOV as the
library embeds them (`lib/<Module>/<File>.sb`). `--lcov <path>` writes the
LCOV elsewhere, and a directory given in place of a file is every `.sb`
file under it, built as one program — so the library's tests are run as
`sable test --cover lib/Sable.Collections tests/lib/Sable.Collections/`
(what `tools/stdlib-coverage.sh` does, CONTRIBUTING.md).

`sable bench` builds the `@bench` functions, at `-O2` as `sable build` does
(`-O0` to `-Oz` for another level), and runs each in a process of its own.
They are selected and discovered as tests are: names, `--filter` with
`TestFilter`'s expressions, `@bench(tags:)`, `--list`, and `--describe`'s lines
for a tool. How a benchmark is measured (`Sable.Testing.BenchSchedule`; docs/06
has the API):

1. *Pilot.* The body is run once, then twice, four times ..., until that takes
   an eighth of a sample's time (`--sample-ms`, 10 ms): how many iterations
   make a sample.
2. *Warm-up*, for `--warm-up-ms` (100 ms), and on until two runs have been no
   faster than the one before, for at most three times that.
3. *Overhead.* An empty body is run as often, five times, when an iteration
   takes under a microsecond; the median is taken from every sample.
4. *Samples.* 15 at least and 100 at most, until half the mean's 99.9 %
   confidence interval is within 2 % of it, or `--budget-ms` (3 s) is up;
   `--samples n` asks for a number.
5. *Memory.* A run with allocations, bytes, retains and releases counted.
6. *Counters* (`--counters`): a run with the processor's instructions and
   cycles read either side. macOS gives them to any process, of itself; Linux
   through `perf_event_open`, where `perf_event_paranoid` is 2 or less and the
   machine has them — a container or a CI's virtual machine seldom has;
   Windows gives cycles alone without privileges; WASI none, nor processor
   time.

The report is the mean and half its confidence interval (Student's t), the
median, the samples that count and their iterations, and what an operation
allocates. Samples above Tukey's upper fence (the third quartile and one and a
half interquartile ranges; quartiles interpolated, R's type 7) are left out;
`--outliers` says otherwise. `--metric processor` is processor time, of every
thread and of the processes waited for, in place of the clock's.

`--save-baseline <name>` keeps the samples, and `--baseline` compares with
them. `--compare <git ref>` builds the benchmarks as they were at a commit
(`git archive`, so nothing of the working tree is touched) with this compiler,
and `--job <name>=<words>` builds or runs them another way: a level,
`-generics=unspecialised`, `--exclusivity=...`, `SABLE_WORKERS=1`, `ref=...`,
or `sable=<path>` for another compiler. Jobs are run in turn, launch by launch
(three of each), so that what the machine does is done to all of them, and
each is compared with the first. The test is **Mann-Whitney's**, on all the
samples, rather than Welch's t: a benchmark's samples have a floor and a tail
of interruptions, so they are not normal, and one far sample moves a mean and
hardly a rank. A benchmark *regressed* when p < 0.001 — exactly for up to 20
samples each without ties, else by the normal approximation — and its median
is slower by more than `--threshold` (5 %): where the linker puts code moves a
time by a few per cent, and samples of one process share that, so a small
difference may be certain and mean nothing. Only `--fail-on-regression` makes
the command exit 1 for one. What a benchmark computed (`Bencher.check`) must be
the same in every process and job; if not, the run fails.

`--markdown`, `--csv` and `--json` write the results; `--history <path>` adds
a row for each to a tab-separated file: the date, the commit (with `+` for
uncommitted changes), the benchmark, its median in milliseconds an operation,
and its allocations, retains and releases. `--sil` and `--asm` print what the
benchmarks were compiled to, each and its closures as functions of their own
(what is inlined into them with them), in place of a run.

`sable fuzz` fuzzes a `@fuzz` function — one that takes the fuzzer's bytes,
`U8[:]`, or up to four values made of them, and fails by trapping or throwing
(S3104 says what else it may not be) — of the package (its library, `tests/`
and `benches/`) or of the files named (a directory is those in it). The program
is built as a library: the compiler writes a harness whose `@export` functions
take an input a byte at a time and run the target on it, and a C shim is
libFuzzer's entry point. clang compiles the IR, the runtime and the shim with
`-fsanitize=fuzzer,address,undefined`: SanitizerCoverage counts every edge of
the Sable code with no rewriting. libFuzzer runs in fork mode (`-j n`
processes), so a failure does not end the run: until `--duration` is up or
Ctrl-C, it keeps the inputs that reach new code in
`target/fuzz/<Module.function>/corpus/` (seeded from the package's
`fuzz/<Module.function>/`, if there is one, and each `--seeds` directory) and
those that fail in `crashes/`. Then each failure is replayed, minimised
(`.min`), its report kept (`.txt`), and the failures grouped by what they say —
a trap's message and place, a sanitizer's summary, a hang — into
`summary.txt`; the command exits 1 while a saved input still fails. Several
targets are fuzzed one after another, each for the whole duration; `--runs n`
tries so many inputs in one process that stops at the first failure.
`--replay [input...]` runs the saved inputs (or those given) once each, and
`--minimise <input>` shrinks one; `--list` lists the targets; what follows
`--` goes to libFuzzer. libFuzzer's runtime comes with LLVM's clang; Apple's
has none, so on macOS fuzzing says so (and how to get an LLVM clang) before it
builds anything, and `--replay` — built with its own `main` and the
sanitizers — is what runs there.

*Values.* A target may take parameters of any types that conform to
`Sable.Testing.Arbitrary`, `@derive(Arbitrary)`'s among them. They are made of
the input by `Choices(bytes:)`, the choices property tests make values of:
each choice reads as many bytes as its limit needs, so that an input is still
bytes, as is every entry of the corpus, and a change of a byte is a change of
a choice. A trap says what the target was called with.

*Dictionaries and mutators.* `fuzz/<Module.function>.dict`, or `--dict`, is a
libFuzzer dictionary of tokens put into inputs whole; `--dict sable` is the
language's own, from the lexer's tables (`TokenDictionary`), for a target that
reads its source. `@fuzz(mutator: f)` names a function of the module,
`f(_ input: U8[:], _ seed: U64) -> U8[>..]`, that makes another input of one:
three inputs in four are its, the others libFuzzer's own. The mutator for Sable
source edits whole nodes of the syntax tree — replaces one by another that can
stand there, deletes, copies, trades, wraps or unwraps it — and is written in
C# (`SyntaxMutator`), where the compiler is: a function of an input and a seed
and nothing else, so that the compiler written in Sable has it as a
`@fuzz(mutator:)` function. Until then the compiler's C# fuzzers use it
(CONTRIBUTING.md).

*Sanitizers.* AddressSanitizer instruments the Sable code as well as the C:
each function of the program's IR is marked `sanitize_address`, and the
runtime takes every block from malloc, not from its slabs, where ASan would
see neither a read past a block's end nor one after it was freed. UBSan is
made by clang's C front end, from C's types, and has nothing to add to IR: it
covers the runtime and the shim, and Sable's own checks trap on what it would
look for. Nothing is suppressed: no function is left out and no report
ignored. (The leak check is the runtime's, not ASan's.)

*Saved inputs are tests.* `sable test` runs every input `sable fuzz` kept, and
the seeds, once: each failure of a target is a test, `Module.target(crash-…)`,
and its corpus one more, `Module.target(corpus)`, the smallest first and no
longer than 20 seconds (2,000 inputs, 4 MB). A target with none has no test.
They are tagged `fuzz`, and `--coverage` counts what they run.

*One instrumentation or two.* `sable test --coverage` counts in SIL, before
any pass: lines and the outcomes of decisions, by the names the source has for
them, in the interpreter as in native code, on every platform, for generics
once however often they are specialised. libFuzzer's edges are counted in LLVM
IR, after passes have merged and split blocks, by a pass and a runtime only
clang has; no edge says which outcome of which `if` it is. Each is the wrong
counter for the other's job, so they stay two, and what is shared is the
corpus: `sable test --coverage` reports the lines and branches that the inputs
fuzzing found reach.

`sable bindgen` writes Sable source, not `.sbi` (which waits on the
interface file format of §5.4): a module of `@extern("c")` functions,
structs and integer constants, read from the header by clang itself. C's types
are `Sable.Interop`'s (`CInt`, `CLong`, `CSize`), so the module is the same on
every platform; a pointer is a `RawPtr` — of an empty enum, where C only names
the struct — a function pointer a `CFunction`, `...` is `CVarArg...`, and a
function given a pointer is `unsafe` (docs/06, "Calling C", has an example). What
cannot cross — a union, a struct with an array or a bit-field in it, by value;
`va_list` — is listed in a comment. `sable toolchain` installs
toolchains side by side in `$SABLE_HOME/toolchains/<version>` from a build
(`--from <directory or archive>`; there is no release server yet), and `pin`
writes `sable-toolchain` at the workspace root, after which the `sable` on
the path hands every command there to the pinned toolchain — or stops,
naming how to install it.

Design rules:
- **No global state that changes the result of a build.** No environment
  variable may alter the output. The lock file plus the manifest plus the
  toolchain version fully determine the artifact.
- **`sable check` is the fast path** and must stay fast: it is what the editor
  and pre-commit hooks run.
- **Every command works offline** once the lock file is satisfied by the cache.

---

## 5.2 The manifest: `package.sb`

> **Status.** Built: `sable` reads `package.sb` and `workspace.sb`, and `sable
> new`, `init`, `add` and `remove` write them. §5.2.10 lists what is not built
> yet.

A package is described in Sable, in a file named `package.sb` at its root, as
Swift describes one in `Package.swift` and Gradle in `build.gradle.kts`, and
not in a second language. The file is a program, but a restricted one: it is
**evaluated at compile time**, it may not touch the machine it runs on, and
its result is plain data that the toolchain caches and every other tool
reads. So it has a language's reach — `if`, loops, functions, shared values,
completion and type errors in the editor — and a data file's guarantees:
the same manifest means the same package on every machine, and reading it
never runs anything that could fail differently tomorrow.

### 5.2.1 Why not TOML, XML, or a build script

| | Data file (Cargo's TOML) | MSBuild (`.csproj`, `.sln`) | Build script (Gradle `.kts`, `build.zig`) | `package.sb` |
|---|---|---|---|---|
| Conditions, reuse, computed values | No: every repetition is written out | Yes, as string-typed conditions and properties evaluated in phases | Yes, the whole language | Yes, the whole language at compile time |
| Editor help | A JSON schema at best | Some | The language's own | The language server's: completion, hover, go-to-definition, type errors |
| Same result on every machine | Yes | Only by discipline | Only by discipline | Yes: no I/O, no clock, no environment, no host conditions (§5.2.4) |
| Readable without running code | Yes | No: evaluation is MSBuild | No: configuration time is a build step | Yes: the evaluated form is cached and published (§5.2.6) |
| Edited by `sable add` | Yes | Yes | No, in general | Yes, where the list is literal (§5.2.7) |

The flexibility of a `.csproj` is MSBuild's, a full build language written in
XML, and that is its hard part: properties and targets evaluated in phases,
conditions as strings, imports changing behaviour at a distance. `.sln` was
replaced by `.slnx` for being unreadable. A build-script manifest (Gradle,
Zig) gives the reach but must be *run* to be read: configuration time is the
cost Gradle is known for, and every tool that wants a dependency list needs
the compiler. `package.sb` takes the language and leaves the running.

### 5.2.2 The file

```sable
// package.sb
import Sable.Build

/// A renderer with optional GPU back ends.
let manifest = Package {
    name: "render-engine",
    version: "0.4.2",
    edition: 2027,
    authors: ["..."],
    license: "MIT OR Apache-2.0",
    repository: "https://example.com/render-engine",

    targets: [
        .library(path: "src/lib.sb"),
        .executable("render", path: "src/main.sb"),
    ],

    dependencies: [
        .registry("sable-math", "1.2"),
        .registry("image", "0.6", features: ["png", "jpeg"], defaultFeatures: false),
        .path("platform", "../platform"),
        .git("shader-tool", "https://example.com/st", tag: "v2.1.0"),
        .registry("vulkan-sys", "1.3", optional: true),
        .registry("metal-sys", "0.9", optional: true, when: .os(.MacOS)),
    ],
    devDependencies: [.registry("bench-harness", "0.3")],

    features: [
        "default": ["vulkan"],
        "vulkan": ["dep:vulkan-sys"],
        "metal": ["dep:metal-sys"],
        "debug-gpu": ["vulkan-sys?/validation"],
    ],

    profiles: [
        .release(optLevel: 3, lto: .fat, codegenUnits: 1, panic: .abort,
                 arcChecks: false, exclusivity: .off, overflowChecks: false),
        .dev(arcChecks: true, exclusivity: .checked, leakCheck: true),
    ],


    build: .script("build.sb",
                   allowExec: ["pkg-config", "glslc"],
                   allowRead: ["$OUT_DIR", "shaders/"],
                   allowWrite: ["$OUT_DIR"]),
}
```

- There is no `module` line: each manifest is a module of its own, so the
  manifests of a workspace's members never clash, and no package's code can
  see one.
- The file declares exactly one top-level `let manifest: Package`
  (`package` is a keyword: the workspace access level). Other
  top-level `let`s and functions are allowed and are the point: a list of
  backends mapped to features, a version shared between targets.
- Everything comes from `Sable.Build`, a library module shipped with the
  toolchain (§5.2.8), and from the pure parts of `Sable.Core`. Nothing else
  can be imported: not the package's own code, not its dependencies — they
  are what the manifest decides.
- `.git(name, url)` takes one of `tag:`, `branch:` or `rev:` (a commit),
  else the repository's default branch, and optionally `version:` for when
  the package is published. Any URL git fetches from works (`https://`,
  `ssh://`, `file://`, `git@host:path`); credentials are git's own. The
  repository is fetched into `$SABLE_HOME/cache/git/`, and the package is
  looked for at the top of the repository, then in directories below it,
  so one repository can hold several. `sable.lock` pins the commit
  (`source = "git+<url>?tag=v2.1.0#<commit>"`); a moving branch stays at
  that commit until `sable update <name>`, or until the manifest names
  another reference. A pinned commit already checked out builds offline.
  A package from git is, like a registry package, a workspace of its own,
  and a published package cannot depend on git alone.
- `.library(path:, native: .staticLibrary)` (or `.dynamicLibrary`; in TOML,
  `[lib] kind = "static"` or `"dynamic"`) also builds the library as one C
  links: `sable build` writes `libname.a` or `libname.so`/`.dylib`
  (`name.lib`, `name.dll` on Windows) to `target/<profile>/`, named for the
  package with `-` made `_`. It holds the library, its dependencies and the
  runtime, compiled whole with no entry point; C calls its `@export`
  functions. As it is loaded, a constructor starts the runtime, takes the
  process's arguments (where the C library passes them to constructors:
  Apple's and glibc) and gives its globals their initial values; the runtime
  destroys them as the process exits, before its leak check. On Windows it
  is compiled for the DLL C runtime, as `/MD` is, and a static library also
  holds compiler-rt's builtins, which Microsoft's toolchain lacks. A single file
  is built so by `sable build file.sb --emit staticlib` (or `dylib`). A
  package's library is built as a binary is (§5.4, "As built"): each package
  a unit, cached, the package's own unit setting up every global as the
  library loads. A shared library is the units' objects linked `-shared`; a
  static one is them joined by a relocatable link (`-r`, with ThinLTO where
  they are bitcode) into one object, archived with the runtime, so that a C
  program taking any of it takes all of it. Where the profile compiles whole
  (`lto = "fat"`), and for a static library on Windows, whose linker makes
  no relocatable object, the library is compiled whole.
- Field names are the TOML keys in Sable's spelling (`defaultFeatures`,
  `optLevel`); every table of the TOML format has a field. Enumerated
  settings are enum cases (`.fat`, `.abort`), so a misspelling is a type
  error, not a silently ignored string.
- What `sable publish` does with the package is said in it too:
  `readme: "README.md"` (a file inside the package, always published, an
  error at publish if it is missing); `keywords:` (at most five, lower-case,
  recorded in the registry's index and matched by `sable search` as the
  description is); `include:` and `exclude:`, globs of the files published
  (`*` within a name, `**` across directories, a pattern with no `/` a
  name at any depth, a directory standing for what is under it; with no
  `include`, every file; the manifest and the readme whatever they say);
  and `publish: false`, which `sable publish` refuses.

### 5.2.3 Workspaces: `workspace.sb`

The workspace root — .NET's solution, Cargo's `[workspace]` — is
`workspace.sb`, declaring `let workspace: Workspace`:

```sable
// workspace.sb
import Sable.Build

let sableMath: Dependency = .registry("sable-math", "1.2")

let workspace = Workspace {
    members: ["crates/*", "tools/shader-tool"],
    exclude: ["experiments"],
    shared: Shared {
        version: "0.4.2",
        edition: 2027,
        dependencies: [sableMath, .registry("image", "0.6")],
        profiles: [.release(optLevel: 3, lto: .thin)],
        lints: Lints { deny: ["unused_import"] },
    },
}
```

A member takes a shared setting with `.workspace`: `version: .workspace`,
`dependencies: [.workspace("image")]`. A `package.sb` cannot import its
workspace's file. Inheritance goes through these named references, so a
member stays readable on its own. (Not built yet: `Workspace` has `members`
and `exclude`; `shared` and `.workspace` are not in `Sable.Build` yet.
`sable.toml` has the inheritance, as Cargo writes it: `[workspace.package]`
and `[workspace.dependencies]` in the root, and `version.workspace = true` or
`image = { workspace = true, features = [...] }` in a member, which may add
features, `optional` and `when`; a shared path dependency is relative to the
workspace. A member that inherits cannot be published until it writes its
settings out, since a consumer has no workspace to take them from.)

A package that is also its workspace's root has both files in one directory.

`sable new` and `init` inside a workspace add the new package to its
`members`, unless a glob already covers it or `exclude` names it; where
`members` is computed rather than a literal list, they say what to add
instead of editing it.

### 5.2.4 What the manifest may do

The manifest is checked as ordinary Sable, then evaluated by the SIL
interpreter (the same one `sable repl` and the compile-time evaluator use)
under these rules. Each is an error at the line that breaks it, not a
surprise later:

- **No effects.** No file, network, process, clock, random or environment
  call. `Sable.Core`'s `Runtime` effects are not in scope, and
  `Sable.IO`, `Sable.Process`, `Sable.Time`, `Sable.Net` cannot be imported.
- **No host conditions.** `#if os()` and `#if arch()` are rejected in a
  manifest, because a manifest that branched on the machine evaluating it
  would give a different lock file on each one. Platform-specific choices are
  data that the resolver applies per target: `when: .os(.MacOS)`,
  `when: .target("wasm32-unknown-unknown")`, `when: .feature("metal")`.
- **Bounded.** A step and memory limit, as the compile-time evaluator has.
  A manifest that loops forever stops with an error naming the line it was
  on.
- **Total.** A trap — an index out of range, a failed `!` — is a manifest
  error with its location; there is no partial package.

Everything else is allowed: `let`, `var`, `if`, `match`, `for`, local and
top-level functions, closures, string interpolation, the collections.

### 5.2.5 Errors

A manifest's errors are compiler diagnostics with the file and line, shown by
every command and by the editor as it is typed:

```
error: package.sb:2: import: a manifest cannot import 'Sable.IO': it imports
       only Sable.Build, and may not read or write anything (docs/05 §5.2.4)
error: package.sb:3: #if: a manifest has no conditional compilation: what
       depends on the platform is data the build applies, such as
       `when: .os(.Linux)` on a dependency (docs/05 §5.2.4)
error: package.sb:2: effect: a manifest cannot use 'Runtime': it may not read,
       write or wait, so that it means the same on every machine
error: package.sb:11: manifest: evaluating it took more than 20,000,000 steps:
       a manifest is data, and should be quick to compute
```

Type errors are the compiler's own, at their line; a value the package model
refuses (a version that is not semver, a feature naming nothing) is reported
against the manifest with the setting's path (`package.version`).

### 5.2.6 Evaluation, caching and what other tools read

1. `sable` parses `package.sb`, checks the rules above, compiles it with
   `Sable.Build` and a `main` that prints `renderManifest(manifest)`, and runs
   that in the SIL interpreter. What it prints is the **canonical TOML** —
   the `sable.toml` format — which the package model reads and validates as
   it always has. The result is cached by content in
   `$SABLE_HOME/manifests/<hash>.toml` (default `~/.sable`), keyed by the
   manifest's text, `Sable.Build`'s, and the toolchain build: evaluated once,
   then read.
2. The resolution cache hashes every manifest file of every local package, so
   a changed `package.sb` re-resolves, as an edited `sable.toml` did.
3. **Published packages carry the evaluated form.** `sable publish` puts the
   canonical `sable.toml` in the archive in place of `package.sb`, and the
   registry, the resolver and any third-party tool read only that. Nothing
   outside the toolchain ever needs to run Sable to learn what a package
   depends on, and anything that already reads `sable.toml` keeps working.
4. A dependency's `when:` is kept as data (`when = "os(macos)"`): the resolver
   and the lock file keep it on every machine, and only the build leaves out
   what does not apply to the target.
5. The language server compiles each manifest in a module of its own, so the
   editor has completion, hover and type errors in `package.sb`. It reads the
   packages through the toolchain, evaluating their manifests, and compiles
   each package as `sable check` does: with its default features and its
   dependencies. The effect rules' errors are not shown in the editor; a
   manifest that breaks one leaves its package's files loose, and says why in
   the server's log.

### 5.2.7 `sable add`, `remove` and `update` edit the source

The syntax tree is lossless, so editing a manifest preserves its comments and
layout. `sable add image@0.6` finds the `dependencies:` field of the
`Package { }` record and appends `.registry("image", "0.6")` to its array
literal, laid out as the list is: on its own line, indented like the others,
in a list of lines; after a comma in a list on one line. An entry for the same
package is replaced where it is; a missing field is added at the end of the
record. `sable remove` takes the entry out, with its line. When the
list is not a literal (computed by a function, or concatenated), `sable add`
does not guess: it prints the entry to add and the line of the expression that
builds the list.

### 5.2.8 `Sable.Build` and editions

`Sable.Build` is part of the toolchain, not a dependency, and is versioned
with it. Its API only grows: a field or case is added with a default, never
removed or changed, except at an edition boundary, where the old spelling
keeps working for manifests of the old edition. This is what Swift needs
`// swift-tools-version:` for; here `edition:` serves, and a manifest using a
field newer than the running toolchain gets an error naming the toolchain
version that has it.

### 5.2.9 `sable.toml`

`sable.toml` is the canonical form a manifest evaluates to, and it is still
read where it is found: in a published package, which carries it in place of
`package.sb`, and in a directory where someone wrote it by hand. There is no
migration step; `sable new` and `init` write `package.sb`, and a directory
with both kinds is an error, so there is never a question which one applies.

The driver reads it, and `sable.lock`, with a TOML reader of its own
(`src/Sable.Driver/Packages/Toml.cs`) and not with the library's
(Sable.Serialize's `Toml`). The plan to use the library's is dropped, for what
it was to bring has gone and what it would cost has not:

- No manifest is edited as TOML. `sable add` and `remove` edit `package.sb`,
  through the syntax tree (§5.2.7), and what a manifest evaluates to is written
  anew; that was what a parser that keeps comments was for.
- The driver is C#, so the library's parser would run in the SIL interpreter:
  bound, lowered and run for each manifest and lock file read, where all that
  is wanted is a table. A manifest must be read before there is a program to
  compile, by the language server as it starts and for every package of a
  graph, archives included.
- The C# reader is what a compiler written in Sable would not bring along: it
  would use the library's.

What matters is that the two never read one file differently.
`TomlAgreementTests` holds them to the same texts being TOML and to the same
values.

### 5.2.10 Not built yet

- Workspace inheritance in `package.sb` and `workspace.sb`: `shared` and
  `.workspace` (§5.2.3); `sable.toml` has it.
- Runtime profiles (docs/06 §6.1): every build links the whole runtime,
  and there is no setting for it — `[targets.<triple>] runtime` is an
  error, as a smaller runtime asked for and not given would be. The
  per-target setting there is is built: `reflection = false`
  (`targetSettings: [.target("wasm32-wasi", reflection: false)]` in
  package.sb) builds for that target as `--deny-reflection` does, the
  triple matched by its operating system and architecture. `when:` covers
  `os()` and `arch()` only. `sable build --target <triple>` builds for
  another machine, and `when:` is tested against that target.
- The language server showing a manifest's evaluation errors (the effect
  rules, a trap) in the editor; it type-checks manifests, and logs the rest.
- A manifest's `const`s and `comptime` blocks follow the manifest's rules
  rather than ordinary code's: the manifest evaluator runs the interpreter
  directly, under its own rules (§5.2.4).

### 5.2.11 Build profiles

`[profile.dev]` is every build but `--release`, `[profile.release]` that
one; `package.sb` writes them as `.dev(...)` and `.release(...)`. A value
the compiler cannot use is an error naming the key; a setting it cannot
act on is a warning the build prints, never taken silently.

A package may have profiles of its own, as Cargo's custom profiles:
`[profile.profiling]` with `inherits = "release"` (or `"dev"`) starts from
that profile's settings as the manifest has them and changes what it
writes; `.named("profiling", inherits: .release, debug: true)` in
package.sb. `sable build --profile profiling` (and `run`, `check`,
`debug`) uses it, and the build goes to `target/profiling/`. A name is
lower-case, and not one of target/'s own directories (`check`, `doc`,
`harness`, `coverage`, `bench`, `generated`); a profile of the package's
own that inherits nothing, or `--profile` naming one there is not, is an
error.

| Setting | Default (dev / release) | What the build does |
|---|---|---|
| `opt-level` | `0` / `3` | clang's `-O0`…`-O3`, `-Os`, `-Oz`, and the SIL passes below |
| `debug` | `true` / `false` | DWARF (`-g`) at any level; optimised, the SIL passes still run |
| `lto` | `"thin"` | `-flto=thin` or `-flto` in an optimised build: `"thin"` compiles each package apart, cached, and inlines across them as they link; `"fat"` compiles the program whole; the runtime compiled to bitcode with it so that its small functions inline; on Linux and Windows through lld, and without, where clang cannot link with lld (a warning when the profile wrote `lto`) |
| `codegen-units` | — | `1` compiles the program whole even in a dev build; the units are otherwise the packages, so another count is warned about |
| `incremental` | `true` | `false` compiles the program whole each time, keeping no unit's object; an unchanged build is still skipped |
| `panic` | `"abort"` | a trap always aborts; `"unwind"` is warned about |
| `strip` | `"none"` | `"debuginfo"` or `"symbols"`: the link's `--strip-debug`/`--strip-all` on Linux, `-S`/`-S -x` on macOS; a warning on Windows |
| `rpath` | `false` | the executable looks for shared libraries beside itself (`$ORIGIN`, `@executable_path`); a warning on Windows, which does already |
| `exclusivity` | `"checked"` / `"unchecked"` | keeps or drops the dynamic exclusivity checks (docs/02 §2.3) |
| `overflow-checks` | `true` | `false`: integer `+ - *`, negation, `abs` and `as` wrap rather than trap |
| `arc-checks` | `true` / `false` | compiled code's retain and release trap on an object whose count is already zero — whose deinit has run — rather than count freed memory |
| `debug-assertions` | unoptimised | `DEBUG` rather than `RELEASE` for `#if` (SYNTAX.md §21) in every package's files |
| `leak-check` | `false` | `sable run` reports what the program leaves alive |

The SIL passes each opt-level runs (`SilPassManager.ForLevel`):

| Level | SIL passes |
|---|---|
| `0` | the ARC passes (slot promotion, copy forwarding, borrowing, stack promotion); none at all with debug information, so every local stays in its slot |
| `1` | devirtualisation and block merging, then the ARC passes: nothing inlined or duplicated, every call a frame |
| `2`, `3` | all of them: devirtualisation, speculative devirtualisation (a vtable call that can reach one or two small overrides tests for each and inlines it), inlining, the ARC passes, loop versioning; `3` differs in LLVM's passes only |
| `s` | as `2` without loop versioning, which copies a loop for its fast path, or speculative devirtualisation, which copies a call for each guess |
| `z` | as `s` without the inliner, which copies a callee into every caller |

An optimised program compiled package by package (`lto = "thin"`, the
default, or off) runs the opt-level's passes on each unit: the inliner
copies in what the unit has the body of — its own functions, and the
instances of generic ones it uses — and ThinLTO inlines across units as
they link; devirtualisation needs the whole program, and leaves a unit
alone. A dev build with debug information, compiled so, runs
none. A single file's `sable build` takes the same levels as `-O0` to
`-O3`, `-Os` and `-Oz` (`-O2` by default), and `-g` alone builds at `-O0`.

The opt-level is the program's own units'. The libraries it links — the
standard library's unit and each registry or git dependency's — are
compiled optimised whatever it says: an unoptimised build compiles them at
`s` (the SIL passes and clang's `-Os`), with the build's debug information
and every check its profile asks for (§5.4, "Libraries compiled
optimised").

### Deliberate departures from Cargo

- **No `reflect:`.** What a program reflects on is said in its code:
  `@reflectable`, or `extension T: Reflectable {}` for another package's
  type, and reflection is generated where a program asks for it (docs/06
  §6.3). An earlier design had the manifest list types to keep for
  reflection; with nothing looking a type up by name, the list had nothing
  to do, and `[reflect]` is an error.
- **No `categories`, `homepage` or `documentation`.** The registry records
  a package's description, repository and keywords; the others are warned
  about and ignored.
- **Memory-model knobs in profiles** (`arcChecks`, `exclusivity`,
  `leakCheck`) — these are the ones programmers will actually want to toggle.
- **`edition` is a year, and editions may change syntax** but never ABI. A
  package declares its edition; packages of different editions interoperate.
- **The manifest is Sable**, evaluated before the build and published
  evaluated (§5.2.1).


---

## 5.3 Resolution

**PubGrub**, not the naive backtracking resolver. It is the correct algorithm
and, more importantly, it produces an explanation when resolution fails:

```
error: cannot resolve dependencies

  Because render-engine depends on image ^0.6 and image 0.6.x depends on
  color-space ^2.0, render-engine requires color-space ^2.0.

  And because platform 1.4.0 depends on color-space ^1.8 and no version of
  color-space satisfies both ^2.0 and ^1.8, render-engine and platform 1.4.0
  are incompatible.

  So, because the root package depends on both, resolution failed.

help: platform 2.0.0-beta.1 depends on color-space ^2.0
      try: sable add platform@2.0.0-beta.1
```

Rules:
- Semver, with `0.x` treated as `0.x.*` (breaking at minor).
- **One major version of a package per graph** — no two copies of the same
  major, because protocol conformance coherence requires it. This is a real
  constraint that Cargo does not have and it needs a clear diagnostic.
- Lock file is committed for binaries, not for libraries.
- Feature unification across the graph, with `dep:` and `pkg?/feature`
  syntax copied from Cargo because it is correct.

---

## 5.4 Build graph and incremental compilation

### Units

The compilation unit is the **module**, not the file. Modules within a package
form a DAG (cycles are an error); packages form a DAG.

### Query-based incrementality

A demand-driven query engine (Salsa / `rustc` query model):

```
  file_text(path)              → string
  parse(path)                  → SyntaxTree
  module_decls(module)         → DeclarationTable
  resolve(symbol_ref)          → Symbol
  type_of(decl)                → Type
  sil_for(function)            → SilFunction
  object_for(module)           → ObjectFile
```

Each query memoises on a fingerprint of its inputs. A change to a function body
invalidates `sil_for` and `object_for` for that function only; a change to a
signature invalidates its dependents. **Red-green marking** avoids re-running a
query whose inputs changed but whose result did not — the single most important
optimisation in the engine.

The on-disk cache lives in `target/<profile>/cache`, keyed by
`(toolchain, profile, target, fingerprint)`.

### Separate compilation and `.sbi`

Each module emits an interface file containing:

- Public and `package` declarations with full signatures
- Generic bodies eligible for cross-module specialisation
- `@inline(always)` bodies
- **Escape summaries** (the key one — see `02-memory-model.md §2.2`)
- Which public unions are `@frozen`, and their cases
- Conformance records

`.sbi` is a binary format with a stable version header, designed so that a
change to a private implementation detail does not change the file, and
therefore does not invalidate downstream modules.

### Parallelism

- Package-level: parallel across the dependency DAG.
- Module-level: parallel within a package once signatures are resolved.
- Function-level: SIL passes and IRGen are embarrassingly parallel.
- Target: linear scaling to 16 cores on a 100-module build.

### As built (Phase 5)

The first implementation (docs/04, "Incremental builds, as implemented")
is a fingerprint-keyed cache rather than the query engine: the unit is the
package's library (and a binary's entry file), each compiled to an object
from a compilation of the whole program and cached in
`target/<profile>/cache` under `(toolchain, profile, target, fingerprint)`,
where the fingerprint is the unit's content and the interfaces of the units
it depends on. An interface is a hash of each file's public surface —
declarations, signatures and the bodies other units lower (generic
functions, `const fn`s, defaults). Each unit's is written to a binary
`.sbi` in `cache/interfaces/`, keyed by the unit's content (`SBI`, a
version, the payload and its SHA-256; one of another version, cut short or
corrupt is worked out again): each file's fingerprint, its extensions'
fingerprint, the dotted names it writes, which of the types its extensions
extend it took to be concrete, and its source with every body other units
cannot see blanked — line breaks kept, so that every declaration and every
body other units lower is where it is in the file, and their debug
information and trap messages come out the same. The next build reads an
unchanged unit's interface from it rather than working it out, and parses
and binds a unit whose object it keeps from that source rather than from
the unit's own: the compiled units are type-checked and lowered against the
others' interfaces. A unit found to need compiling is parsed from its
source; so is the whole program, when a build falls back to compiling it
whole. What the `.sbi` does not hold yet, of what is planned above: escape
summaries (a unit assumes the worst of another's functions, docs/02 §2.2),
and declarations as symbols rather than source — binding them is still the
front end's work on each build, over the interfaces' shorter text.

**Declarations as symbols: what stands in the way.** Measured on
`examples/ssg` built as a package (one 636-line unit and the standard
library's, 186 files and 15.7k lines of it imported; `sable` built Release,
load average about 10): a no-op build is fresh in 0.06 s (0.12 s with the
process), and a body edit 2.7–3.0 s, of which bind is 0.62–0.69 s, SIL
1.3–1.5 s, clang 0.3 s and link 0.05 s. Of bind, declaring the program
(`Compilation.Create`) is 130–180 ms in a fresh process and 45–65 ms warm,
binding every signature 125–135 ms fresh and 45–55 ms warm, and checking the
edited unit's bodies the rest (390–450 ms fresh, 110–150 ms warm), much of
the fresh times being JIT. Symbols read from a `.sbi` in place of declaring
and binding the kept units would save at most the first two, about 0.25 s of
the 2.8 s fresh and less warm; binding the kept units' signatures lazily
instead saves nothing, since checking the bodies binds nearly all of them
(0.54–0.62 s against 0.52–0.58 s eagerly). The standard library was not
read from a `.sbi` at all: it was the embedded source, parsed whole (every
module's, 247 files) on each build — which this left out, and which by the
time `lib/` had 100k lines was the largest part of a small build (below,
"Declarations from the interface").

Loading symbols without their source was blocked by what reads the source
behind a symbol, not by the binder's tables alone. What lowering and binding
read of another unit's declaration is now a fact on its symbol (#182, stage
2): `Symbol.IsSynthesized` (an implicit initialiser, a flags type's
`contains`) rather than an empty `DeclaringSyntax`; `Symbol.Attributes`,
each attribute's name and argument list as written, with those of the
extension a member is declared in — `@inline(always|never)`, `@main`,
`@intrinsic`, `@indirect`, `@MainActor` and a type's reflected attribute
names are read there; `IsNonisolated`; `FunctionSymbol.HasBody`; and a
type's const-generic predicates, bound once as `ValuePredicate`s that are
evaluated without syntax. Enum raw values were facts already. Lowering's
index of what another unit can see (`SilProgram.Index`) is built from the
compilation's symbols — every module's members, every type's, extensions'
among them, and properties' accessors — not by walking the other files'
syntax (stage 3); a unit still walks its own files whole, for the
declarations in its bodies. `SilProgramTests` holds the two to the same
declarations in the same order.

What still reads another unit's source is the bodies another unit lowers: a
generic function's instances, `@inline` bodies, default arguments, `const
fn`s, constants' values, field initialisers and, for the entry unit, every
global's initialiser, and a type's reflected attribute values. They are bound
from their file in its file scope, so a unit's symbols can stand in for its
trees only where each such file can still be had when one of them is asked
for — which the standard library's always can, since it comes with the
compiler. Built unspecialised, its generics are compiled once (#297) and few
of them are.

**Library interfaces (`.sbi`), as built** (#182, stage 4). A library unit's
interface — `LibraryInterface` in Sable.SIL, written for Sable.Core by
`Pipeline.CoreInterface` — holds its bound symbols and its SIL. The symbols
(`SymbolInterface`) are a table of declarations in preorder, each with its
kind, name, container, accessibility, flags, attributes and the facts above,
followed by the types each refers to, written as trees: a declaration of the
table by its index, one of another unit by module and dotted path, which
reading resolves against the units read before it. They read back as symbols
with no syntax that write back to the same bytes, and describe every
declaration of Sable.Core as the bound ones do (`LibraryInterfaceTests`).
Default arguments, initial values and accessors are not in the symbols:
they are expressions and functions, in the SIL or the source. The SIL is
`SilPrinter`'s text, Brotli-compressed: `Pipeline.CoreInterface`'s holds
every function of the unit's files lowered as a unit of a program compiled
separately, what `CompileAgainst` links in; a build's (`std.sbi`, a
dependency's) only the code other units may copy (#297 stage 7, below).
After it comes the table of the unit's generic functions compiled once
into its archive, each with whether it writes out a value of a type it is
given and whether it is inlinable. The file is binary: `SBIL`, the
format's version (2), then the compiler's identity, the settings (target os
and arch, profile, `--deny-reflection`, unspecialised generics), the unit's
name, each of its files' name and SHA-256, its modules, what other units'
extensions add to its types in the compilation it was lowered in, the
sections, and the payload's SHA-256. One of another version, another compiler, cut short or corrupt is
not read (`LibraryInterface.Read` gives null); the `interface` fuzz target
holds the symbol reader to refusing what it cannot read and to writing what
it reads back the same. The compiler's identity
(`LibraryInterface.IdentityOf`) is the SHA-256 of its assemblies' module
version ids, which a deterministic build derives from their content — the
embedded library and runtime among it — so another build of the compiler
never reads an interface this one wrote, whatever their version numbers.

`Pipeline.CompileAgainst(program, interfaces)` compiles a program against
them: every file no interface covers is checked and lowered as the unit
holding the entry point (which sets up every global), and each interface's
SIL is linked into the module, the program's copy kept of what both make (an
instance, a thunk), and what the program does not reach dropped. The libraries' bodies are not bound or checked
(`Compilation.GetAllDiagnosticsByFile(checkBodies)`) and their code is not
lowered; their declarations are still declared from their source, which
ships with the compiler and holds the generic bodies the program
instantiates. An interface built from other files than the program's, for
other settings, or before another unit's extension of its types (which
changes how its methods are named) is refused, and a build answers by
building it again. What the program says and builds is what compiling it
whole does: `InterfaceCompileTests` compares the errors, the warnings, the
SIL and the interpreter's output. The interface keeps each function's own
order of its blocks (`blocks(...)` in the SIL text, which the printer
otherwise orders by dominance), so that the passes see the library's code as
lowering made it. An optimised build does the same, but its SIL may differ
where lowering the whole program used what only the whole program shows —
an existential's call made directly when nothing the program declares
answers it otherwise — which the library, lowered as a unit, leaves to
dispatch.

Measured on the three-line program of #182 (`print("hi \(1 + 2)")`), in one
process, on an M1 Max under a load average of about 15 from other builds —
rough times; the shape is what counts. Before #182's stage 1, compiled
whole, each compilation bound and checked every body of Sable.Core: 3.4 s
the first time in the process, 1.6–1.7 s after, of which binding was
1.28 s. Since stage 1 (`Library.CheckFor`), a process checks the core once
and then neither checks nor lowers more of it than the program reaches: the
first compilation still takes 2.0 s, the next ones 0.14–0.18 s. Against the
core's interface, read once, the first compilation takes 0.18 s and the
next ones 0.13–0.17 s (binding 0.05 s, lowering 0.08 s — the program's own
code, the core's instances it uses, and parsing the interface's SIL, 60 ms
of it — the interface's code the program does not reach dropped before it
is verified). So the interface saves what a fresh process — a
`sable build` of one file, a test harness's first program, the language
server's first file — pays to check the core: 1.8 s of the first
compilation's 2.0 s; a long-lived process that has checked the core already
gains nothing more from it. Building the core's interface takes 5 s once per
target and profile; reading the file back takes 5 ms, its symbols 10 ms,
and it is 330 KB (the symbols 183 KB, the SIL 820 KB of text compressed to
150 KB). A package build writes the standard library unit's interface
(`std.sbi`) into its build cache entry, beside the archive (stage 5's
`LibraryUnit.Write` hook). `CompileAgainst` still declares the core from its
source (`Compilation.Create`, about 60 ms warm); a build against the cache
takes the declarations too (below).

**Declarations from the interface** (#182). A build whose standard library
unit is in the build cache takes the library's declarations from there
rather than declaring them: its entry keeps, beside `std.sbi`, `std.symbols`
(`LibrarySymbols` in Sable.Symbols) — the symbols in `SymbolInterface`'s form,
and for each, what taking them in place of the files needs: the place of its
declaration (file, offset, length, kind), whether a constant's value is a
literal, each property's and subscript's accessors, and the array extensions,
`@derive`d members, inherited initialisers and `@autoclosure` callees the
compilation otherwise finds by declaring. It is written with the entry, from
the library declared alone — the same for every program — and refused
unless its SHA-256, version and files are right; the entry's key holds the
compiler's identity, so a changed compiler has another.

`Compilation.Create(trees, library: symbols)` reads the modules (fresh symbols
for each compilation) and declares only the program's files. Nothing of the
library is parsed: its trees are `SyntaxTree.Deferred`, whose text is parsed
when something first asks for a node, and whose module and imports are read
off the lines before its declarations (`SourceHeader`). What only a file says
is found when asked for: `Symbol.DeclaringSyntax` parses the file and finds
the declaration where the interface says it is (`SymbolSource`; `IsDeclared`
answers without), `GetDeclaredSymbol` gives a parsed file's declarations
their symbols, `GetFileScope` resolves its imports — so an instance bound from
source, a default argument, a non-literal constant, a place a diagnostic points
at come out as they do when the file was declared. Lowering lists the library's
functions, accessors, globals and `@builtin`s from the symbols
(`Compilation.InterfaceDeclarations`) rather than from the outline of its
files. `--timings` says how many of the library's files a build parsed
(`library files parsed`): for the program below, none.

The library is declared from its source, as before, whenever that could differ
or is not worth it: a program that brings a file of the library or declares
in one of its modules, or extends one of its types (whose members then come in
another order); a first build, which compiles the library; an optimised
build of a single file and a fallback to compiling whole; `sable check`,
`sable test`'s harness, the documentation, and the language server, whose
hovers and navigation read the library's files all the time. A library with
something the symbols do not hold — a generator, a lazy or observed property,
a `comptime` block, a hidden `some`, an attribute of its own — has none;
`LibrarySymbolsTests` fails when the standard library gains one.
`SABLE_LIBRARY_SYMBOLS=source` turns them off, and `=interface` gives them to
every compilation, made in the process: what the tests hold the two to each
other with. `LibrarySymbolsTests` compares the diagnostics, SIL, layouts and
external functions of the examples and the corpus both ways, and binds every
body of the whole library against its own symbols.

Measured on a package of the three-line program (`print("hi \(name) \(1 + 2)")`),
warm cache, its `target/` removed before each build, `sable` built Release on an
M1 Max under a load average of 10–150 from other builds, best of 7, fresh
processes (so most of each phase is JIT):

| Phase | Before | Files parsed as needed | Declarations from the interface |
|---|---:|---:|---:|
| the library's source: loading, parsing | 614 ms | 10 ms | 10 ms |
| interface phase (names its extensions extend) | 16 ms | 13 ms | 15 ms |
| bind: declare (parsing Core, in the second) | 131 ms | 202 ms | 37 ms |
| bind: signatures | 90 ms | 91 ms | 2.5 ms |
| bind: the program's bodies | 56 ms | 35 ms | 41 ms |
| index | 13 ms | 13 ms | 1.5 ms |
| SIL, verify, emit | 176 ms | 176 ms | 180 ms |
| the whole build | 1.4–1.6 s | 0.82 s | 0.65 s |
| user CPU time | 1.55 s | 1.07 s | 0.56 s |

So of the 0.85 s a warm build spent on the library, about 0.05 s is left:
reading its text (which the cache's key hashes) and its symbols. Parsing it
was most: every module of `lib/`, 100k lines, one file after another, for a
program that imports none. A program that instantiates the library's generics
parses the files whose bodies it lowers: sorting, mapping and joining an array
parses 4 built specialised and 3 built unspecialised; `examples/ssg` as a
package (which imports three modules) 50.

That package's warm build, its `target/` removed, the same machine at a load
average of 15: 7.6 s before, 2.9 s with the library declared from source, and
2.3 s from the interface (user CPU 10.3, 3.5 and 2.3 s). Declaring and binding
signatures fell from 0.57 s to 0.08 s; the rest of the difference, 4.2 s, was
compile-time evaluation, which a program importing a library constant that is
computed runs: it bound every body of the library's files that have a property
to name their accessors. It names those of a file it does not check from their
declarations, as lowering a unit does.

**Dependents only.** A unit depends on the units declaring a module it
imports or names in full (`Shapes.Square` needs no import), on the units
that share one of its modules (what one declares there, the other sees as
its own), and on what those depend on; the unit holding the entry point
depends on every unit, since it sets up every global, and every unit on the
standard library, which depends on none. A unit's key covers the interfaces
of its dependencies, so a changed interface compiles again exactly the
units that depend on it. What makes that sound is that a unit's lowering
reads no fact of the units that depend on it: separately compiled, an
`open` class's methods are dispatched through its vtable whatever
subclasses the unit sees (any module may add one), while a class that is
not open can be subclassed only in its own module, which the unit's key
covers; and a unit makes storage only for the globals it names (the entry
unit, for all). The one exception is extensions: an extension adds to a
type that units which do not depend on it can see — a conformance, or an
overload that changes how the type's methods are named — so every unit,
the standard library's too, is keyed by every unit's extensions'
fingerprint. A body inside an extension is its unit's own, as any other.
Clang runs on units in parallel; lowering is still one unit at a time.
An unoptimised debug build is compiled this way, and so is an optimised one
whose profile has `lto = "thin"` — the default — or `"off"`: each unit's SIL
passes run on the unit — assuming the worst of what another unit defines —
its object is LLVM bitcode, and the link optimises across units with
ThinLTO, keeping ThinLTO's own cache beside the objects. `lto = "fat"`, and
an optimised build with debug information, compile the program whole. A
package's native library (`[lib] kind`) is built the same way, its own
unit leading in place of an entry file (§5.2.2).
The standard library's unit is compiled to an archive (`libsable_std.a`) and
the runtime to another (`libsable_runtime.a`), each kept in the shared build
cache below; a program's link takes its units' objects, then those archives.
An unoptimised build of a single file (`sable build -O0`, `-g`, and `sable
run` of either) is compiled the same way, the file its one unit, so that it
compiles only itself against a warm cache; an optimised one is compiled
whole, the library with it, so that the SIL passes see all of it.

**What a unit binds of the files it does not own** (#182). Lowering a unit
— and lowering a library known to check cleanly on demand, as `sable
check`, a single-file build and the language server do — binds another
file's body only where it reaches it: a generic function's instance, a
function lowered on demand, a global's or a field's initial value, a
constant. Each is bound by a model of the one member it is in
(`SemanticModel.GetMemberModel`, which shares the file's accessor
functions, so a setter's `newValue` is the parameter declaring it gave),
not by binding every body of its file (`SilGen.BodyModel`); a file the
lowering checks, or has bound already, is read as it is. Compile-time
evaluation searches a file's text for `const` and `comptime` before
walking its tree, and finding the generators searches a function's text for
`yield`. Measured on `examples/ssg` as a package (a one-body edit in its
636-line unit, the standard library's archive and `.sbi` cached, `sable`
built Release, load average 5–10): the build falls from 2.03–2.05 s to
1.58–1.67 s; lowering from 0.77–0.82 s to 0.37–0.50 s, of which
instantiating the 70 instances of the library's generics the unit uses fell
from 0.41 s to 0.19 s (they were binding all of `Dictionary.sb`, the string
extensions and `Array.sb`), compile-time evaluation from 0.10 s to 0.01 s
and finding generators from 38 ms to 3 ms. Of the rest: binding 0.41 s
(declaring the program from source 0.12 s, its signatures 0.08 s, the
unit's bodies 0.22 s), clang 0.30 s, link 0.05 s.

*A library's generic code, compiled once* (#297 stage 7). Built specialised
(the default), what a unit lowers of the library is the instances of its
generics, which no `.sbi` can carry: of the 70 `examples/ssg` uses, the
library's own unit had made none. Built unspecialised (`-generics=unspecialised`),
the library's unit compiles every generic function of its files that can be
compiled once into its archive and records them in its `.sbi`
(`LibraryInterface.Generics`, docs/06 §6.2); the build lowers the library
units first, and every unit after calls that code in the archive, neither
binding nor lowering its bodies. Only the generic functions the library
left specialised are still instantiated from its source. The `.sbi`'s SIL
is the inlinable part of that code, which an optimised unit copies in for
the specialisation pass (`LibraryInterface.LinkInlinable`). The library's
archive is one object, which a linker otherwise takes whole: on macOS a
program is linked with `-dead_strip`; on Linux and Windows every unit and
the runtime are compiled with a section per function and datum
(`-ffunction-sections -fdata-sections`) and linked with `--gc-sections` (but
not a debug build, whose debug information GNU ld would leave at address 0) and
`/OPT:REF` (not `/OPT:ICF`, which could fold two types' metadata or witness
tables into one address); wasm-ld leaves out what nothing reaches unasked.
What the units' constructors register (witness tables, describers, error
erasers) stays, as `.init_array` and `.CRT$XCU` are roots, and so does the
debugger's table of types (`llvm.used`, `SHF_GNU_RETAIN`). Linux programs
no longer link `-rdynamic`, whose exported symbols would all be roots; a
trap's backtrace names their frames from `.symtab`. Reckoned from the
`examples/ssg` dev build's objects for x86_64 Linux and Windows (cross-
compiled, reachability over their relocations as the linkers walk it), the
code and data the link takes fall from 1.92 MB to 1.07 MB on Linux and
1.97 MB to 0.85 MB on Windows unspecialised, and from 1.13 MB to 0.79 MB
and 1.14 MB to 0.65 MB specialised; the standard library's share from
1.57 MB to 0.71 MB (Linux, unspecialised).

Measured with `sable` built Release, a package's dev build (debug
information, units) on an M1 Max under a load average of 10–30; "before"
is master just before this, cold is an empty `$SABLE_HOME` and `target/`,
warm a warm cache and an empty `target/`, edit one body changed after a
warm build; sizes are the executable's:

| Build | Before, specialised | Before, unspecialised | After, specialised | After, unspecialised |
|---|---:|---:|---:|---:|
| three-line, cold | 2.7–3.0 s | 2.8–2.9 s | 2.7–2.8 s | 3.55 s |
| three-line, warm / edit | 0.85–0.88 s | 0.85–0.87 s | 0.86–0.89 s | 0.86–0.90 s |
| three-line, size | 195 KB | 195 KB | 94 KB | 94 KB |
| `examples/ssg`, cold | 3.75–3.77 s | 4.0 s | 3.6 s | 4.68 s |
| `examples/ssg`, warm / edit | 1.69–1.72 s | 1.93–1.95 s | 1.69–1.70 s | 1.54–1.57 s |
| `examples/ssg`, lowering (warm) | 0.38–0.39 s | 0.54 s | 0.38–0.39 s | 0.25 s |
| `examples/ssg`, size | 1.45 MB | 1.62 MB | 0.82 MB | 1.21 MB |

An unspecialised one-body edit of `examples/ssg` is now 9% quicker than a
specialised one and 20% quicker than before; its first build, which
compiles the library's every generic function into the cache, is 17%
slower, once per target and profile. The binary is half again a
specialised one's. The benchmarks, each built as a package in the release
profile (ThinLTO, units), run as before in both modes (within the 2–3 ms
the runs vary by), and
`words`, whose dictionary code is copied from the interface, as fast as
specialised; an unspecialised one is up to 3.1× the size (`words`, 279 KB
against 90 KB).

What is still not met of #182: a warm three-line build binds for 0.2 s,
much of it declaring Sable.Core from its source (`Compilation.Create`).
Declaring it from the `.sbi`'s symbols would need every body a unit may
lower of the library — the generic functions still specialised, `@inline`
bodies, default arguments, `const fn`s, constants, initial values — in a
form other than source.

*Libraries compiled optimised* (#298). A library's unit — the standard
library's, and each registry or git dependency's — is compiled optimised
in every profile, as Swift ships its standard library: a dev build calls
optimised library code, and only the program's own units are unoptimised
and fully debuggable. An optimised build compiles its libraries with its
own settings, as before. An unoptimised one compiles them with
`BuildSettings.ForLibrary`: the SIL passes of opt-level `s` (the
specialisation pass for size, devirtualisation, borrowing, the inliner,
closures, simplification and the ARC passes) and clang's `-Os`, to a plain
object (no bitcode: an unoptimised link does no link-time optimisation),
with the build's debug information. Its cache entry is keyed by those
settings (`option optimise=True`, `option sil=s`, `option clang=-g -Os …`),
not by the program's, nor by the profile's name; two unoptimised profiles
with the same checks and debug information share one entry. (The release
profile's library — `-O3`, ThinLTO bitcode, no debug information, no
`DEBUG` — is compiled differently, so has its own.)

*What a dev program gets from it.* Every check the profile asks for: the
library is parsed with the program's `DEBUG`/`RELEASE` and lowered from the
same compilation, so `assert`, `assertionFailure` and `#if DEBUG` in it
hold in a dev build as in the program's own code, and its exclusivity
checks, overflow traps and ARC checks are as the profile says.
`precondition` holds in every build. This differs from Swift, whose
library is built once, in release mode, and so keeps only its
preconditions: Sable compiles the library for the build's settings anyway,
so a dev program loses no trap by its library being optimised
(`IncrementalTests.ADevBuildLinksTheStandardLibraryOptimisedWithTheProfilesChecks`).

*Debugging.* The archive keeps its line tables and debug information, its
compile unit marked optimised: lldb shows its frames with `[opt]` and says
once that the program "was compiled with optimization". A breakpoint in
the library's code stops there, the backtrace names its frames and the
program's, and the formatters show the program's values; in a library
frame some variables may be unavailable, and stepping inside it is coarse
(`DebuggerFormatterTests.ADevBuildStopsInTheOptimisedLibraryAndShowsTheProgramsValues`).

*Why `-Os`, and why all three optimisers.* Measured with the library at
each level (the three-line program's cold build, its library's `__text`,
and `words`, the benchmark that runs the most library code, at `-O0`,
unspecialised):

| Library built with | Cold build | Library `__text` | `words` |
|---|---:|---:|---:|
| `-O0`, no SIL passes (before) | 3.55 s | 1074 KB | 333 ms |
| clang `-O2`, no SIL passes | 6.30 s | 649 KB | 278 ms |
| SIL `2` without the specialisation pass, clang `-O2` | 7.07 s | 520 KB | — |
| SIL `2`, clang `-O2` | 6.92 s | 527 KB | 176 ms |
| SIL `s`, clang `-Os` | 6.61 s | 477 KB | 174 ms |

The SIL passes are what make the library's generic code fast (LLVM alone
cannot see through value witnesses and metadata); the specialisation pass
costs nothing measurable and makes the instances the library's own code
needs; `-Os` ran as fast as `-O2` on every benchmark and is 9% smaller.

*The cost: a library's first build.* The library's passes and clang's
`-Os` take longer than its `-O0`: an empty cache's three-line build goes
from 3.55 s to 6.6 s unspecialised and from 2.6 s to 4.0 s specialised. It
is paid once per compiler, target and settings — a release of the compiler
carries the dev and release entries prebuilt (`sable cache warm --into`),
so a user's first build does not pay it — and every later build links the
entry. To keep it down, a release build of the compiler verifies SIL once
after the passes rather than after each (docs/04 §2.1): verifying after
every pass was 2.4 s of the 3.6 s the standard library's passes took.

*Why a cold unspecialised `-O0` build was 1 s slower than specialised.*
Not stage 6 (run-time class metadata): stage 7 (#297, a library's generic
code compiled once into its archive) is where the gap grew, from 0.3 s to
1.1 s on `examples/ssg` (the stage-7 table above: 4.0 s to 4.68 s). Before
it, the standard library's unit compiled none of its generic functions;
since, it compiles every one that can be compiled once — 767 of them, 624
KB of the unit's 1074 KB of `-O0` code — on its first build. On the
three-line program the difference is all in that unit: SIL lowering +290
ms, verifying +65 ms, emitting +110 ms and clang +475 ms. That is the work
stage 7 moves into the cache, not a regression in any one step, and later
builds do not repeat it. Now that nearly all of the library's generic code
is compiled once (1324 functions, docs/06 §6.2), and its array searches by
vectors are made for every SIMD scalar, the gap is larger, and of the same
kind: `examples/ssg` with an empty cache takes 24 s specialised and 31 s
unspecialised on a loaded M1 Max (clang 10 s and 19 s of it, the passes 4 s
and 6 s; 4 s is the prepared searches), and each build after it 2.6 s and
2.0 s.

*Why an unspecialised `-O0` binary is larger* (2.6 MB of `examples/ssg`,
0.67 MB specialised): what each unit registers as it starts — witness
tables, templates, boxers, describers — is named by its constructor, so the
linker keeps every table of the library's units, and with them the entries
(1,300 witnesses, 610 template functions), the code those call and its
metadata, whether or not a lookup can ask for one: 1.0 MB of code for 0.44,
0.4 MB of constants, and their symbols (1 MB). Keeping only what a program
can look up needs the linker to drop a record whose type or protocol is
dropped (Mach-O's `live_support`, ELF's link order), which the constructors
do not let it. An optimised build has none of this.

*Before and after*, measured with `sable` built Release on an M1 Max
(load average 5–8), each a package's dev build: cold is an empty
`$SABLE_HOME` and `target/`, warm a warm cache (as a release's prebuilt
one is) and an empty `target/`, edit one body changed after a build; sizes
are the executable's:

| Build | Before, specialised | Before, unspecialised | After, specialised | After, unspecialised |
|---|---:|---:|---:|---:|
| three-line, cold | 2.61–2.65 s | 3.54–3.56 s | 4.04 s | 6.57–6.60 s |
| three-line, warm / edit | 0.83–0.85 s | 0.83–0.85 s | 0.83–0.84 s | 0.83–0.87 s |
| three-line, size | 94 KB | 94 KB | 94 KB | 94 KB |
| `examples/ssg`, cold | 3.50–3.61 s | 4.56–4.60 s | 5.08–5.35 s | 7.99–8.06 s |
| `examples/ssg`, warm | 1.64–1.68 s | 1.51–1.52 s | 1.64–1.65 s | 1.57–1.58 s |
| `examples/ssg`, edit | 1.65–1.69 s | 1.50 s | 1.63 s | 1.50–1.51 s |
| `examples/ssg`, size | 818 KB | 1214 KB | 677 KB | 943 KB |

The benchmarks, each a single file's `sable build -O0 -g` (the program's
unit unoptimised, the library's archive from the cache), fastest of three
runs in ms, and the executable's size:

| Benchmark | Before, specialised | Before, unspecialised | After, specialised | After, unspecialised |
|---|---:|---:|---:|---:|
| binary-trees | 767 | 765 | 770 | 766 |
| nbody | 246 | 246 | 245 | 245 |
| orders | 424 | 425 | 423 | 424 |
| shapes | 19 | 18 | 19 | 19 |
| sort | 117 | 176 | 117 | 177 |
| words | 142 | 333 (601 KB) | 118 | 174 (451 KB) |
| json | 17 (531 KB) | 17 (932 KB) | 17 (515 KB) | 19 (812 KB) |

Most benchmarks spend their time in their own `-O0` code, which is as it
was; `words`, which runs dictionary and string code of the library's, is
17% faster specialised and 48% faster unspecialised. At `-O2` (compiled
whole) nothing changed: every benchmark within a millisecond or two of
before, in both modes, at the same sizes.

*Unspecialised as the debug default (#297), revisited.* With the library
optimised, unspecialised generics in a dev build give an edit of
`examples/ssg` 8% quicker than specialised (1.50 s against 1.63 s) and a
warm first build about as quick, but an executable 39% larger (943 KB
against 677 KB; it was 48%), and the program's own generic code at `-O0`
is still 1.5× slower (`sort`, `words`); a cold build without a prebuilt
cache is 2.9 s slower, once. (The program's own generic frames showed
opaque values in a debugger then; since stage 9 they show their values and
types, docs/04 "Debugging, as implemented".) So debug builds stay specialised for now: the
edit gain is small next to the size and `-O0` run time, and the choice is to
be made again once the binary-size follow-ups (#299, #300) land.

*One witness-table convention (#299).* Existentials became Swift's
layout, always boxed — the box, the value's metadata and a table pointer
per protocol — and every witness table one inline list of entries in the
generic calling convention, with the existential-convention slots and every
thunk between the two conventions deleted (docs/06 §6.2). Measured with
`sable` built Release on an M1 Max (loaded), each a single file's `sable
build`, fastest of five runs in ms, before (b801d3c) and after:

| Build | Before, specialised | After, specialised | Before, unspecialised | After, unspecialised |
|---|---:|---:|---:|---:|
| `examples/ssg` `-O0`, size | 529,400 B | 529,400 B | 920,584 B | 727,928 B |
| `examples/ssg` `-O0`, `__LINKEDIT` | 70,648 B | 70,648 B | 314,376 B | 236,408 B |
| shapes `-O0` / `-O2` | 31 / 28 | 31 / 26 | 29 / 25 | 30 / 24 |
| 1000 existentials × 20,000 rounds, `-O0` | 410 | 260 | 407 | 264 |
| the same, `-O2` | 122 | 109 | 121 | 108 |

The other benchmarks (binary-trees, nbody, orders, sort, words, json) are
within noise at both levels in both modes, with the same allocation counts;
unspecialised `-O0` executables shrink where they box or call generic code
(`words` 510 KB to 336 KB, json 821 KB to 668 KB). An unspecialised ssg is
21% smaller and its `__LINKEDIT` 25%: one entry per requirement, and no
thunks or bridges to relocate. A specialised ssg makes almost no tables and
is unchanged. A call through an existential is now two dependent loads (the
table, then the entry) where it was one, but an existential of one
protocol is three words, not one plus two per requirement, so copying one
— what iterating an array of them does — is cheaper, and the existential
benchmark is faster at both levels.

### The build cache

What a build makes that does not depend on the program — the C runtime's
archive, the standard library's, and each registry or git dependency's — is
kept in `$SABLE_HOME/cache/<key>/`
(default `~/.sable/cache`), shared by every project and every build, and made
lazily the first time a target and profile need it. `target/` keeps only the
project's own units. An entry is a directory: the archive, the object it
archives (a static library C links takes that whole), the library's `.abi`
record of the types it laid out, the library's interface `<unit>.sbi`
(its symbols and SIL, `LibraryInterface`), for the standard library the
declarations a program takes from it, `std.symbols`, and `entry.txt`, the key it was
made for and the profile of the build that made it.

A **dependency** from a registry or git — one whose own dependencies are too
— is a library unit like the standard library (`UnitPlan.Package`): its
archive and interface go in an entry of the shared cache, so a dependency
that two projects use, at the same version, for the same target and
profile, is compiled once (`BuildCacheTests`). Beside it is a second,
smaller entry (`interface` in `sable cache list`) holding the unit's
interface file — its declarations with the bodies other units do not need
blanked, which the build binds a kept unit from — keyed by its source and
what it is parsed for alone, since that is all it depends on: a project
that has never built the dependency finds it there, and neither parses its
bodies nor works its interface out. A package of the workspace — a path
dependency — stays a unit of the project, its object in `target/`: it
changes as the project does, is used by that workspace alone, and is named
in diagnostics and debug information relative to it; the shared cache
would only fill with versions nobody else can use.

The key is a SHA-256 over:

- the package's name and version, and its source checksum — every file's
  name and text (the standard library's: Sable.Core's and the modules the
  program imports, parsed for the target and profile);
- the **compiler's identity** (`CompilerIdentity`): not its version string but
  a hash of its own build — every assembly of it, every resource they embed
  (`lib/`'s source and the runtime's among them) and the version — so a
  compiler rebuilt with any change, even under the same version number, finds
  none of the entries an earlier build of it wrote;
- the target triple;
- the settings the library is compiled with (`BuildSettings.ForLibrary`),
  which are not the program's in an unoptimised build: the opt-level and
  the SIL passes it picks (`s` for every unoptimised profile), debug
  information, link-time optimisation (bitcode or not), exclusivity,
  overflow and ARC checks, `debug-assertions`, reflection,
  position-independence, and the C compiler's identity. The profile's name
  is recorded, not keyed, so two profiles that compile the library the same
  way share its entries;
- the enabled features, and the keys of the entries it was compiled
  against: a dependency's, the standard library's and those of its own
  dependencies (the standard library depends on nothing kept);
- and the fingerprint of the program's extensions, which can change how
  the library's methods are named — none, in most programs, so they all
  link one archive.

An entry is made in a temporary directory beside where it goes and renamed
into place, so builds running at once and the language server see it whole
or not at all; two builds that need the same entry at once each make it,
and the first to rename keeps it. A unit whose archive does not link, or
whose `.abi` disagrees with the program's, makes the build compile the
program whole, as with any unit.

`sable cache list` shows the entries with the fetched packages; `sable cache
prune [--max-age <days>]` removes the entries another build of the compiler
wrote, and its records that a library checks cleanly (below), the entries,
packages and evaluated manifests no build has used in that many days
(default 30), and what an interrupted build left half-made; `sable cache
clean` removes everything, the records too. `sable cache warm [--target <triple>]
[--profile dev|release]` makes the entries a build for that target and
profile needs now: the runtime, and the standard library alone and with each
module a program may import (`--import <Module>` for one set). With
`--into <dir>` it writes them to `<dir>` instead: a release of the compiler
carries the host's in a `cache/` directory beside `sable`, which is read as
prebuilt and never written (`tools/release/package.sh`), so a first build
on a fresh machine compiles only the program. Another target's entries are
made in the user's cache on first use.

**The library, known to check cleanly.** A standard library unit in the
cache with its `.sbi`, made for a program that extends nothing, is proof
that the library's files — Sable.Core and the modules imported, for that
target and profile — bind and lower without a word: the build that finds or
writes it (and `sable cache warm`) records them in
`$SABLE_HOME/cache/library-checked/<compiler identity>/`
(`Library.MarkChecked`), as a build that checked the library whole does
(`LibraryCheck.Passed`). A fresh process then checks none of it again for a
program that cannot change how it binds or lowers
(`Compilation.ReachesIntoLibrary`): a build of one file and `sable check`
bind only the program's bodies, and lower only the library functions it
reaches. So does the **language server**: its SIL checks — use after move,
overlapping accesses, roots — lower the package with the library on demand
(`LanguageServerOptions.LibraryCheck`), and a server that checked the
library whole with nothing to say records it for the next. Its compilation
still declares the library from its source, so go-to-definition and hover
land in the library's files as before. The editor's files are parsed with
neither `DEBUG` nor `RELEASE`, so they are recorded apart from a build's.
Measured on a small file (`"a,b".split(",")` and an interpolation), a fresh
`sable lsp` built Release, on a loaded machine: the first diagnostics (the
type errors) come 0.7 s after the file is opened, before and after; the
complete ones, with the SIL checks, 5.3–5.6 s after with the library
checked whole (lowering 4.6–4.9 s), 2.6–2.9 s once it is known (lowering
1.9–2.1 s). A dependency's files read from disk are library files to
these checks: the package's own are its documents and what its generators
write (`Analysis.IsOwn`), and `Library.CheckFor` records any library file
that is not the standard library's by its name, features and text, so a
dependency known to check cleanly with the package — a server that checked
it whole found nothing — has its bodies left alone too, until its text
changes. Since a unit binds only the members of other files it reaches
(above), the same small file's checks lower in 0.46–0.47 s against
0.60–0.62 s before, and complete 1.1 s after the file is opened (load
average about 10); a first server, with the library not yet known, lowers
it whole in 1.7 s. A build's records are not the server's: its files are
parsed with `DEBUG` or `RELEASE`, so the server learns its own.

Measured (#182), `sable` built Release, on a machine under a load average
of 10–35, so the times are rough:

| Build | Before | Cold cache | Warm cache |
|---|---:|---:|---:|
| `examples/01-hello.sb`, `sable build -O0` | 5.4 s | 6.2 s | 1.1 s |
| the same, `-g` | 5.3 s | 5.8 s | 1.1 s |
| the same, `-O2` (whole, as before) | 7.0 s | 8.0 s | 7.0 s |
| `examples/ssg` as a package, dev, clean `target/` | 7.7–8.3 s | 10.1 s | 4.0–4.5 s |
| the same, release (`lto = "thin"`), clean `target/` | 10.4 s | 12.9 s | 5.5 s |

"Before" had the runtime cached and nothing else; a warm cache leaves a new
project's first build its own unit to bind and lower against the library's
source, which is #182's later stages. The benchmarks are optimised single
files, compiled whole as before; their times are unchanged.

**Measured** (#182) on `tools/incremental/genworkspace.py`'s workspace (100
packages, 110k lines, 102 units), `sable` built Release, on a machine under
a load average of about 130 — so the times are rough; the unit counts are
exact. Before is keying by the whole program's interface, after is keying by
dependencies and binding kept units from their `.sbi`:

| Edit (dev profile)                          | Units before | Units after | Before  | After   |
|---------------------------------------------|-------------:|------------:|--------:|--------:|
| A function body (pkg50 / pkg0 / pkg99)      | 1            | 1           | 4.8 / 8.9 / 5.4 s | 3.5 / 2.5 / 3.4 s |
| A new public function in a leaf (pkg99)     | 102          | 2           | 19.3 s  | 2.7 s   |
| … in a package half the others use (pkg50)  | 102          | 51          | 20.1 s  | 10.9 s  |
| … in the package every other uses (pkg0)    | 102          | 101         | 18.0 s  | 14.7 s  |

Of a body edit, parsing fell from 0.37–0.59 s to 0.15–0.30 s (496 of the
501 files are parsed from interfaces) and binding from 1.1–2.3 s to
0.5–0.9 s. `IncrementalTests` counts the units each kind of edit compiles.

### Dependencies by name (#182, stage 9)

Keying a unit by the whole interfaces of the units it depends on compiles
again every dependent of a package when anything another unit could see of
it changes — a new public function in the package every other uses compiled
101 of the 110k-line workspace's 102 units. A unit is now keyed by what of
its dependencies it may use, declaration by declaration: the fine-grained
dependency tracking of a query engine, recorded where the interface is
worked out rather than by a long-lived process.

*What is recorded.* With each file's interface (`FileInterface`, kept in
the unit's `.sbi`, format 4), `InterfaceFingerprint.Interface` writes:

- for each top-level declaration other than an extension, a
  `DeclarationPrint`: the name it declares, the fingerprint of its own part
  of the interface (its signature, its members, and the bodies other units
  lower — generic, `const`, kept by an attribute — with the positions the
  file's fingerprint has), and the names that part mentions. A declaration
  that may matter to a unit that never writes its name — an operator, a
  global or constant (whose initial value the entry unit runs), `fn
  Type.method`, anything not recognised — is named `*`;
- the file's *uses*: every identifier it writes, bodies included.

*The key.* `IncrementalBuild.DependencyKey` takes, for unit *u* and each
unit *d* it depends on (`UnitGraph`, as before):

- *d*'s whole interface when *u* holds the entry point (it sets up every
  global and so reads every unit) or shares a module with *d* (what one
  declares there the other sees as its own);
- otherwise *d*'s modules and the fingerprints of the declarations *u* may
  use: those named `*`, those whose name *u* uses, and, closed over, those
  whose name a selected declaration mentions — a function's result type, the
  class a subclass names, a generic body's callees — across all of *u*'s
  dependencies, until nothing new is named.

Names are compared as written, without modules: a name *u* writes that is
some other declaration's too only costs a compilation that was not needed.
A declaration added, removed or changed in *d* changes *u*'s key exactly
when its name is among those, so an addition that could change *u*'s
overload resolution or name lookup (it has a name *u* writes) compiles *u*
again, and one *u* cannot see does not. What is not by name stays as it
was: every unit is keyed by every extension's fingerprint, and the
units' ABI records are compared before the link, which compiles the program
whole if two units laid a type out differently.

*Why not a query engine with red-green marking.* The work a red-green
engine saves over these keys is recomputing fingerprints that did not
change; here that is the interface phase and the key computation, 45 ms and
33 ms of a 2 s build on the 110k-line workspace. What it decides — which
units' code is stale — is what the keys decide, now at the granularity of
declarations. A long-lived engine pays off once binding is incremental
(below); until then the build's time is binding and lowering the units the
keys say are stale.

Measured on `tools/incremental/genworkspace.py`'s workspace (100
packages, 110k lines, 102 units), dev profile, `sable` built Release, load
average 7–9; before is keying by dependencies' whole interfaces (this
series' items 1–3 in both):

| Edit | Units before | Units after | Before | After |
|---|---:|---:|---:|---:|
| A function body (pkg50 / pkg0 / pkg99) | 1 | 1 | 2.0 / 2.0 / — s | 1.9 / 2.1 / 2.2 s |
| A new public function in a leaf (pkg99) | 2 | 2 | 2.1 s | 2.1 s |
| … in a package half the others use (pkg50) | 51 | 2 | 8.4 s | 2.1 s |
| … in the package every other uses (pkg0) | 101 | 2 | 13.2–14.2 s | 2.2 s |
| A used function's signature (pkg0's `run0_0`, called by 6 units) | 101 | 8 | — | 3.3 s |

The key computation (`unit graph`) takes 33 ms against 28 ms before.
`IncrementalTests.AnInterfaceChangeRecompilesOnlyTheUnitsThatUseWhatChanged`
holds the counts.

### The query engine: what is left

The cache above is a query engine with its memo tables on disk and its
dependency edges hashed into keys: each cached thing's key is the
fingerprint of exactly the fingerprints it was computed from. What a query
engine adds is a long-lived process (the language server, or a build daemon)
keeping its values in memory between revisions, and red-green marking over
recorded edges in place of recomputing every key. It is specified here
against the code that exists, so that it can be built one query at a time.

**Inputs** (re-read at each revision, compared by content hash): `text(file)`,
`options(file)` (features, target, generator attributes), `units(program)`
(the plans: which files each unit has), `settings`.

**Queries**, each a pure function of the queries it reads:

| Query | Value | Reads | Today |
|---|---|---|---|
| `parse(file)` | syntax tree | `text`, `options` | `SyntaxTree.Parse` |
| `declared_types(file)` | concrete and other type names | `parse` | part of `ConcreteTypeNames` |
| `concrete(program)` | the concrete type names | `declared_types` of every file | `ConcreteTypeNames` |
| `file_interface(file)` | `FileInterface` | `parse`, `concrete` ∩ the file's `ExtendedNames` | `InterfaceFingerprint.Interface`, `.sbi` |
| `unit_facts(unit)` | modules, imports, dotted names | `parse` heads, `file_interface.References` | `UnitFacts` |
| `unit_graph(program)` | each unit's dependencies | `unit_facts` of every unit | `UnitGraph.Dependencies` |
| `unit_interface(unit)` | hash of its files' fingerprints | `file_interface` | `unitInterfaces` |
| `extensions(program)` | hash of every extension fingerprint | `file_interface` | `extensions` |
| `declarations(program)` | bound declarations (a `Compilation`) | the compiled units' `parse`, the others' interface trees | `Compilation.Create` |
| `check(unit)` | diagnostics | `declarations`, the unit's trees | `Diagnostics` |
| `lower(unit)` | SIL, IR and ABI records | `declarations`, `unit_graph`, `unit_interface` of its dependencies, `extensions` | `SilGen.LowerUnit` |
| `object(unit)` | `.o` | `lower` | `NativeObjects.CompileIr` |
| `link(program)` | executable | every `object`, every ABI record | `NativeObjects.Link` |

**Memo entries.** Each holds the value, the fingerprint of the value, the
revision at which it was last verified, and the list of queries it read in
the order it read them, each with the fingerprint it had then.

**Red-green marking.** At a new revision an input whose hash changed is red,
the rest of the inputs green. To use a memoised query, `try_mark_green(q)`
walks q's recorded reads in order: a green read is passed over; a read not
yet verified at this revision is verified first by `try_mark_green` itself,
and if that fails it is executed again, and the new value's fingerprint is
compared with the one recorded — equal, and the read counts as green (this
is early cut-off); different, and q is red. When every read is green, q is
green and its value is reused without running it; when one is red, q runs
again, recording its reads afresh. Reads are walked in order because a later
read may only exist because of an earlier one's value (which modules a unit
imports decides which interfaces it reads).

Early cut-off is where the gain is. An edit to an ordinary body makes
`parse(file)` red, `file_interface(file)` runs again and its fingerprint is
unchanged, so `unit_interface`, `unit_graph` and `extensions` are green, and
of `lower` and `object` only the edited unit's are red (its trees are read).
A new public function changes one `unit_interface`, and `lower` is red for
exactly the units whose `unit_graph` entry lists that unit — what the keys
above already compute, but without hashing every file's interface again.

**On disk.** `file_interface` is persisted as the `.sbi`, `object` as the `.o`
and its `.abi`, keyed as they are today; a cold process starts with every
entry unverified and marks them green against what is on disk, which is
exactly today's build. The in-memory engine is the same algorithm with the
values kept.

**What blocks it.** `declarations` is all or nothing: a `Compilation` cannot
replace one tree, so any red tree rebinds every declaration (~0.2 s on the
110k-line workspace, most of a warm build). The engine pays off once
binding is per module — `module_declarations(module)` reading the
`parse` of that module's files and the `module_declarations` of what they
import, with a `Compilation` assembled from them — so that a body edit
rebinds nothing but the edited file's bodies. The language server needs the
same: it builds a package's `Analysis` from every tree of the package and
its dependencies on each snapshot (`AnalysisCache` keeps only the latest),
and would read `parse`, `declarations` and `check` from the engine, sharing
them with `sable build` in one process. Until binding is incremental, the
server would gain only `parse` (already reused per document) and the
interface trees of dependencies, which it cannot use while it navigates
into their bodies; what it takes from the cache today is that the library
checks cleanly (§5.4, "The build cache").

---

## 5.5 The registry

- Content-addressed, immutable. A published version is never mutated or
  deleted; yanking marks it unresolvable for new builds only.
- Every package signed; the lock file records the hash.
- `sable publish` requires a clean tree, a tag, passing tests, and a
  `--dry-run` that shows exactly what will be uploaded.
- A mirror/vendoring story (`sable vendor`) from day one — enterprises will not
  adopt a language whose builds require a network call to a startup's server.
- Namespacing: `org/package`, with unprefixed names reserved for the standard
  ecosystem.

**As built.** A registry is a directory or a URL (`$SABLE_REGISTRY`, a
manifest's `[registry] path`, or `--registry`). A directory registry is a
private or local one: `index/<name>.json` (the owners' keys, every key that
ever owned it, and each version with its checksum, dependencies and ECDSA
P-256 signature) and `blobs/<sha256>.tar`. A network registry serves the same
files, and an API for writing:

```
GET  config.json                     {"api": "v1"}: is it there
GET  index/<org>/<name>.json         the index, as a directory keeps it
GET  blobs/<sha256>.tar              an archive
GET  api/v1/search?q=&limit=         {"packages": [{name, version, description}]}
GET  api/v1/me                       {"user"}: whom the token stands for
PUT  api/v1/packages                 {name, entry (signed), archive (base64)}
POST api/v1/yank                     {name, version, yanked, key, signature}
POST api/v1/owners                   {name, action: add|remove, owner, key, signature}
```

Writing takes `Authorization: Bearer <token>` (`sable login` keeps it in
`~/.sable/credentials.json`, mode 0600), which says who may write to the
server and under which namespaces: a user publishes `user/…` and the `org/…`
its token names; only a token of the standard ecosystem publishes an
unprefixed name. The package's own key says who owns the package, and signs
every version, yank and owner change — the last two over text naming the
state they change, so a request cannot be replayed. The client verifies
every entry and archive as it does a directory's, so the server is trusted
to keep packages, not to vouch for them; the server checks besides that an
archive's `sable.toml` is the package and version its entry claims.
`sable registry serve --root <dir>` is the reference server, and `sable
registry token` issues its tokens (only their hashes are kept).

`publish` refuses a dirty tree (`--allow-dirty`), a commit without a tag for
the version — `v1.2.0`, `1.2.0`, or a member's `name-v1.2.0` —
(`--allow-untagged`), and failing tests (`--no-verify`). `search`,
`install` (a package's binaries, built with the release profile, into
`~/.sable/bin`), `uninstall`, `owner` and `cache list|prune|clean|warm` (§5.4,
"The build cache") exist.

What a real deployment needs beyond this: TLS in front of the server (it
speaks plain HTTP), a store that is backed up and served from more than one
machine (it is a directory), sign-up and token management for third parties
(tokens are issued by hand), a policy for who holds the standard ecosystem's
token and org names, rate limits, and a mirror (`vendor/` covers offline
builds, not a second registry).

## 5.6 Build scripts

`build.sb` runs before the package compiles, for code generation and native
library discovery. Unlike the manifest it is an ordinary program — it may
read files and run tools — so it is sandboxed by default, and the manifest
declares what it needs:

```sable
build: .script("build.sb",
               allowNet: false,
               allowExec: ["pkg-config", "glslc"],
               allowRead: ["$OUT_DIR", "shaders/"],
               allowWrite: ["$OUT_DIR"]),
```

The split is the point of §5.2: what a package *is* stays evaluable data,
and what it *does* before compiling is a sandboxed program with its powers
written down.

A build script that wants more than it declared fails rather than being
silently permitted. `cargo` not having this is a known supply-chain weakness and
there is no reason to repeat it.

**As built.** The script is compiled with the standard library and run by the
SIL interpreter, as a manifest is (§5.2.6), before any command that compiles
the package (`build`, `run`, `check`, `test`, `doc`, …), for the profile in
use. Every runtime call that touches the machine asks the sandbox first, so
the declaration is enforced, not trusted:

- It reads under `allowRead` and `allowWrite`, writes under `allowWrite`, and
  always reads and writes its own `$OUT_DIR`
  (`target/<profile>/build/<package>-<version>/out`). Paths are taken from
  the package's directory with links followed, so a link cannot lead out.
- It runs only the programs `allowExec` names. Such a program is a process of
  its own, which the interpreter cannot confine: `allowExec` is the line.
- It sees only the build's variables: `OUT_DIR`, `SABLE_PACKAGE_DIR`,
  `SABLE_PACKAGE_NAME`, `SABLE_PACKAGE_VERSION`, `SABLE_PROFILE`,
  `SABLE_FEATURES`, `SABLE_TARGET_OS`, `SABLE_TARGET_ARCH`; and its working
  directory is its own.
- A refusal stops the build, naming the path or program and the list to add it
  to; so does a trap or a type error in the script, with what it wrote to
  standard error.

What it leaves: the `.sb` files it writes under `$OUT_DIR` are compiled into
the package's library, and what it prints as `sable:link-lib=NAME`,
`sable:link-search=DIR`, `sable:rerun-if-changed=PATH` and
`sable:warning=TEXT` goes to the link (and the test harness's), the cache and
the user. It runs again only when its text, the manifest, the profile, the
features, the toolchain, or a file it read or directory it listed changed —
the sandbox records those, so they need not be declared; what a program it ran
read, it names with `rerun-if-changed`. Its `buildDependencies` are resolved
and locked with the rest, and the script is compiled with their libraries (and
theirs) — but they are not part of the package's program. Not built yet:
`allowNet` allows nothing, as there is no network library to reach.
A build script is for code that comes from outside the program, such as a
schema file, a C header or a shader. Code that comes from the program's own
declarations is written by a source generator (docs/08). A generator can read
the declarations but cannot read, write or run anything.

## 5.7 Formatting and linting

- `sable fmt` has **no options**. One style, enforced, settled by argument once
  and then never again. Go's decision, and it was correct.
- `sable check` runs the lint set. Lints are in three tiers: `error` (always
  fatal), `warn` (default on), `allow` (opt in). Configured per-package in
  `[lints]`, with `@allow(...)` / `@warn(...)` attributes for local override.
- Third-party lints are packages that implement a `Lint` protocol against the
  `SemanticModel` API, loaded as plugins by `sable check`. This only works
  because the semantic layer was designed as a product in Phase 1.
  Today a plugin is a .NET assembly of `Sable.Symbols.Lint` subclasses,
  named by `[lints] plugins = [...]` or `sable lint --plugin`.
- `sable fix` applies the fixes lints carry (`var` to `let`, an unused import
  removed, ...), and `sable fix --check` says what it would change.
