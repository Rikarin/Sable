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
sable test [filter] [--doc] [-j n] [--timeout s] [--coverage]  run @test functions and doc examples
sable bench [filter]               run @bench functions, compare to baseline
sable fuzz [target] [--time 8h]    fuzz a @fuzz function with libFuzzer; --replay, --minimise
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
sable cache <list|gc|clean>        the package cache, and collecting it
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

`sable test --coverage` counts what of the package's own code (not
`tests/`, dependencies or the library) the tests run: each line, and each
outcome of every decision — both ways of an `if`, `guard`, `while` and
`?:`; each arm of a `match` and case of a `switch` (and none, without a
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

`sable fuzz` fuzzes a `@fuzz` function — one that takes the fuzzer's bytes,
`U8[:]`, and fails by trapping or throwing (S3104 says what else it may not
be) — of the package (its library, `tests/` and `benches/`) or of the files
named. The program is built as a library: the compiler writes a harness whose
`@export` functions take an input a byte at a time and run the target on it,
and a C shim is libFuzzer's entry point. clang compiles the IR, the runtime
and the shim with `-fsanitize=fuzzer,address,undefined`: SanitizerCoverage
counts every edge of the Sable code with no rewriting, and AddressSanitizer
and UBSan watch the C runtime and what crosses to C. libFuzzer runs in fork
mode (`-j n` processes), so a failure does not end the run: until `--time`
is up or Ctrl-C, it keeps the inputs that reach new code in
`target/fuzz/<Module.function>/corpus/` (seeded from the package's
`fuzz/<Module.function>/`, if there is one) and those that fail in
`crashes/`. Then each failure is replayed, minimised (`.min`), its report kept
(`.txt`), and the failures grouped by what they say — a trap's message and
place, a sanitizer's summary, a hang — into `summary.txt`; the command exits
1 while a saved input still fails. `--replay [input...]` runs the saved inputs
(or those given) once each, and `--minimise <input>` shrinks one; `--list`
lists the targets; what follows `--` goes to libFuzzer. libFuzzer's runtime
comes with LLVM's clang; Apple's has none, so on macOS fuzzing says so (and
how to get an LLVM clang) before it builds anything, and `--replay` — built
with its own `main` and the sanitizers — is what runs there.

`sable bindgen` writes Sable source, not `.sbi` (which waits on the
interface file format of §5.4): a module of `@extern("c")` functions,
structs and integer constants, read from the header by clang itself. What
cannot cross to C yet — pointers, arrays, unions, variadic functions — is
listed in a comment, until `Sable.Interop` (#74). `sable toolchain` installs
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
        .registry("metal-sys", "0.9", optional: true, when: .os(.macOS)),
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
  data that the resolver applies per target: `when: .os(.macOS)`,
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
       `when: .os(.linux)` on a dependency (docs/05 §5.2.4)
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
(0.54–0.62 s against 0.52–0.58 s eagerly). The standard library is not
read from a `.sbi` at all: it is the embedded source, parsed whole (every
module's, 247 files) on each build.

Loading symbols without their source is blocked by what reads the source
behind a symbol, not by the binder's tables alone: a symbol's
`DeclaringSyntax` being empty means *synthesised* to lowering (the implicit
initialisers and `contains` of `SilGen`), attributes such as
`@inline(always|never)` and a type's reflected attributes are read from the
declaration's syntax where they are used, value-generic predicates, enum raw
values and default arguments are expressions, `SilProgram.Index` finds the
declarations to lower or declare by walking every file's syntax, and the
bodies another unit lowers — generic functions' instances, `@inline`
bodies, defaults, `const fn`s and, for the entry unit, every global's
initialiser — are bound from their file's source in its file scope. So a
symbol `.sbi` needs, first, every such fact moved onto the symbols (an
`IsSynthesised` flag, attributes, predicates and raw values as bound
values), lowering's index built from symbols, and the bodies other units
lower kept as something lowering can take without the file — serialised
bound trees or SIL, as Swift's modules keep inlinable bodies as SIL. Only
then can `Compilation.Create` take a unit's symbols beside other units'
trees; per-module binding (`module_declarations`, below) is not needed
first, but needs the same symbols-without-source.

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
The runtime is compiled once per set of options and kept, in the build cache
(or `$SABLE_HOME/cache/runtime` for a single file).

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

### The query engine: design (not built)

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
into their bodies.

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
`~/.sable/bin`), `uninstall`, `owner` and `cache list|gc|clean` (packages no
build used, and evaluated manifests, in `--max-age` days) exist.

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
