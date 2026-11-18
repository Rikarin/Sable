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
sable test [filter] [--doc]        run @test functions and doc examples
sable bench [filter]               run @bench functions, compare to baseline
sable check                        type-check only, no codegen (fast)
sable fmt [--check]                format; no options, ever
sable doc [--open] [--json]        generate documentation + symbol graph
sable add <pkg>[@<ver>] [--dev]    add a dependency, update the lock
sable remove <pkg>
sable update [pkg]                 re-resolve within semver bounds
sable tree                         print the resolved dependency graph
sable publish [--dry-run]          publish to the registry
sable clean                        remove the target directory
sable bindgen <header.h>           generate .sbi from a C header
sable repl                         interactive, backed by the const evaluator
sable lsp                          language server (stdio)
sable explain <S1234>              print the full documentation for a diagnostic
sable toolchain <install|list|pin>  manage toolchain versions
```

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

    // Types reachable only through reflection; see "Deliberate departures".
    reflect: ["Scene", "Material", "App.Config.*"],

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
- `.git` dependencies are written and kept, but the resolver refuses them
  until it fetches from git (#201); everything else above builds today.
- Field names are the TOML keys in Sable's spelling (`defaultFeatures`,
  `optLevel`); every table of the TOML format has a field. Enumerated
  settings are enum cases (`.fat`, `.abort`), so a misspelling is a type
  error, not a silently ignored string.

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
and `exclude`; `shared` and `.workspace` wait on the manifest model learning
inheritance, which `sable.toml` never had either.)

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
   editor has completion, hover and type errors in `package.sb`. It does not
   evaluate manifests or apply the effect rules, and still does not read the
   dependencies they declare (#192).

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

- Workspace inheritance: `shared` and `.workspace` (§5.2.3).
- Per-target settings (`[targets.*]`: runtime profile, reflection), waiting on
  cross-compilation; `when:` covers `os()` and `arch()` only.
- The language server evaluating manifests and applying the effect rules
  (#192); it type-checks them.
- Compile-time evaluation of `const` and `comptime` in ordinary code (#172).
  The manifest evaluator runs the interpreter directly, under its own rules,
  and does not wait on it.

### Deliberate departures from Cargo

- **`reflect:`** is new and necessary: reflection reachability has to be
  declared somewhere, and the manifest is the honest place for it.
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
- Layout information for `@frozen` types
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
where the fingerprint is the unit's content and the program's interface. The
interface is a hash of each file's public surface — declarations,
signatures and the bodies other units lower — not yet a `.sbi` file, and an
interface change invalidates every unit rather than only the dependents.
Clang runs on units in parallel; lowering is still one unit at a time.

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

## 5.7 Formatting and linting

- `sable fmt` has **no options**. One style, enforced, settled by argument once
  and then never again. Go's decision, and it was correct.
- `sable check` runs the lint set. Lints are in three tiers: `error` (always
  fatal), `warn` (default on), `allow` (opt in). Configured per-package in
  `[lints]`, with `@allow(...)` / `@warn(...)` attributes for local override.
- Third-party lints are packages that implement a `Lint` protocol against the
  `SemanticModel` API, loaded as plugins by `sable check`. This only works
  because the semantic layer was designed as a product in Phase 1.
