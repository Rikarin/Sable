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
sable bindgen <header.h>           generate .sabi from a C header
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

## 5.2 The manifest: `sable.toml`

```toml
[package]
name        = "render-engine"
version     = "0.4.2"
edition     = "2027"
authors     = ["..."]
license     = "MIT OR Apache-2.0"
description = "A small forward renderer"
repository  = "https://example.com/render-engine"

[lib]
path = "src/lib.sab"

[[bin]]
name = "render"
path = "src/main.sab"

[dependencies]
sable-math  = "1.2"
image       = { version = "0.6", features = ["png", "jpeg"], default-features = false }
platform    = { path = "../platform" }
shader-tool = { git = "https://example.com/st", tag = "v2.1.0" }

[dev-dependencies]
bench-harness = "0.3"

[build-dependencies]
shader-compiler = "1.0"

[features]
default   = ["vulkan"]
vulkan    = ["dep:vulkan-sys"]
metal     = ["dep:metal-sys"]
debug-gpu = ["vulkan?/validation"]

[profile.release]
opt-level        = 3
lto              = "fat"
codegen-units    = 1
panic            = "abort"
strip            = "debuginfo"
arc-checks       = false       # unowned(safe) traps
exclusivity      = "unchecked"
overflow-checks  = false

[profile.dev]
opt-level        = 0
debug            = true
arc-checks       = true
exclusivity      = "checked"
overflow-checks  = true
leak-check       = true        # heap walk at exit

[reflect]
# types reachable only through reflection must be named here or marked
# @reflectable, or the linker will strip their metadata
include = ["Scene", "Material", "App.Config.*"]

[workspace]
members = ["crates/*", "tools/shader-tool"]
exclude = ["experiments"]

[targets.wasm32-unknown-unknown]
runtime = "minimal"
reflect = false
```

### Deliberate departures from Cargo

- **`[reflect]`** is new and necessary: reflection reachability has to be
  declared somewhere, and the manifest is the honest place for it.
- **Memory-model knobs in profiles** (`arc-checks`, `exclusivity`,
  `leak-check`) — these are the ones programmers will actually want to toggle.
- **`edition` is a year, and editions may change syntax** but never ABI. A
  package declares its edition; packages of different editions interoperate.

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

### Separate compilation and `.sabi`

Each module emits an interface file containing:

- Public and `package` declarations with full signatures
- Generic bodies eligible for cross-module specialisation
- `@inline(always)` bodies
- **Escape summaries** (the key one — see `02-memory-model.md §2.2`)
- Layout information for `@frozen` types
- Conformance records

`.sabi` is a binary format with a stable version header, designed so that a
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
signatures and the bodies other units lower — not yet a `.sabi` file, and an
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

`build.sab` runs before the package compiles, for code generation and native
library discovery. It is sandboxed by default and declares what it needs:

```toml
[build]
script       = "build.sab"
allow-net    = false
allow-exec   = ["pkg-config", "glslc"]
allow-read   = ["$OUT_DIR", "shaders/"]
allow-write  = ["$OUT_DIR"]
```

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
