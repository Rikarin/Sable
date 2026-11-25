# 4. Implementation Plan

Seven phases. Roughly 30 months to a self-hosted 1.0 with 2–4 engineers, or
proportionally longer solo. Each phase ends in a **shippable artifact** and a
**go/no-go decision** — the plan is designed so that stopping after any phase
leaves something useful rather than a half-finished compiler.

---

## 4.0 Architecture: Roslyn, with a SIL layer

Roslyn's architecture is the right starting point, but Roslyn targets a managed
runtime and therefore goes straight from its bound tree to IL. A native,
ARC-managed, borrow-checked language needs a **mid-level IR** between the bound
tree and LLVM, for the same reason Swift needed SIL: ARC optimisation, borrow
checking, exclusivity, region analysis and devirtualisation are all
high-level-enough to need type and ownership information that LLVM IR has
already thrown away.

```
   source text
        │
   ┌────▼──────────────────────────────────────────┐
   │ 1. SYNTAX LAYER            Sable.Syntax        │   immutable red/green trees
   │    Lexer → Parser → SyntaxTree                 │   full fidelity, incremental
   └────┬──────────────────────────────────────────┘
        │  SyntaxTree (lossless; ToFullString() == source)
   ┌────▼──────────────────────────────────────────┐
   │ 2. SYMBOL LAYER            Sable.Symbols       │   declaration table,
   │    Compilation, SemanticModel, lookup          │   lazy symbol creation
   └────┬──────────────────────────────────────────┘
        │  Symbols + SemanticModel (the public API surface)
   ┌────▼──────────────────────────────────────────┐
   │ 3. BINDING                 Sable.Binder        │   name resolution,
   │    Binder → BoundTree, type inference          │   overload resolution,
   └────┬──────────────────────────────────────────┘   constraint solving
        │  BoundTree (typed, still tree-shaped)
   ┌────▼──────────────────────────────────────────┐
   │ 4. LOWERING                Sable.Lowering      │   closures, async, match,
   │    BoundTree → SIL                             │   generics, defer, using
   └────┬──────────────────────────────────────────┘
        │  SIL — SSA, typed, ownership-annotated, CFG
   ┌────▼──────────────────────────────────────────┐
   │ 5. SIL PASSES              Sable.SIL           │   ★ the interesting part
   │    diagnostics → optimisation                  │
   └────┬──────────────────────────────────────────┘
        │  optimised SIL
   ┌────▼──────────────────────────────────────────┐
   │ 6. IRGEN                   Sable.CodeGen       │   LLVM IR via LLVMSharp
   │    SIL → LLVM IR → object files                │
   └────┬──────────────────────────────────────────┘
        │
   ┌────▼──────────────────────────────────────────┐
   │ 7. LINK                    lld + Sable.Runtime │
   └───────────────────────────────────────────────┘
```

### The SIL passes, in pipeline order

| # | Pass | Kind | Phase |
|---|---|---|---|
| 1 | Definite assignment | diagnostic | 1 |
| 2 | Move checking (`@noncopy`) | diagnostic | 3 |
| 3 | **Root inference / borrow check** | diagnostic | 3 |
| 4 | Exclusivity (static + marker insertion) | diagnostic | 3 |
| 5 | Exhaustiveness (match) | diagnostic | 1 |
| 6 | Reachability / definite return | diagnostic | 1 |
| 7 | Mandatory inlining (`@inline(always)`, `const fn`) | opt | 2 |
| 8 | Closure specialisation | opt | 3 |
| 9 | Generic specialisation (monomorphisation) | opt | 2 |
| 10 | Devirtualisation (witness tables, `sealed`, `final`) | opt | 4 |
| 11 | **ARC: local retain/release pairing** | opt | 3 |
| 12 | **ARC: borrow-informed elimination** | opt | 3 |
| 13 | **ARC: release hoisting / loop motion** | opt | 4 |
| 14 | **Escape analysis → stack promotion** | opt | 4 |
| 15 | COW uniqueness propagation (`isUnique` folding) | opt | 4 |
| 16 | Region promotion (heap → region → stack) | opt | 4 |
| 17 | Inline-layout materialisation | lowering | 3 |
| 18 | Capture promotion / box elimination | opt | 3 |

Passes 11–16 are where the performance story lives. Budget real time for them;
they are not "optimisations we can add later" — the language's cost model is a
lie without them.

### Why C# for the host compiler

- Roslyn is the reference implementation of the architecture being copied, and
  reading it is the fastest way to get the syntax and symbol layers right.
- LLVMSharp gives working LLVM-C bindings without writing them.
- The tooling story (debugging a compiler, profiling it, writing 50,000 tests)
  is excellent.
- Self-hosting later is the plan, not a regret.

**Target: .NET 10, C# 14.** Use `readonly record struct` aggressively for SIL
nodes, `Span<T>` throughout the lexer, source generators for the syntax-node
boilerplate (Roslyn generates its node classes from an XML schema — do the same,
from a JSON schema derived from `grammar.ebnf`).

---

## Phase 0 — Foundations and the syntax layer
**~3 months. Deliverable: a parser, a formatter, and a syntax-tree explorer.**

### 0.1 Repository and process

```
sable/
├── sable.sln
├── src/
│   ├── Sable.Syntax/          # lexer, parser, trees, trivia
│   ├── Sable.Symbols/         # symbol model, declaration table
│   ├── Sable.Binder/          # binding, inference
│   ├── Sable.SIL/             # IR, passes
│   ├── Sable.CodeGen/         # LLVM lowering
│   ├── Sable.Driver/          # compiler entry point
│   ├── Sable.Build/           # package manager, build graph
│   ├── Sable.Tools.Fmt/
│   ├── Sable.Tools.Lsp/
│   └── Sable.Runtime/         # C — the native runtime (not C#)
├── stdlib/                    # written in Sable from Phase 4
├── tests/
│   ├── Syntax.Tests/
│   ├── Semantic.Tests/
│   ├── lit/                   # FileCheck-style end-to-end tests
│   └── corpus/                # .sb files that must parse/compile
└── docs/
```

Decisions to make now, because changing them later is expensive:

- **Error codes** are `S####`, allocated in ranges: `S0001–0999` lexical,
  `S1000–1999` syntactic, `S2000–2999` name resolution, `S3000–3999` types,
  `S4000–4999` ownership/borrow, `S5000–5999` concurrency, `S6000–6999`
  patterns/exhaustiveness, `S7000–7999` codegen, `S8000–8999` linking,
  `S9000+` internal. Every code gets a documentation page from day one.
- **Diagnostics carry structured data**, not formatted strings — spans,
  related locations, and machine-applicable fix-its. The LSP and the CLI render
  the same object.
- **Every test is a `.sb` file plus expected output.** Directive-driven, lit
  style. 50 handwritten unit tests will not find what 5,000 corpus files find.

### 0.2 Lexer

- `Span<char>`-based, zero-allocation for the common path.
- Interned identifiers and keywords (a perfect-hash keyword table).
- Full trivia capture with the round-trip invariant tested by property test:
  *parse any corpus file, concatenate `ToFullString()`, assert byte equality.*
- String interpolation via a brace-depth stack; raw strings with hash counts.
- The newline rule from `lexical.md §4`, implemented as a small state machine
  over (previous significant token, next significant token, bracket depth).

### 0.3 Syntax tree

Copy Roslyn's **red/green tree** design exactly:

- **Green nodes**: immutable, position-independent, width-only, aggressively
  shared across trees. This is what makes incremental reparse cheap.
- **Red nodes**: lazily created facades carrying parent pointers and absolute
  positions.
- Nodes generated from a schema file by a source generator — about 180 node
  kinds; hand-writing them is six weeks of pure tedium and guaranteed drift.

### 0.4 Parser

- Recursive descent, hand-written. No parser generator — error recovery and
  incremental parsing both need hand control, and every production compiler
  that matters is hand-written for this reason.
- **Error recovery first, not later.** Skipped tokens become trivia on the next
  node; missing tokens are inserted as zero-width `IsMissing` nodes. An IDE
  parses broken code 100% of the time.
- **Incremental reparse** from a changed text span plus the old tree, reusing
  green nodes outside the damaged region. Build it in Phase 0 — retrofitting it
  is a rewrite.
- Parser-directed disambiguation for `<` (generics vs comparison) and `&`
  (bitand vs address-of vs reference type), per `lexical.md §7`.

### 0.5 Exit criteria

- [x] Every construct in `SYNTAX.md` parses — `SyntaxSpecTests` parses each of
      its 59 `sable` blocks as a file, a function body, or piece by piece
      (listings of types, patterns and literals item by item); a block that is
      deliberately not Sable would be marked `<!-- not-sable: why -->` in the
      spec, and none is
- [ ] Round-trip invariant holds on a 2,000-file corpus — it holds on all
      the corpus there is: `RoundTripCorpusTests` takes every `.sb` file
      (140) and every `sable` block in the Markdown (418), broken ones
      included, each whole and cut short at seven points, and requires the
      text back exactly and spans that tile (it found the lexer dropping a
      byte order mark). 558 inputs, not 2,000 files
- [x] Incremental reparse of a 10,000-line file after a one-character edit
      completes in < 2 ms — about 1 ms in Release (0.9 ms at best, 1.2-1.5 ms
      median under heavy load), against 20-35 ms for a full parse
- [x] `sable fmt` is idempotent and reproduces the corpus modulo normalisation
      — `FormatterRepositoryTests` holds `lib/`, `examples/`, `tests/corpus/`
      and `tests/programs/` to being formatted already
- [x] A syntax-tree visualiser exists (Roslyn's Syntax Visualiser equivalent)
      — `tools/Sable.Visualiser`
- [ ] Fuzzer runs for 24h without a crash or an assertion failure — the
      fuzzers exist and run nightly (`.github/workflows/fuzz.yml`, a new
      seed each night, `SABLE_FUZZ_SEED`/`SABLE_FUZZ_SCALE` to reproduce):
      the parser's random-input round trips; `CompilerFuzzTests`, which
      mutates the repository's programs at token boundaries (a token for
      another of its kind, runs deleted, duplicated, spliced, swapped)
      through parsing, binding, the semantic model, constant evaluation,
      lowering, the SIL verifier and the passes, the printed SIL read back,
      async lowering and the optimising passes, with a time limit per
      input and failures minimised by delta debugging; and
      `ResolverFuzzTests`, random registries (cycles, self-dependencies,
      yanked versions, pre-releases, locks naming what is gone) through
      PubGrub, every answer checked against every requirement and every
      failure without pre-releases against trying every assignment, plus
      the version parsers and the set algebra. Beside them,
      `tools/fuzz/coverage.sh` fuzzes guided by code coverage: SharpFuzz
      instruments the Sable assemblies, and libFuzzer (on Linux, through
      libfuzzer-dotnet; on macOS, the fuzzer's own in-process loop) drives
      the parser (round trips, spans that tile, incremental reparse equal to
      a full one), the language server's documents (edits by line and
      column equal to a full parse), the front end (as `CompilerFuzzTests`
      holds it), SIL as text (read, printed and read back, verified, through
      the passes), the resolver and the package files, and `fromBytes` under libFuzzer with
      ASan and UBSan, from the repository's sources and a dictionary from
      the lexer's tables, nightly for 90 minutes a target with the corpus kept from
      night to night. No fuzzer has yet run for 24 hours on end. Found and
      fixed so far: an extension nested in an extension threw while
      attaching; a self-dependency hung the resolver and crashed its
      explanation; a `-` in a requirement's build metadata threw; ranges
      past the largest number wrapped round to nothing; an edit in a file
      with directives reparsed incrementally to a different tree; the
      SIL of a program with an `async let`, async lowered, did not read back
      (a frame named for `$task.run`)

---

## Phase 1 — Symbols, binding, and the type checker
**~4–5 months. Deliverable: a front end that fully type-checks the language,
with no code generation.**

### 1.1 Symbol layer

- `Compilation` as the unit of semantic analysis: syntax trees + references +
  options, immutable, with `WithX()` producing a new one.
- **Lazy, demand-driven symbol creation** with cycle detection. Do not build a
  full symbol table eagerly — the IDE needs to answer "what is this identifier"
  without binding the world.
- Declaration table built from the syntax trees, keyed by name, supporting
  partial construction and incremental invalidation.
- `SemanticModel` as the public API: `GetSymbolInfo`, `GetTypeInfo`,
  `GetDeclaredSymbol`, `AnalyzeDataFlow`, `AnalyzeControlFlow`. **This is a
  product, not an internal detail** — the IDE, the formatter, the linter, the
  refactoring engine and the documentation generator all consume it.

### 1.2 Binder

- Scoped binder chain (module → file imports → type → function → block →
  statement) mirroring Roslyn's.
- Overload resolution per `SYNTAX.md §5`, with the candidate set and the
  rejection reason for each candidate retained for diagnostics.
- Name lookup with shadowing rules, extension-member lookup, protocol-member
  lookup.

### 1.3 Type checker

- The bidirectional constraint solver of `03-type-system.md §3.5`.
- **Build the solver-step budget in from the first commit.** A hard limit plus a
  diagnostic naming the offending subexpression.
- Conformance checking with a demand-driven conformance graph.
- Generic signature validation, same-type constraint unification (a union-find
  over type parameters), associated-type inference.
- Exhaustiveness via Maranget's usefulness algorithm, producing witnesses.
- Definite assignment, reachability, and definite-return analyses on the bound
  tree (these move to SIL later; doing them on the bound tree first is faster
  to build and the tests carry over).

### 1.4 Exit criteria

- [x] The full type system of `03-type-system.md` is checked
- [x] Negative-test corpus: ≥ 600 programs that must fail, each asserting an
      exact error code, span, and message
- [x] `AnalyzeDataFlow` is correct on a 300-case corpus — 105 hand-written
      cases plus 195 straight-line programs checked against an independent
      oracle, over a control-flow graph (`ControlFlowGraph.cs`)
- [x] Type-checking 100,000 lines completes in < 5 s single-threaded
- [x] **Go/no-go: is the language actually pleasant?** Write 10,000 lines of
      real Sable (a JSON parser, an HTTP client, a small game) against the type
      checker alone, before any backend exists. Fix the language now, not later.
      — about 12,900 lines that type-check: the standard library (`lib/`,
      6,600, its JSON among them), `tests/corpus/` (2,900), a JSON parser in
      `tests/programs/` (2,100) and `examples/` (1,300, a static site
      generator among them). The HTTP client and the game wait on `Sable.Net`
      and a graphics binding.

---

## Phase 2 — SIL and the first native binary
**~4 months. Deliverable: `sable build hello.sb && ./hello`.**

### 2.1 SIL design

Typed SSA with explicit ownership. Instruction set, roughly 70 opcodes:

```
  ; memory
  alloc_stack $T                    dealloc_stack %0
  alloc_box $T                      project_box %0
  alloc_region                      region_alloc %r, $T        dealloc_region %r
  load [copy|take|trivial] %0       store %1 to [init|assign] %0

  ; ownership
  copy_value %0                     destroy_value %0
  begin_borrow %0                   end_borrow %0
  move_value %0
  strong_retain %0                  strong_release %0
  begin_access [read|modify] [static|dynamic] %0     end_access %0

  ; aggregates
  struct $T (%0, %1)                struct_extract %0, #field
  struct_element_addr %0, #field
  union $T, #case, %0               union_extract %0, #case
  switch_union %0, case #A: bb1, case #B: bb2

  ; calls and dispatch
  function_ref @f                   apply %f(%0, %1)
  class_method %0, #m               witness_method $T : $P, #m
  partial_apply %f(%0)

  ; control flow
  br bb1(%0)                        cond_br %c, bb1, bb2
  return %0                         throw %0                   unreachable
```

Key properties:
- **Ownership is explicit in the IR.** Every value is `@owned`, `@guaranteed`,
  or `@trivial`. A verifier checks that every `@owned` value is consumed exactly
  once on every path. This single invariant catches most ARC codegen bugs
  before they reach LLVM.
- **A SIL verifier runs after every pass in debug builds.** Non-negotiable.
  A debug build of the compiler, and every test run whatever the compiler's
  build (the suites set `SABLE_VERIFY_PASSES=1`); a release build of it
  verifies once, after the passes, where verifying after each was two thirds
  of an optimised standard library's pass time (docs/05 §5.4).
- **SIL is textually printable and parseable.** Tests are `.sil` files. This is
  the difference between a debuggable compiler and a mystery.

### 2.2 Lowering (BoundTree → SIL)

Order matters; each of these is a self-contained lowering:

1. `defer` / `using` → explicit cleanup on every exit path
2. `match` → decision tree (Maranget's compilation, not a linear chain)
3. Closures → boxes + `partial_apply`; non-escaping closures stay on the stack
4. `async` → a state machine: the coroutine split generators use, made
   after the SIL passes — see "Async, as implemented" under Phase 4
5. `throws` → a discriminated return with an error register, plus the error
   edges in the CFG
6. Generic instantiation → monomorphised SIL functions
7. Property accessors, subscripts, operators → ordinary calls
8. Optional sugar (`?.`, `??`, `!`) → explicit `switch_union`

### 2.3 LLVM code generation

- **LLVMSharp** against LLVM 19+. Emit object files directly; no `.ll` text in
  the hot path (but keep `--emit=llvm-ir` for debugging).
- Type lowering: Sable types → LLVM types, with the value-witness table for
  anything whose layout is not statically known.
- Calling convention: the platform C convention, plus Sable-specific rules —
  `self` in a dedicated register where one is free, error register for
  `throws`, `@guaranteed` parameters not retained.
- Debug info (DWARF / CodeView) from the first day a binary is produced.
  Retrofitting debug info is awful; emitting it alongside is easy.
- Targets in order: `arm64-apple-darwin` → `x86_64-linux-gnu` →
  `aarch64-linux-gnu` → `x86_64-windows-msvc`.
- **As built.** `sable build --target <triple>` (and `--sysroot <dir>`) for a
  file or a package; a package's output goes to `target/<triple>/<profile>/`.
  `NativeTarget` parses the triple (aarch64/arm64, x86_64, wasm32; darwin,
  linux, windows, wasi) and asks clang, once per triple, for the target's
  `target datalayout`, normalised triple and CPU features, which head the
  program's IR — the layout clang compiles the runtime with. The IR reads
  the runtime's object, string and array headers through typed GEPs, so 32-bit
  pointers lay them out as C does; `#if os()`/`arch()`, `Platform.*` (the
  core library is parsed per target), a dependency's `when:` and the C ABI's
  classification follow the target. Every target compiles to an object
  (`--emit obj`); an executable links where the runtime has a platform layer —
  `runtime/platform_posix.c` (macOS, Linux: kqueue or epoll),
  `platform_win32.c` (Windows: an I/O completion port, SRW locks) and
  `platform_wasi.c` (WASI preview 1: one thread, so no executor pool and the
  blocking pool's work runs inline; no reactor, sockets or processes; a trap
  exits 134). For another machine the target's C library comes from
  `--sysroot`, else `$SABLE_HOME/sysroots/<triple>`, else for WASI
  `$WASI_SYSROOT`, wasi-sdk's or wasi-libc's installed sysroot; a build that
  finds none says so and where it looked, not as an internal error, and the
  compiled runtime is cached by target and sysroot. A program can also be a
  library C links (`--emit staticlib|dylib`, `[lib] kind`; docs/05 §5.2.2):
  no entry point, its globals set up by a constructor and destroyed by the
  runtime at exit. CI builds and tests on Linux x64 and arm64, macOS arm64
  and Windows x64, and runs WASI programs under wasmtime. Not yet: WASI
  threads (`wasm32-wasip1-threads`) and sockets (preview 2).

### 2.4 Minimal runtime (C)

~3,000 lines: allocation, refcounting, metadata records, error propagation,
`abort`, and a `main` shim. See `06-runtime-and-stdlib.md`.

### 2.5 Exit criteria

- [x] Hello world links and runs on macOS arm64 and Linux x64 — and so does
      every native test, the JSON exit program included: on Linux x64 in a
      container (`tools/linux/test.sh`, Ubuntu clang 21), each builds, runs
      and agrees with the interpreter
- [x] A 2,000-line program (JSON parser + pretty printer) compiles and passes
      its tests — `tests/programs/json.sb`, 2,100 lines: strict RFC 8259
      parsing with line/column errors and opt-in comments, trailing commas,
      duplicate-key and depth checks; compact, pretty, sorted and ASCII-only
      printing; JSON Pointer (RFC 6901); Merge Patch (RFC 7386); comparison,
      transforms and statistics. Its own suite of 237 checks passes natively
      and in the interpreter, which agree, with nothing leaked
- [x] SIL verifier is clean on the corpus — every function in
      `tests/corpus`, `tests/programs` and `examples` lowers, and the
      verifier is clean before and after the passes, ownership included
      (every owned value consumed exactly once on every path).
      `CorpusLoweringTests` now fails on any construct that does not lower,
      not only on what lowers wrong, and `tools/lowering-probe` lists type
      errors and lowering gaps across the corpus (none). Closing the last
      24 gaps and the examples' 47 type errors took:
      - `==` and `!=` on enums, by case;
      - `%` on floats (`frem`);
      - untyped `throws` (an `any Error` box class), `throws(A | B)` (an
        enum per union), `rethrows`, and the conversions between error
        types at a handler, `From` included;
      - thunks for a function used as a throwing function type;
      - `some P` parameters, as anonymous generic parameters;
      - user subscripts, getters and setters;
      - `T[..k]` outside an inline layout, with its bound checked;
      - local functions, capturing or not, recursive;
      - `#file`, `#line`, `#column`, `#function` and `#module`;
      - recursive unions, generic or not, a self-holding case boxed;
      - structural unions as values (`F64 | String`: injection, `is`,
        narrowed reads);
      - static computed properties, and an initialiser assigning its type's
        `let` fields.

      Two gaps the corpus never reached were closed later: a store to a
      computed property (`t.fahrenheit = 32`, and `+=` through one) calls
      its setter, as a subscript's store does; and a record literal that
      leaves out a field with an initial value takes it. With them came
      `var area: F64 => w * h` and properties kept in `field` (SYNTAX.md
      §13): a private stored field of the type, `<name>$field`, that only
      the property's accessors name, started by the property's initialiser.

      Four corpus functions called `removeLast()` in a getter where they
      meant to index. Now that `removeLast` is `mutating`, the binder
      rejects them, and they read the element. The three examples were
      rewritten against the library that exists: no `Files`, HTTP or JSON
      module of their imagining. They run natively and interpreted, and
      `ExampleProgramTests` checks their output. Classes are complete as docs/03 describes them (`tests/.../ClassTests.cs`):
      inheritance, overrides and `super`, `abstract` methods (a trapping
      vtable slot), protocol requirements with defaults in extensions
      (dispatch falls back to the default), stored properties with initial
      values (stored first by every initialiser of the declaring type; an
      unannotated one's type read off a literal, a record or an initialiser
      call), `private(set)`, static stored properties and module-level
      variables (`sil_global`, initialised in order as the entry point
      starts, destroyed last first as it returns), and `as?`, `as!` and `is`
      (the superclass chain in the class metadata). Async and actors lower
      too — see "Async, as implemented" under Phase 4
- [x] `lldb` shows correct frames, locals and types — on macOS, checked by a
      test that drives lldb: `gcd(a=1071, b=462)`, `(Int) t = 147`,
      `(Debug.Point) p = (x = 1071, y = 462)`
- [x] Startup time < 2 ms, hello-world binary < 400 KB stripped — on macOS
      arm64, 35 KB stripped and 1.69 ms median start to exit
      (`tools/startup/spawn.c`: posix_spawn to waitpid), against 1.59 ms for
      the same program in C and 0.95 ms for `/usr/bin/true`; the earlier
      ~3.2 ms included the measuring script's own spawn cost

---

## Phase 3 — The memory model
**~5 months. Deliverable: the language's actual value proposition.**

This is the highest-risk phase. Everything before it is well-trodden.

### 3.1 ARC (~6 weeks)

1. Naive insertion: retain on copy, release at end of lifetime. Correct, slow.
2. Verifier: every `@owned` value consumed exactly once per path.
3. Local pairing: remove retain/release pairs with no intervening escape.
4. `@guaranteed` calling convention for `self` and borrowed parameters.
5. `weak` via a side table; `unowned` via a second count in the header.
6. `deinit` ordering, `@immortal` fast path, atomic/non-atomic selection.

Status:
- [x] 1, 2 — from Phase 2: the lowering's explicit ownership and the
      verifier's consumed-once rule, which every pass is held to.
- [x] 3 — `ForwardCopies` and `BorrowLoads` (`src/Sable.SIL/ArcPasses.cs`):
      copies only ever borrowed, or outliving their source, are removed; a
      `load [copy]` only ever borrowed becomes `load_borrow`/`end_borrow`,
      across a branch on it too, where nothing on the way may write the
      storage — a slot by anything given its address, other storage by a
      call or a release that could run a deinit (`SilModule.MayRunDeinit`).
      The verifier checks a slot is not written while it is borrowed.
- [x] 4 — parameters are guaranteed; an initialiser's are `@owned`, so a
      caller's temporary is handed over rather than copied in, and the one
      copy an initialiser stores is forwarded into the parameter.
- [x] 5 — `weak var x: C?` and `unowned var x: C` on class fields:
      `@sil_weak`/`@sil_unowned` storage, `load_weak`/`store_weak`,
      `load_unowned`/`store_unowned`; weak references share a side-table
      entry per object, cleared when it dies; unowned ones count in the
      header's flags word, holding the memory, not the object, and trap on
      a read once it is gone. A struct's weak or unowned field is the same
      storage, copied and destroyed by the struct's witnesses as a weak or
      unowned retain and release; as a value it is made with
      `weak_copy_value`/`unowned_copy_value` and read with
      `strong_copy_weak_value`/`strong_copy_unowned_value`, in place with
      `load_weak`/`load_unowned`.
- [x] 6 — deinit ordering (subclass first, fields last to first), the
      immortal fast path, and a stack fast path (§3.7: an object made in
      its frame is counted as any other, and never freed). Atomic counts
      came with the concurrency runtime's pool: the flag bit of docs/02
      §2.8, set where a value crosses tasks (see "Async, as implemented").

Measured by `tools/bench/run.py` (`benchmarks/history.tsv`): against naive
insertion, retains fall by 75% (binary trees), 92% (sort), 77% (shapes),
71% (JSON), 49% (words) and 100% (n-body), and time by 7–44%; since then,
by 100% (binary trees), 99.7% (shapes), 90% (words), 29% (sort) and 40%
(JSON) more — see §3.8 for how, and for the overhead that is left.

### 3.2 Move checking (~3 weeks)

Dataflow over SIL for `@noncopy` types: every value consumed exactly once on
every path, with the consuming site reported in the use-after-move diagnostic.
This is a straightforward dataflow problem and it is a good warm-up for 3.3.

Status: done for variables and parameters (`src/Sable.SIL/MoveChecker.cs`).
`@noncopy` structs and unions, with `deinit` for structs; `consuming`
parameters (owned, and kept in a slot when move-only); `move x` for any
type. The lowering borrows a move-only variable with `load_borrow`, so a
borrow never copies — a copy destroyed would run the deinit — and consumes it
with a load the checker turns into `load [take]`; a forward dataflow then
reports a read or a move where the variable may already have been moved,
naming the move (`'a' is used after its value was moved out of it, at line
16`), and any copy left — a borrowed parameter consumed, a part moved out —
is an error. Natively a struct with a deinit carries a hidden live byte, so
the storage a move empties is not deinited. Locals now die at the end of the
block that declared them, on every way out, which a move-only value needs and
deterministic destruction (docs/02 §2.2) asked for. Deferred: partial moves
out of fields, `using`, and move-only values in arrays or generic
parameters — none is needed by anything Phase 3 delivers, and each is a
use of the same dataflow rather than a new one.

### 3.3 Root inference / borrow checking (~10 weeks) ★

The novel pass. Algorithm sketch:

1. **Anchor set.** Assign each SIL value an anchor set — a set of static roots
   (`param(i)`, `local(x)`, `region(r)`, `static`, `heap`).
2. **Propagation.** A forward dataflow over the SSA graph: `load` from a place
   inherits that place's anchors; `struct_extract` inherits the aggregate's;
   `apply` uses the callee's **escape summary** to map argument anchors to
   result anchors.
3. **Checking.** At every `store`, `return`, and region-escaping `apply`,
   verify that the destination's lifetime is outlived by every anchor.
4. **Summaries.** Compute a per-function summary
   (`params: [borrowed | owned | escaping]`,
    `result: fresh | anchors(S) | heap`) and serialise it into the `.sbi`.
   Summaries make the analysis modular — the whole point.
5. **Specialisation.** Where codegen differs by root shape (region vs heap),
   emit specialised bodies, keyed by the anchor signature.

Diagnostics must name the root, the escape site, and at least two concrete
fixes. A borrow checker with bad errors is a borrow checker nobody uses.

Status: a first, working slice (`src/Sable.SIL/RootChecker.cs`). References
are coherent in the front end — `T&` reads as its target, a place becomes a
reference where one is wanted (`return a` from `-> T&`, a `T&` argument,
each arm of `c ? a : b`), and a value with no place cannot (S3060) — and
lower to addresses. Anchors (1) are a local's slot, a parameter, or `heap`;
propagation (2) goes through field projections, reference variables, joins and
calls; summaries (4) are the parameters a result may refer into, inferred to
a fixed point across the module so recursion converges, and recorded on the
function (`roots(0, 1)` in SIL — `longest`'s `{a, b}`, with nothing written).
Checking (3) rejects a returned reference rooted in a local, a temporary or a
by-value parameter, with the fixes named, and — flow-sensitively — a
reference used after its local's scope ended or its value was moved out.
References are now values like any other: a struct may hold one (`next:
Node&?`), and a value holding references carries their anchors through
construction, extraction, copies, joins and the switch on an optional, so
returning one is held to rule 3 as a bare reference is. Rule 4 is in:
references stored into a region's memory may refer only into that region,
and into an object's only into the heap — `r.alloc(Node { next: &local })`
is rejected naming the local and the region, as is linking one region's
node from another's — so a linked structure lives in one region and
nothing in it points out. Non-escape now deletes allocations rather than
ARC traffic (§3.7), from summaries of the same shape. Deferred: references
in arrays, summaries serialised into a `.sbi` (the `.sbi` itself is
Phase 5's), secondary diagnostic labels, and rule 5's specialisation by
root shape, which waits on code generation that would differ by shape.

**Risk note:** budget 10 weeks, expect 16. This pass is where an ambitious
schedule goes to die, and it is also the only part of the design that has not
been validated at scale by someone else. Prototype it standalone, against a
toy IR, *before* Phase 3 starts — ideally during Phase 1 as a side project.

### 3.4 Exclusivity (~3 weeks)

Static checking where accesses are to provably distinct storage; dynamic
`begin_access`/`end_access` markers otherwise, elided in release builds.

Status: done for the accesses a call makes (`src/Sable.SIL/SilGen.Exclusivity.cs`).
Statically, each `inout` and `T&` argument, and a mutating method's
receiver, is a path from a variable through fields and elements (an
element is the whole collection); two that overlap, one of them a change,
are error S3062, naming both arguments and the fix — `bump(&p.a, &p.a)`,
`swap(&a[i], &a[j])`, `copy(n, &n)`, `p.absorb(&p.a)` are rejected, and
`bump(&p.a, &p.b)` is not. Where the compiler cannot tell — shared
storage: a global, or a field of an object (a class's, a closure context's,
a captured variable's box) — the call is bracketed by
`access_begin_modify`/`access_begin_read` and `access_end`, begun once
every argument is evaluated (so `add(&box.n, box.n + 1)` reads first), and
the runtime traps on an access that conflicts with one in progress. Every
plain read or write of shared storage — a load, a store, an array changed
in place — is checked against the accesses in progress as it happens, so
`bump(&counter)` that does `counter += 1` traps; inline, the check is one
load of `sable_access_count`, and the runtime is called only when an
access is in progress. Storage is a range of bytes (the interpreter: a path
of object, field and elements), so a struct overlaps its fields. An array
element passes inout through `array_element_addr` — `bump(&values[0])`,
`bump(&box.items[0])` — and the access is to the whole array, which may not
change while the element is lent. The markers and checks are kept in debug
and `-O0` builds and dropped from optimised ones unless
`--exclusivity=checked` keeps them (docs/02 §2.3); the interpreter follows
the module's setting.

### 3.5 Regions (~4 weeks)

- `region` as a value type with a bump allocator in the runtime.
- Region references get `region(r)` anchors and fall out of 3.3 for free —
  this is the payoff for doing root inference properly.
- `reset()`, nested regions, region-to-owned copy-out (`toOwned()`).
- Thread affinity by construction (a region anchor is not `Sendable`).

Status: done but for `toOwned()`. `region arena { … }` makes a `Region` — a
counted runtime arena of chunks — that ends with its block on every way
out; `arena.alloc(value)` moves a value into it and gives back a `T&`; an
`in r: region` parameter (either spelling) borrows a caller's. Ending a
region runs the destroys of what it holds, last first, then frees its
chunks at once. And, as promised, it fell out of 3.3: what a region
allocates is anchored where the region is, so returning a reference into
a local region is rejected with its fixes, a reference used after its
region ended is rejected, and `build(in r) -> Node&` has `roots(0)`.
Linked structures live in regions (`Link { next: Link&? }`, rule 4 above).
`pool.reset()` destroys what the region holds and keeps its newest chunk
for what comes next; root inference knows a reference made before the
reset is dead after it and one made after is not, through variables and
loops — the per-frame `reset()`-then-allocate loop compiles, and reading a
node kept across a reset does not. `region frame = pool { … }` is a nested
region in the same memory: the block allocates through `frame`, `pool` is
lent to it meanwhile (a direct use is an error), and as the block ends
what it took is given back — destroyed, its chunks freed, the bump pointer
restored. Deferred: `toOwned()` as a method (a dereference already copies
out), and thread affinity, which waits for threads.

### 3.6 Inline layout (~6 weeks)

- Layout computation for `@inline_layout`: contiguous, pointer-free, with
  variable-size fields laid out in declaration order with inline extents.
- Relative references: `&<u32>`, `&<u16 in pool>`, null-as-zero.
- Variant-sized unions (`union X..`): per-value sizing, sequential arrays,
  no in-place case change.
- The array family (`T[k] T[] T[..k] T[>..] T[>..<] T[:]`).
- `Bytes.of` / `fromBytes` with full validation of framing, tags, lengths and
  every relative link.
- In-place construction: a value is built at its final destination, never
  constructed-then-copied.

Status: done for structs (`src/Sable.SIL/SilGen.Inline.cs`, `InlineLayout`
in `SilModel.cs`, the runtime's inline section, and its mirror in the
interpreter). It needed the sized integers first: `U8`…`U64`, `I8`…`I64`,
`UInt` and `F32` now lower, with arithmetic that traps on overflow at
each width, comparisons by signedness, `U64(x)` conversions that trap
when the value does not fit, and `-5` a literal of whatever type is
wanted. An `@inline_layout` struct is a block: one counted object holding
the value's bytes, copied on write. Fixed-size fields come first, packed,
at offsets the compiler knows; then each variable-size field in
declaration order — `T[..k]` with its count in the fewest bytes its bound
needs, `T[>..]` with a LEB128 count, then the elements, nested inline
structs in place. Everything inside is reached through views
(`$*inline Order`): a field, an element, a loop's element are views into
their owner, never copies. `T&<u32>?` and `T&<u16>?` are signed offsets
from the field, 0 for null, set from `&tree.nodes[0]` and read back as
references; a change of size that moves bytes measures every relative
reference again. `Bytes.of(v)` is the bytes; `T.fromBytes(b)` gives
`.Ok(value)` or `.Invalid(why)` after checking framing, every count
against its bound and the bytes left, every Bool and Char, and that every
relative reference points at the start of a value of its type found by
the same walk. Values are built where they will live: a record is an
empty block filled field by field, and with §3.7 that block is in the
frame. Views are references, held to root inference: one is rooted where
its block is held, so returning a view into a local block, or into a
block passed by value, is rejected (a `T&` parameter's is fine); and a
change to a block — copied on write, or grown and moved — makes every
view taken from it before stale, as a region's reset does its references,
so `let kept = &bag.items[0]; bag.items.append(…); kept.value`, or
appending to an array while iterating it, is rejected. Deferred: variant-sized unions (`union X..`), `&<u16 in pool>`
(pools are regions of inline values, which wait on them), fixed `T[k]`
and the slice and shrink kinds inside inline structs, indexing elements
that vary in size (they are iterated; docs/02 says so), and generic
inline structs.

### 3.7 Escape analysis and promotion (~4 weeks)

The pass that makes idiomatic code fast: a `class` that does not escape its
frame becomes a stack value with no header and no refcount. Driven by the same
summaries as 3.3.

Status: done, keeping the header (`src/Sable.SIL/EscapeAnalysis.cs`); the
count goes where nothing copies the object.
A value escapes if it, or a copy, may be returned, thrown, stored anywhere
but a local slot, put inside another value, captured, held weakly, or
passed to a parameter that escapes or to a callee not known here; which
parameters escape is a per-function summary computed for the module to a
fixed point. A class's allocating initialiser is made where it is called,
so the caller owns the allocation; then `alloc_ref [stack]`,
`inline_new_stack` and `array_literal_stack` put what does not escape in
the frame. Made in a loop, it is promoted when it is gone before the loop
comes round, however many blocks the body has: every value holding it is
made where the allocation's block dominates (or made and gone in one
block), and each local slot holding it is emptied of it on every path back
to the allocation, so the one place in the frame is reused turn after
turn. A closure's context is an object like
any other: making the closure lets it escape only when the closure's code
keeps its context beyond a call, and calling a closure hands its context to
that code alone. A closure captures a local that cannot change by value, as
`[x]` would, so it needs no box of its own; so does a local function, of a
`let` that has its value where the function is declared. An array literal is made whole
(`array_literal`, its elements consumed) and, in the frame, as a header
with its elements after it; grown past them, the elements move to the heap
and the header stays. An object in the frame that nothing copies has one
reference all its life, so its count is one from start to end: each
destroy of it, of what is taken out of a slot that only ever held it, or of
such a slot, is `[uncounted]`, and runs the deinit and the fields'
releases where it is, in order, freeing nothing, with no release counted
(`sable_destroy_uncounted`, which traps if the count is not one). One
copied is counted as any object is, and its last release does the same.
An inline block that outgrows its room in the frame moves to the heap,
where an uncounted destroy releases it as any. The header stays: the
runtime's operations on arrays and inline blocks, a deinit and a method
call read it. The JSON program loses
187 allocations; the order record of §3.8 loses all of them. A loop of
100,000 turns that calls a closure capturing its counter, makes an object,
and reads an array literal made 500,001 allocations and makes 1. Orders' four
million releases, all of blocks in the frame, are none (#188); a loop
whose body branches, and a local function's context, allocate nothing.

### 3.8 Exit criteria

- [x] A benchmark suite exists and is tracked per-commit — `benchmarks/`
      (binary trees, n-body, shapes, sort, words, and the JSON exit
      program), run by `tools/bench/run.py --record`, which appends time,
      allocations, retains and releases per commit to
      `benchmarks/history.tsv`
- [x] ARC overhead on the suite < 5% vs a manually-managed equivalent —
      2.3% across the suite (`tools/bench/arc_overhead.py`; 1–3% per
      benchmark run to run, n-body none, shapes noisy at 5 ms). A program
      that manages memory by hand still frees what it allocates and destroys
      its contents, as ARC's last release does; what it does not do is
      count. So the overhead is the count traffic — every retain, and every
      release that leaves its object alive — measured by doing it twice: each
      benchmark against the runtime as it is and against one where each such
      operation first does an equal one, out of line, on the same header.
      Getting there took the count traffic from tens of millions of
      operations to hundreds of thousands: a borrowed field kept unretained
      in its `if let` slot when nothing can write the heap meanwhile
      (`BorrowFields`, binary trees: 14.6M retains to 0); array elements read
      in place where they are only looked at, the dictionary's key search
      among them (words: 7.9M to 0.8M); a `for` loop's element held
      unretained, since the loop walks a copy nothing else can change
      (shapes: 903K to 2.6K); `return result` a move; and an optional class
      reference one nullable word rather than a tag and a payload. The C
      ports in `benchmarks/programs` — same allocations, checks and output — are
      timed beside it. Closing that gap was code generation, not ARC:
      inout and `T&` parameters are LLVM `noalias` (exclusivity guarantees
      it) and dereferenceable, as a lent object is; array headers, reference
      counts, numbers and references carry distinct TBAA tags, a range of an
      array is checked by one comparison (`IndexTraps`), an object just
      made has its fields initialised with no test of sharing (`InitFields`),
      a `let` field and an array viewed whole are borrowed for as long as
      what holds them lasts (`BorrowHeld`), `a[i].x = v` stores the field
      where it is, array
      count, element access and append are emitted inline, a loop that only
      reads an array and writes its elements makes it unique once before
      the loop (`HoistUniqueness`), an array passed to a mutating member of
      `Element[:]` (`bytes.writeU64(…)`) is made unique once, inline, and
      moved into a view of itself and back with nothing retained, its
      writes, the member inlined, one store each, string equality decides by identity and
      length inline, inline layouts get walkers generated per layout rather
      than a descriptor walked at run time, and optimised macOS builds link
      the runtime with LTO. Sable is now 1.0× C on n-body, about 1.25× on
      binary trees, sort and words, 1.4× on shapes (a 5 ms run) and 2× on
      orders, down from 1.45–1.9× and 4.7×; binary trees' remainder is
      layout, a 16-byte object header on a 16-byte node
- [x] Root inference rejects no program in the corpus that should compile —
      `CorpusLoweringTests` holds every corpus file, example and the JSON
      program to producing no diagnostic but "not lowered yet": root
      inference, the move checker and the exclusivity check reject none of
      them
- [x] `@inline_layout` reproduces the Goose order-record result: a nested
      record in **0 allocations**, within 20% of Goose's byte count — the
      order of docs/02 §2.5 (`id: U64`, `customer: U8[..32]`,
      `lines: OrderLine[>..]`, `parent: Order&<u32>?`) for customer "Ann"
      with one line is 26 bytes against Goose's 29, and building it, reading
      it and totalling it allocates nothing (`SABLE_STATS`: allocations 0);
      `benchmarks/programs/orders` builds a million of them with 2,000
      allocations, all in the thousand that go out as bytes and back
- [x] Round-trip `Bytes.of` / `fromBytes` survives a 10M-case fuzzer with no
      memory-safety failure on corrupt input — `tools/fuzz/run.sh` builds
      `tools/fuzz/inline_layout.sb` with AddressSanitizer and
      UndefinedBehaviorSanitizer over the runtime and feeds `fromBytes`
      ten million mutations of valid orders and trees (with relative links,
      cycles among them): about 990,000 are accepted, each walked entirely,
      every reference followed, and written back byte for byte; the rest
      are refused with a reason. No sanitizer report, no leak. A test runs
      the same fuzzer for 3,000 cases through the interpreter and natively,
      and the two validators agree on every one

---

## Phase 4 — Runtime, reflection, and the core library
**~4–5 months. Deliverable: a standard library you can write programs with.**

### 4.1 Runtime completion

Metadata records, protocol conformance registry, generic metadata caching,
existential boxing, dynamic casts, the error ABI, backtraces, the memory
allocator (a size-class allocator — mimalloc is the reference to copy or embed).

As implemented so far: with `SABLE_BACKTRACE` set (to anything but `0`), a
trap prints a backtrace after its message, one frame per line, named through
`dladdr`, and on Linux, where the program's own functions are not in the
dynamic symbol table (it links with `--gc-sections`, not `-rdynamic`, which
would keep every symbol), from the program's `.symtab`. `1` starts from the program's frame that trapped; `full` shows the
runtime's own frames above it as well. `Backtrace.capture()`
(Sable.Diagnostics) gives the same frames as a value; the interpreter has
none to give. Each frame is looked up one byte before its return address,
because a call that never returns leaves that address past the end of its
caller, and one byte back is in the call, on the line that made it. Under
LTO most of a small program is inlined into `main`, so expect few frames.

In a debug build (`-g`) a frame is named by its line, the program's and the
library's alike: `sable.Main.parse at main.sb:12:15`, `sable.Sable.Core.precondition…
at lib/Sable.Core/Assertions.sb:45:99`. The runtime reads the build's own
DWARF line table (runtime/lines.c, a few hundred lines of C, no library):
the `.debug_line` section, with `.debug_line_str` for its file names, run
as the small program DWARF writes it until the row that holds the address.
On Linux the table is in the executable itself (`/proc/self/exe`, mapped);
on macOS it is in the `.dSYM` clang's dsymutil writes beside the program,
read only when its UUID is the running program's, so a stale one is never
believed. The address is first moved back by how far the loader slid the
program (`_dyld_get_image_vmaddr_slide`, `dl_iterate_phdr`), and only a
frame in the program itself is looked up — the system's libraries keep
`symbol + offset`. The table is found once, by whichever thread first asks,
and kept mapped. Windows describes a debug build in CodeView, in the PDB the
link always writes, so there DbgHelp's `SymGetLineFromAddr64` gives the
line (no column; a PDB has none for this). Two alternatives were set
aside: running `atos` or `addr2line` is another process, not allowed from a
crashing one; and an address→line table the compiler embeds itself would
work on every platform but needs the addresses, which only the linker knows.
An optimised build without `-g` has no line table and keeps `symbol +
offset` — line tables alone (`-gline-tables-only`) would cost a `.dSYM` per
build on macOS, and are left for when they are wanted. WASI has no frames.

**The panic hook.** `Panic.setHook { info in … }` (Sable.Diagnostics, Rust's
`std::panic::set_hook`) has every trap said by the program's own hook, in
place of the report: `PanicInfo` has the message, the file, line and
column, and the backtrace. Every trap goes through it — one the compiled
code makes, one the runtime makes, one the program asks for, an error main
threw — because they all end in the runtime's `trap_report`. The hook is a
closure the runtime holds (`trap_hook_set`), its context shared, since a
trap can happen on any of the pool's threads; the first thread to trap
claims the report and any other that traps meanwhile waits for the program
to end, so the hook runs once. A trap inside the hook falls back to the
default report, of the trap the hook was given and then its own (`fatal
error: the panic hook trapped: …`). `Panic.takeHook()` takes it out and
gives it back, or, with none set, the default report as a hook, so a hook
can be put in front of whatever was there. The program then ends as it
would have. A stack overflow does not run it: there is no stack left to run
it on. The interpreter runs the hook the same way, with no frames.

**An error that leaves main** is a trap too, as in Swift and Rust: `fatal
error: uncaught error: ` and the error's `description()`, the place of the
`throw` or `try` it left main from, through the panic hook, and status 1
rather than an abort's 134. SilGen does it where the error would leave
main (sync or async: before async lowering), calling Sable.Core's
`uncaughtError` with the error boxed as `any Error` instead of throwing it
to the C `main`, which had nothing to do with it and returned 0.

**`Debugger.isAttached` and `Debugger.break()`** (Sable.Diagnostics): asked
of the system each time — `sysctl`'s `P_TRACED` on macOS, `TracerPid` in
`/proc/self/status` on Linux, `IsDebuggerPresent` on Windows, never on
WASI or in the interpreter. `break()` is `llvm.debugtrap` in the caller's
own code, so the debugger stops on the line that asked and goes on from it;
with no debugger attached it is skipped, rather than ending the program as a
trap instruction nothing catches would.

Every trap says where it happened, on the line after its message:
`fatal error: array index out of range` then `  at main.sb:12:15`. A trap
the compiled code makes passes its place to `sable_trap_at`; before a call
into the runtime that can trap (a string index, an unowned read, an access
check) the compiled code stores the call's place in `sable_trap_site`, a
store on the slow path only. The interpreter reports the same place: the
innermost instruction that trapped. A trap the program asks for —
`fatalError`, a failed `precondition`, `todo()`, `Optional.expect` — goes
through the `trap` builtin (`sable_trap_with`), with a message and place
made as it runs: the caller's, since a parameter that defaults to `#file`,
`#line`, `#column` or `#function` takes the place of the call. Traps that belong to no line — memory
run out, every task waiting — have no place. Running off the end of a stack
is a trap too, not a bare SIGSEGV: every thread has a small signal stack
(`sigaltstack`), and a fault at the low end of the thread's stack prints
`fatal error: stack overflow: calls nested too deeply`, as the interpreter's
depth limit does, and exits as a trap does.

### 4.2 Reflection

- **Static metadata** for every type: size, alignment, field offsets, name,
  conformances. Always emitted; the runtime needs it anyway.
- **Full metadata** for `@reflectable` types: member tables, parameter info,
  attribute payloads, dynamic invoke thunks, dynamic construction.
- **Reflection graph at link time**: start from `@reflectable` roots and the
  manifest's `reflect` list, compute the closure, and emit only what is
  reachable. `sable build --explain-reflection` prints the graph and the bytes.
- API shape: `Mirror(of:)` for read-only introspection (cheap, works on
  everything), `TypeInfo` for the full `@reflectable` surface.

#### Reflection, as implemented

A first slice, in `lib/Sable.Core` (`Reflect.sb`, `Mirror.sb`, `TypeInfo.sb`) over `Reflect`, a struct
of `@intrinsic` members whose bodies the compiler writes, per type (unlike
`@builtin`, which names one SIL builtin; see "Sable.Core in Sable")
(`src/Sable.SIL/SilGen.Reflection.cs`; tests in
`tests/Sable.CodeGen.Tests/ReflectionTests.cs` and
`tests/Sable.Symbols.Tests/ReflectionTests.cs`):

- `Mirror(of: value)` on any value: `typeName`, `kind` (struct, class,
  actor, union, enum, optional, tuple, array, dictionary, function,
  existential, primitive), `caseName`, `fields` (name, type name and value,
  described) and `description` — `Point(x: 1, y: 2)`,
  `Shape.Circle(radius: 2.5)`, `[1, 2]`, `["a": 1]`, `none`. A class's
  superclass fields come first; weak and unowned fields are read without
  keeping the object; objects are followed eight levels deep, so a cycle
  ends in `...`. Closures, existentials, regions and `@inline_layout`
  values are named, not taken apart: `<(Int) -> Int>`.
- `@reflectable` is a conformance to `Reflectable` (writing `: Reflectable`
  is the same), and `TypeInfo.of(T.self)` requires it — the binder's usual
  constraint error otherwise, and `@reflectable` on a protocol is S3080.
  `TypeInfo` gives the name, kind, stored fields (type, mutability,
  visibility), methods, initialisers and operators (signature, parameter
  names and types, return type, static/public/mutating), union and enum
  cases with their payloads, declared conformances and the attributes as
  written; generic code reaches it through `T: Reflectable`.
- Nothing is emitted for a type until a program reflects on it: each
  question is a SIL function synthesised per type on first use, shared by
  later calls, and pruned with everything else unreachable. Hello world is
  51,736 bytes before and after. Instead of the always-present field
  descriptors of docs/06 §6.2, the answers are code generated where asked
  for; the interpreter and the native build agree because both run it.

Deferred: a class reference is described as its static type, not the
subclass it is; `Mirror(of:)` on `()` (a generic parameter of type `()`
does not lower yet); computed properties; attribute payloads as values;
dynamic invoke and construction; `Runtime.lookupType` and the manifest's
`reflect` list; `--explain-reflection` (there is no graph to print until
metadata is emitted as data rather than code).

### 4.3 The core library (`Sable.Core`)

Order of construction:

```
Sable.Core         primitives, Optional, Outcome, String, Array family,
                   Comparable/Hashable/Equatable, iteration protocols
Sable.Collections  List, Dictionary, Set, Deque, OrderedDictionary, PriorityQueue, InlineArray, SmallVec
Sable.Text         unicode, formatting, parsing, regex
Sable.Math         fixed/float math, SIMD vectors, random
Sable.IO           File, Directory, Stream, Path, stdin/stdout
Sable.Serialize    Encoder/Decoder protocols, JSON, binary, the zero-copy path
Sable.Concurrency  Task, TaskGroup, Actor runtime, Channel, Mutex, Atomic
Sable.Net          Url, sockets, Dns; Http client to come
Sable.Time         Instant, Duration, Clock, Calendar
Sable.Process      spawn, environment, signals
Sable.Hosting      host, lifetime, services, cron jobs
Sable.Diagnostics  Logger, tracing, metrics
Sable.Testing      the test harness `sable test` drives
```

**Write the standard library in Sable from day one of this phase.** It is the
first large program and the best source of language bugs.

A library — the standard library, or a registry or git dependency — is
shipped as its source and, built once per target and profile into the
shared build cache, an archive and its interface (`.sbi`, #182; docs/05
§5.4): its bound symbols and its SIL.
A program compiled against Sable.Core's interface (`Pipeline.CompileAgainst`)
does not bind or check the core's bodies or lower its code again — 1.8 s of
the 2.0 s a fresh process spends on a small program; it still declares the core from
its source, which holds the generic bodies the program instantiates. A
library whose unit is in the cache is known to check cleanly, so a fresh
process — a build, `sable check`, the language server's SIL checks — binds
none of its bodies and lowers only what the program reaches.

### The library, as begun

The library is Sable source in `lib/<Module>/*.sb`, embedded in the driver
and compiled with every program (`src/Sable.Driver/Library.cs`):
`Sable.Core` always, every other module when a program imports it, and what
those import in turn. Only what the entry point, the globals and the deinits
reach is emitted, so hello world stays at 51 KB unstripped with the prelude.
An error inside the library is reported as such, not against the program.

Written so far (`tests/Sable.CodeGen.Tests/LibraryTests.cs`):

- `Sable.Core`: `Equatable`, `Comparable` (refining it) and `Hashable` as
  Sable protocols with operator requirements; the integer types, floats,
  `String`, `Char` and `Bool` conform in extensions. `!=`, `>`, `<=` and `>=`
  are derived from `==` and `<`, so generic code compares through
  `T: Comparable` and user types conform with one operator.
- `Sable.Math`: generic `max`/`min`/`clamp`, `gcd`, `lcm`, `power`,
  `isPrime`, `squareRoot`, `floor`/`ceiling`/`round`, and `Random`.
- `Sable.Text`: `repeating`, `joined`, `reversed`, `hasPrefix`/`hasSuffix`,
  `find`, `contains`, `replacing`, `splitting`, padding, case mapping, and
  `TextBuilder`. Formatting and parsing (`TextLibraryTests.cs`): integers
  in any radix from 2 to 36 with a width, a fill (a '0' goes after the
  sign) and a letter case, `parseInt` in a radix (none on overflow), and
  `formatted(x, decimals: n)` for F64 — the digits of `%.*f`, rounded half
  to even on the value's exact binary expansion, worked out in Sable with
  base-10^9 limbs rather than asked of printf or .NET, so native and
  interpreted agree to the last digit. Also `format("{} is {1}", args)`
  templates, `lines`, `trimmedStart`/`trimmedEnd`, `isBlank`,
  `removingPrefix`/`removingSuffix`, `findLast`, `occurrences` and
  `escapedHtml`. (Since removed: the string functions are String's own
  members, and `format`/`formatted` gave way to interpolation's format
  specs, `"\(x:.2f)"`; `escapedHtml`, `parseInt` and `TextBuilder` stay.)
- `Sable.Collections`: `Stack`, `Queue`, `Set` (a `[Element: Bool]`
  underneath, iterating in insertion order; the `HashMap` that was here
  became the built-in dictionary, docs/06 "Dictionaries and hashing"), `Deque` (a ring
  buffer of optional slots, so a removed element is released at once),
  `PriorityQueue` (a binary heap, smallest first or by a stored ordering
  closure), `OrderedDictionary` (keys and values in arrays, positions in a
  dictionary; removal closes the order up), `BitSet` (64 to a word, with
  union, intersection, subtraction and subset), and a stable merge sort —
  `sort(&values)` in place, `sorted(values)` a copy, either by `<` or by a
  predicate (`tests/Sable.CodeGen.Tests/CollectionLibraryTests.cs`). (Since
  #12 the sort is the arrays' own, `values.sort()` and `values.sorted()`,
  and the free functions are gone.)
- `Sable.IO`: `File` (read, readLines, write, append, exists, remove),
  `Directory` (exists, create, list — sorted byte by byte so every platform
  agrees — and remove), `Path` (join, fileName, directory, extension,
  withoutExtension), `Console` (the standard streams, `readLine`,
  `readLines`) and `Environment` (variables, arguments), failing with
  `IoError`. It is written over `Runtime`, a `Sable.Core` struct of static
  `@builtin` calls (`lib/Sable.Core/Runtime.sb`, see "Sable.Core in Sable"
  below) that the runtime implements in C (`sable_io_*`) and the interpreter
  in .NET; a call that may fail gives a value either way and `io_succeeded`
  says whether to keep it, which `Runtime`'s Sable wrappers turn into an
  optional. `main` now takes `argc`/`argv`. The test harness gives programs
  standard input and compares standard error between the two.
- `Sable.Time`: `Duration` (to the nanosecond, Comparable and Hashable,
  printed in its largest whole unit), `Instant` on the monotonic clock,
  `Clock.sleep` and `Clock.measure` (since: docs/06's clocks), and `DateTime` (now `UtcDateTime`) in UTC — fields,
  ISO 8601 weekday and day of the year, `iso8601()`, `parse`, arithmetic —
  over proleptic Gregorian 400-year eras, so dates before 1970 work. Over
  three more runtime calls (`time_monotonic`, `time_wall_clock`,
  `time_sleep`). Not yet: time zones (since: docs/06's Sable.Time), and a
  sleep that lets other tasks run (it holds the scheduler's baton).
- `Sable.Process`: `Process.run` runs a program found on PATH to its end,
  its input written and its output and errors read together through
  `poll` (so neither side blocks on a full pipe), giving `Completed`
  (status — 128 plus the signal when one ended it — output, errors);
  `ProcessError.NotFound` when nothing by that name starts. Also the
  current directory and the process id. (Since, `Command`, `Child`,
  `Signals` and `Process.exit`, #70: docs/06 §6.4, "Processes and signals".)
- `Sable.Serialize`: JSON, which is Phase 2's 2,000-line exit program made a
  module. It has the `JsonValue` model, a strict parser (with opt-in comments,
  trailing commas and duplicate-key checks) that reports errors by line and
  column, and compact, pretty and canonical printing. It also covers JSON
  Pointer, Merge Patch, comparing, measuring and transforming documents. The
  program's own 237-case suite runs against the module as well
  (`JsonProgramTests`). Its text helpers are internal, so that it imports
  cleanly beside Sable.Text and Sable.Collections. Not yet: the `Encoder` and
  `Decoder` protocols, binary formats, and namespacing the free functions
  (`parse`, `render`) under a type.
- `Sable.Diagnostics`: `Logger` writes one structured line per event to
  standard error, as `LEVEL name: message key=value`. It has a level
  threshold (or `SABLE_LOG`), context fields that `with` carries to child
  loggers, optional UTC timestamps, and quotes values where they would be
  misread. Not yet: tracing spans and metrics.
- `Sable.Concurrency`, written in Sable over the core's `Task` and
  `WaitList`: `Channel`, an actor (unbounded, closable, of `Sendable`
  values; `receive` parks until a value comes) and a `sleep` that lets
  other tasks run meanwhile. Task groups are the core's (`withTaskGroup`;
  see "Async, as implemented"). Not yet: bounded channels, `Mutex` and
  `Atomic`.

What it took from the compiler: `forward-copies` no longer folds a copy into
its source when the copy is consumed while the source is still in use (a
task spawned in a loop), `[]` as an argument typed by the parameter
(overloaded or not), `?.` on any optional rather than only a
local's, diagnostics placed in the file they are in rather than by offset
(offsets overlap between the program and the library), generic initialisers
inferring their type arguments, user operators found through a type parameter's constraints,
operators and witnesses on primitive types lowered, and reachability-based
emission. A first slice of reflection (4.2) has followed. Next: streaming I/O (`Stream`, buffered
readers and writers, memory-mapped files), and the names the plan lists that are not yet here
(Unicode tables, regex).

### Sable.Core in Sable, as implemented

The primitive types (`Int`, `String`, `Char`, …) are the compiler's, since
every layer knows them by sight; their members are now Sable, in
`lib/Sable.Core` — `String.sb`, `Char.sb`, one file per number type
(`Int.sb`, `U8.sb`, `F64.sb`, …) and `Runtime.sb`, beside the protocols
(`Equatable.sb`, `Debug.sb`, the numeric ones from `AdditiveArithmetic.sb`
to `BinaryFloatingPoint.sb`, …) and reflection (`Reflect.sb`, `Mirror.sb`, `TypeInfo.sb`). At the floor is
`@builtin("name")` on a bodiless declaration, naming the SIL builtin it is:

```sable
extension String {
    @builtin("string_count")
    public var count: Int { get }

    @builtin("string_trimmed")
    public fn trimmed() -> String

    /// Sable over a raw builtin: -1 becomes none.
    public fn indexOf(character: Char) -> Int? { … }
}
```

A call is that builtin, applied where it is called to the receiver (if
any) and then the arguments, in order, each borrowed for it — no function
is generated, so it costs what the special case in `SilGen` it replaces
cost: hello world (52,392 bytes for `print("hello, world")` on the machine
this was measured on) and every benchmark in `benchmarks/programs` and
`benchmarks/library` build to the same number of bytes, and run at the
same speed, before and after. A builtin that answers with a flag — parsing,
a fallible runtime call — is a raw, internal `@builtin` with a public Sable
function over it (`toInt`, `Char.fromCodePoint`, `Runtime.readFile`), which
LTO inlines. Writing them in Sable gave real bodies to members the compiler
used to declare and could not lower: `toI8`…`toUInt`, `abs`/`min`/`max` on
every width, `Char.code`.

Misuse is caught where it is written: S3081 for `@builtin` outside
`Sable.Core` (a builtin trusts its operands); S3082 for a malformed one (not
one string literal, a body, a generic, a property other than a lone bodiless
`get`); and, from SIL lowering, which owns the table, S7001 for a name
`SilBuiltins` does not have and S7002 for a declaration whose receiver,
parameters and result do not lower to the builtin's operands and result
(`tests/Sable.Symbols.Tests/BuiltinTests.cs`,
`tests/Sable.SIL.Tests/BuiltinLoweringTests.cs`). `@intrinsic`, which
reflection uses, stays apart: its body is synthesised per type, not one
builtin.

`@intrinsic` also declares what is one builtin for no single signature, or
more than one: `Region.alloc<T>(_ value: T) -> T&` (room bumped, the value
stored, for any `T`); `Task<Success>` in `Task.sb`, a class whose
`static fn spawn<T>(_ body: async () -> T) -> Task<T>` builds the task object
around the closure, whose `var value: Success { async get }` waits for it,
and whose ``static async fn `yield`()`` lets the queue run (it is made by
`spawn` alone: its `init` is private, and a private initialiser is now
enforced). Inline layout's are in `InlineLayout.sb`: `union Decoded<Value>
{ Ok(value: Value), Invalid(why: String) }`; `protocol InlineLayout { static
fn fromBytes(_ bytes: U8[>..]) -> Decoded<Self> }`; and `Bytes.of<T:
InlineLayout>(_ value: T) -> U8[>..]`. The conformance is synthesised the way
`@derive`'s are (`Compilation.InlineLayout.cs`): once every type is declared,
an `@inline_layout` struct gets the requirement, `Self` substituted, as its
`fromBytes`, and `InlineLayout` in its base list; SIL lowering writes the
body from the layout. So the declarations are Sable and only the per-type
body is the compiler's, and `Bytes.of` on a struct that is not
`@inline_layout` is a type error where it is written rather than a lowering
failure; naming `InlineLayout` in a base list without the attribute is an
error too, since there is no body to write. Three small language additions
came with it: `async get` (an async getter, awaited where it is read and
free to await, in any type), an escaped identifier naming the function it
declares (`` fn `yield`() `` declares `yield`), and conformance checking that
substitutes `Self` inside a requirement's generic types (`Decoded<Self>`).

To make this possible `Sable.Core` is now part of every compilation —
embedded in `Sable.Symbols` and appended to whatever trees a compilation is
given (`CoreLibrary`) — rather than something the driver adds, so a test
binding three lines sees the same `String` a program does. Deleted from C#:
`CoreTypes`' declarations of those members and of `Runtime` and `print`,
and `SilGen`'s `CoreMethod`, `RuntimeCall`, the string and character cases
of `ComputedProperty`, the `print` special case and `MakeOptional`.

The array family followed, once a declaration could range over every
array: `extension<Element> Element[>..] { … }` (SYNTAX.md §11) declares its
parameters after the keyword, since an array has no name to write them
after. Each kind is extended on its own (`T[]`, `T[..]`, `T[>..]`,
`T[>..<]`), and what `T[:]` declares every kind has, as every array can be
viewed as a slice. The members belong to an unnamed type made for the
extension, generic over its parameters and holding the array it extends;
inside, `self` is that array, and looking a member up on an array matches it
against each extension's array and substitutes what it finds, constraints
checked (`Compilation.ArrayExtensions.cs`). So a member is an ordinary
generic, monomorphised per element type. `lib/Sable.Core/Array.sb` declares
`count`, `slice`, `append`, `removeLast` and `clear` as `@builtin`s —
`mutating` ones pass the array where it is, and an array builtin's
declaration is checked as if its element were `Int`, as the array builtins
take any — and `isEmpty`, `contains` (where `Element: Equatable`) and bytes'
`toString` (UTF-8, in Sable) as Sable. The growing members being `mutating`,
growing an array that cannot change is now an error the binder reports
(S3041) rather than a store lowering could not make. Deleted from C#:
`CoreTypes.GetArrayMembers`, and `SilGen`'s array cases of `CoreMember` and
`ComputedProperty`. `count`, `append` and indexing lower exactly as before:
every benchmark in `benchmarks/programs` and `benchmarks/library` compiles to
identical LLVM IR, and `list` measured 1.13 against .NET before and 1.10
after (processor time, fastest of five, on a busy machine).

### The first real program

`examples/ssg/ssg.sb` is a static site generator, some 640 lines of Sable: it
reads a directory of Markdown pages with front matter and a template,
renders a pragmatic Markdown subset (headings with unique ids, paragraphs,
emphasis, code, links and images, nested lists, quotes, rules, fenced code)
and writes a page each, an index newest first, a page of tags, and the
static files; `sable run examples/ssg/ssg.sb -- <site> <out>` (`run` now
passes what follows `--` to the program). `StaticSiteTests.cs` builds the
sample site in `examples/ssg/site` both ways and requires the same output
and the same files, byte for byte, with nothing leaked. Writing it found:
default arguments were checked but never passed (now filled in by the
binder, bound where the callee is declared, and a label may skip
defaulted parameters); string ordering (`a < b`) did not lower; `[]` as
an argument had no type; `HashMap<String, String[>..]>()` parsed as
comparisons; a conditional could not be broken before its `?`; and
String's hash overflowed past a dozen characters.

### `@derive`, as implemented

`@derive(Equatable, Hashable, Comparable, Debug)` on a struct or a union
(docs/06 §6.5; `tests/Sable.CodeGen.Tests/DeriveTests.cs`,
`tests/Sable.Symbols.Tests/DeriveTests.cs`). It is conformance synthesis,
not source generation: once every type is declared, the compilation adds the
members (`==`, `<`, `hash(into:)`, `debugDescription()`) as symbols on the type and
the protocols to its base list, and builds their bodies as bound trees from
the fields (`Compilation.Derive.cs`, `DerivedBodies.cs`), which SIL lowering
takes like any other body. `==` is field-wise, `<` lexicographic in declaration
order, `hash(into:)` feeds the fields to the hasher in turn (a union's case
position first), and a union compares case by case — by case first, in declaration
order, then by payload. `Debug` is a new `Sable.Core` protocol,
`fn debugDescription() -> String`, giving `Point(x: 1, y: 2)`,
`Shape.Circle(radius: 1.5)`, strings quoted and escaped, characters in single
quotes and floats with their point; every primitive conforms. Hashable or
Comparable bring Equatable unless the type writes its own `==`.

On a generic type the conformance is conditional: `Pair<A, B>` is Equatable
where its fields' types, substituted, are — decided per construction in
`Conversions.Satisfies`, coinductively for a type that holds itself. A field
that can never conform (one that mentions no parameter) is an error at the
declaration naming the field; using `==` or `hash()` on a construction that
does not conform is an error naming the field that is why. Operators of
generic types now work generally: the binder finds them through the operand's
type arguments and SIL lowering instantiates them from the operands. Classes
and actors are refused: an object has identity, and equality by its fields
would contradict it. `Serializable` derives too, with `@serialize` (docs/06
§6.5), as bound trees calling each field type's own `encode`/`decode`. Not
yet: `Copy`, optional and array fields (neither conforms to the protocols
yet), and a directly recursive union, which SIL lowering does not lay out.

### Against .NET

The exit criterion below, measured: `benchmarks/library/*/*.sb` and
their C# ports beside them (Release, tiered PGO), run by
`tools/bench/compare.py`, which checks the two print the same line.
`map-int` is a million pseudo-random `Int` keys in `HashMap` against
`Dictionary<long, long>` (insert, three rounds of lookups, a million misses,
half removed, looked up again); `map-string` 200,000 interpolated keys in
`HashMap<String, Int>`; `list` thirty million appends, iterations by `for`
and by index, and `removeLast`s; `sort` a million `Int`s and 200,000
`String`s against `List.Sort`; `string` 300,000 interpolated lines split,
compared, joined by `+` and sliced. Times are processor time, fastest of ten
— the Sable process whole, the .NET benchmark alone after a warm-up run,
which leans towards .NET on purpose. Apple M1 Max, macOS, 17 Nov 2026:

| benchmark  | .NET ms | Sable ms | ratio |
|------------|--------:|---------:|------:|
| list       |   125.8 |    143.9 |  1.14 |
| map-int    |   136.0 |    149.6 |  1.10 |
| map-string |    50.8 |     45.8 |  0.90 |
| sort       |   109.6 |    109.6 |  1.00 |
| string     |    59.9 |     42.8 |  0.72 |

(Fastest of five, after the program's functions were given the host's
`target-cpu` and `target-features`, below; the same run without them gave
1.11, 1.24, 1.09, 1.02 and 0.88.)

Measured on a busy machine (load average 11–30, other builds running); an
earlier run the same evening gave the same ratios within 0.03. The .NET side
varies by up to 15% from run to run on `string` and `map-string` (46–60 ms);
the Sable side by a few percent, so `list` sits on the line, 1.14–1.15.
Before this work `sort` was 4.6 times .NET (a merge sort that made two arrays a level),
`string` 2.7 times, and `map-string` trapped: the Sable-written string hash
overflowed past a dozen bytes. What closed the gap, by cause:

- **Library.** `HashMap` is one array of entries (key, value, and the 31-bit
  tag that grows the table without rehashing) under a power-of-two table of
  index-and-tag slots, mixed with a xorshift since Sable has no wrapping
  multiply; the sort is bottom-up over one scratch array, and its merge
  has no branch on the comparison: it moves `source[takeRight ? y : x]` and
  steps both indices by the comparison's 0 or 1, which LLVM makes conditional
  selects (a random merge mispredicts every other step otherwise). A tie
  takes from the left, so the sort stays stable — no unstable variant was
  needed to come within 15% of `List.Sort`'s introsort.
- **Runtime.** A size-class allocator for objects and element buffers up to
  256 bytes (malloc and free were most of the string benchmark), and the
  large buffer freed last kept for the next (the system otherwise maps and
  zeroes it afresh); string hashing and comparison in C
  (`Runtime.hashString`, `Runtime.compareStrings`), the comparison eight
  bytes at a time as big-endian words — the last word ending at the shorter
  string's end, reaching back into bytes already equal or the `count` field,
  which a shift drops — where a byte loop mispredicted on short keys; integers written without
  `snprintf`; `split` on an ASCII separator by `memchr` into an array made at
  its size; strings not zeroed before they are written, nor their scalars
  counted when already known.
- **Compiler.** Interpolations and chains of string `+` are one allocation
  (`string_concat_many`); `removeLast` has an inline fast path, as `append`
  had; an array element read for a call is borrowed even where the array's
  own borrow ended first, which takes the retains and releases off a sort's
  comparisons and a `HashMap` probe's; a `for` over an array lets its copy
  go as the loop ends; a value read from storage whose use is after a
  branch and a join — `values[c ? i : j]`, the array read before the index
  is chosen — is borrowed across the blocks between, which write nothing,
  rather than copied (`BorrowLoads`). String `<` and its derived operators now bind to
  Sable.Core's operator — there was no builtin to lower them to.

`sort` went from 1.26 to 1.03 by the last three: the `Int` sort from about
76 ms to 56 (.NET 55), the `String` sort from 51 to 44 (.NET 38). What is
left: a `String` move in the sort is a retain and a release (the scratch
array shares the elements), which an element swap without counts would take
off; the merge's bounds checks stay, cheap and never taken. The runtime's
`sable_retain` and `sable_release` were not inlined into Sable code even
under LTO: the generated IR carried no `target-cpu`/`target-features`, and
LLVM will not inline a callee built for more features than its caller. An
LTO build now asks clang once for the attributes it gives a C function on
this machine and puts them on every function the program defines
(`NativeBuild.WithHostAttributes`), so the runtime's small functions inline
— the string-heavy benchmarks gained the most (`string` 54 ms to 43,
`map-string` 57 to 46).
`list` is mostly the operating system faulting in fresh pages, which .NET's
warmed-up heap already has; a `HashMap<String, _>` hit no longer copies
the entry or its key to compare it (see "The SIL optimiser" below). `Sable.Text`'s
`joined` (and so `TextBuilder.build`) was quadratic, `result = result + part`;
it is now one runtime call (`Runtime.joinStrings`) that sizes the result once.

`Regex` (#31) against .NET 11's `System.Text.RegularExpressions`, rebar-style:
the `regex-*` benchmarks count matches ten times over in a megabyte of
generated text (`regex-pathological`: `(?m)^(a|aa)+$` on a hundred lines of
twenty 'a's and a '!'), and .NET runs each with `RegexOptions.Compiled` and
with `NonBacktracking` (`<name>/nonbacktracking`). Processor time, fastest
of five, the Sable process whole (about 6 ms of it making the text), Apple
M1 Max on a busy machine (load average 13–50), 24 Nov 2026:

| benchmark          | Compiled ms | NonBacktracking ms | Sable ms |
|--------------------|------------:|-------------------:|---------:|
| regex-literal      |         3.8 |                7.7 |      6.3 |
| regex-alternation  |         6.0 |               24.6 |     16.0 |
| regex-words        |        67.1 |               93.0 |    109.3 |
| regex-email        |         5.2 |               20.3 |     27.0 |
| regex-pathological |       301.7 |                0.4 |      2.3 |

Sable's engine is the linear-time kind, and stands with .NET's: faster
than `NonBacktracking` on the literal and the alternation, within 1.35
times of it on the rest, and linear where `Compiled` backtracks
exponentially. `Compiled` is ahead where it searches for a literal the
pattern needs before trying it — the '@' of an e-mail address, the first
letters of an alternation — which Sable does only for a literal prefix and,
less well, for a small set of first bytes; and on `\w+`, where each of the
150,000 matches a round costs Sable a reverse scan for its start.

### The SIL optimiser, as implemented

What runs at each opt-level is docs/05 §5.2.11's table. At `-O2` and `-O3`,
in order (`SilPassManager.Optimising`): devirtualisation, speculative
devirtualisation, borrowing loads, inlining, simplification, the ARC passes,
loop versioning, simplification again (#179).

- **Devirtualisation.** A `class_method` every possible class answers with
  one function is a direct call (`Devirtualise`). One that can reach one or
  two small overrides, each the method of exactly one class with no
  subclass, tests for each class and calls it directly, the vtable left for
  the rest (`SpeculativeDevirtualise`); the test is `class_is`, which for a
  class nothing inherits from is one comparison of the object's metadata,
  and the call is then inlined. A protocol requirement on a generic is
  already direct (generics are specialised). A call through an
  existential made in the function (#179) calls the witness directly: the
  closure the existential holds has no context, so it is found through the
  struct and the slot it was put in, and its code called with
  `no_context`, which LLVM then inlines (`Simplify`).
- **Inlining.** By cost: an instruction costs one, except what only names
  or reshapes a value (a literal, an extract, a cast, a borrow's ends, a
  slot, a branch), which costs nothing, and a call, which costs five. Up to
  24 is inlined, up to 72 where nothing else names the callee, and each
  literal argument the callee branches on adds 8. A `try_apply` is inlined
  too, the callee's `throw` a branch to the error block. Not inlined: a
  callee with a loop, one that never returns (cold, and kept in a trap's
  backtrace), and, into a loop, one that allocates what it does not return,
  which in its own frame is on the stack but in the caller's loop only if
  gone before the loop comes round. An initialiser, whose object leaves its
  frame anyway, is inlined into a loop, where the object is then
  promoted (§3.7).
- **ARC.** Besides the passes of §3.1 and §3.7: a release is sunk past what
  only looks to pair with a copy's, which then goes (never past a call, a
  write, a release, or a `share`, which publishes to another task, so a
  retain that publishes is never removed: only copies that are borrowed and
  then destroyed are); a case payload borrows from what was switched on; an
  array element switched on is read in place across the switch; a load is
  borrowed across a `switch_enum` to its join and across the tests a
  speculative call leaves; an `inout` parameter is treated as a stack slot,
  exclusivity keeping a callee not handed it from writing it; and a load
  only borrowed for a call is borrowed before the call is inlined.
  Loop-wide (#179): a `let` that copies a borrowed parameter, or storage
  nothing changes wherever the binding may hold the copy — followed along
  every path from its store to its destroys, a loop's turn however many
  blocks long — borrows it, so the retain at the top of each turn and the
  release at the bottom pair across the turn and go (`BorrowLets`; never
  past a call, a write outside the function's slots, or a release that
  could run a deinit, and a copy made from the binding to publish to
  another task is its own); an element switched on is read in place when
  the array's borrow outlives the switch, a load switched on is borrowed
  when the case it does not destroy in only traps (`!`), and a copy loaded
  out of a slot destroyed after it is put away whole — into an enum, an
  array — is taken instead (`entries[i]!.value = v`).
- **Simplification.** Constant folding (now division, remainder and shifts
  that cannot trap, and extracts of what the function made), decided
  branches and checks, dominator-scoped common subexpressions over trivial
  values (pure and only-trapping builtins included), unreachable and
  forwarding blocks, block arguments that never change or are never used,
  and dead instructions.

Measured with the compiler before and after, each benchmark its fastest of
seven runs, `SABLE_STATS` for the counts. #179's first round, the
dictionary's (a hit no longer copies its entry, the entry's key or the
dictionary), took words from 6.8M retains to 0.8M, map-string from 6.7M to
1.5M and map-int from 1M to 41. Its second round and #188's — loop-wide
`let` borrowing, elements and loads read in place across a switch, copies
moved into an array, uncounted objects in the frame, promotion in loop
bodies of several blocks, local functions' `let`s by value, direct
witness calls:

| Benchmark | ms before | ms after | retains before | retains after | releases before | releases after |
|---|---|---|---|---|---|---|
| binary-trees | 422 | 419 | 0 | 0 | 14,723,790 | 14,723,790 |
| nbody | 45 | 45 | 0 | 0 | 1 | 0 |
| orders | 188 | 192 | 1,000 | 1,000 | 4,005,000 | 3,000 |
| shapes | 5.0 | 5.3 | 2,600 | 2,600 | 4,602 | 4,602 |
| sort | 40.4 | 39.8 | 967,230 | 967,230 | 1,447,230 | 1,447,230 |
| words | 37.8 | 37.8 | 800,039 | 400,037 | 1,400,106 | 1,000,084 |
| json | 8.8 | 9.0 | 63,576 | 60,549 | 208,280 | 205,249 |
| map-string | 51.3 | 47.3 | 1,462,179 | 200,039 | 1,862,202 | 600,062 |
| map-int | 165 | 165 | 41 | 41 | 67 | 67 |

Map-string's lookups now retain nothing, and words' updates copy each
entry once rather than twice. Orders' four million releases were of
inline blocks in the frame, now destroyed uncounted; its time is the
blocks' building and reading, not their counts, and is unchanged, as are
shapes', json's and the rest's within run-to-run noise. Allocations are
unchanged everywhere: these benchmarks' promotable objects were already
in the frame. Not yet: hoisting a release out of a loop when the value it
releases is not a `let` the loop borrows (the general case of docs/02
§2.2's release hoisting and sinking); updating an entry in place rather
than copying it out and back (words' remaining 400,000); the header of an
uncounted object, which the runtime's operations on arrays and blocks, a
deinit and a method call still read; a copy of an upcast of an object made
in the function (shapes' boxes); and devirtualising a speculative call by
profile rather than by class shape.

### Async, as implemented

Stackless, as #276 decided, in the shape of Swift's runtime; this is its
first stage (`tests/Sable.SIL.Tests/AsyncLoweringTests.cs`,
`tests/Sable.CodeGen.Tests/AsyncTests.cs`).

**The coroutine split.** Lowering marks an async function's SIL type
`async` and otherwise lowers it, and every call to one, as an ordinary
function and call; the places a task itself waits are builtins
(`task_wait` for `await task.value`, `task_yield`, `task_sleep`). Every SIL
pass sees them so, and inlines, borrows and simplifies through them. Last,
before the interpreter or the code generator runs the module,
`AsyncLowering` splits each async function that may suspend with the
transform generators use (`CoroutineSplit`): a *start* function, under the
function's name, that makes its frame and runs nothing, and a *resume*
function, the body, which runs from where the frame's state says until it
finishes or must wait, and says which. Every slot the body makes is a field
of the frame, and so is every value live across a point where it may
suspend — stored as it is made and read back where it is used, as bits,
since its ownership is the body's as before. A call to an async function
starts the callee's frame and polls it there and then, so an await that
need not wait costs a bump of the slab and a call; when the callee must
wait, the caller records its state and returns, and so on up to the task.
Every frame starts as its signature's head does — the resume function,
the state, the result, the error — so a caller that knows only the
signature, through a closure, a vtable or an existential, starts and polls
any of them alike. An async function that cannot reach a point where it may
suspend, called only by name, stays an ordinary function, as do its calls,
in a program compiled whole; a unit compiled separately keeps the async
convention for every async function, which is all another unit can see. An
async entry point is run as the program's first task by an ordinary entry
made for it, which polls it, lets the other tasks run while it waits, and
destroys the globals once every task has finished.

**Frames on the task's slab.** A frame is never counted and never made by
malloc: each task has a bump allocator (`runtime/frames.c`), its first
512-byte chunk inside the task object, further 16 KiB chunks from the heap
as its calls go deeper, one emptied chunk kept as a spare. Frames are made
as calls begin and given back as they end, so they come and go as a
stack's would; the slab checks that they do. A thousand rounds of an await
chain six deep, each round waiting, allocate exactly what ten rounds do,
and take no chunk (`AnAwaitAllocatesNothing`). On an M1 Max, an await of a
call that need not wait — a frame made, polled once and given back — costs
about 4 ns, through a chain ten deep, and a task suspending and being
resumed (two tasks taking turns at `Task.yield()`) about 4 ns too.

**The executor.** In the first stage one executor, on the program's one
thread, resumed each task queued in turn until it finished or waited
(`runtime/tasks.c`; the interpreter's is the same,
`SilInterpreter.Tasks.cs`), and the third stage made it a pool (below);
`SABLE_WORKERS=0` still runs a program so. Where a task waits, the runtime
puts it where it will be woken — among the waiters of the task it awaits,
at the back of the run queue, or among the sleepers, a heap by deadline —
and the task suspends. On the one executor only those places switch tasks,
so the interleaving is deterministic and the interpreter's is the native
one, and there is no thread per task. A task holds its
frames and nothing else while it waits: a hundred thousand tasks each
sleeping a second take 1.04 s and under a kilobyte each — the task
with its slab's first chunk 640 bytes, its `Task` object and its closure the
rest — 93 MB at the peak (`AHundredThousandSleepingTasksTakeASecondInBoundedMemory`).
`Clock.sleep` (now a clock's `sleep(for:)`) and `Sable.Concurrency`'s `sleep` are timers; only
`Runtime.sleep` holds the thread. `SABLE_STATS` says how many tasks ran, how many
were alive at once and were dropped, how many frames and chunks the slabs
made, and the peak memory. `Task.spawn { … }` queues a task (a class
holding the runtime's handle, the body and the result), `await task.value`
waits for it, and the program ends once every task has finished or can no
longer go on. A class or actor whose fields all have initial values is
made with `init()`.

**Structured concurrency** — the second stage, still on the one executor
(`tests/Sable.CodeGen.Tests/StructuredConcurrencyTests.cs`,
`tests/lib/Sable.Core/TaskTests.sb` and `TaskGroupTests.sb`). A task spawned
from another is its child: it takes its parent's priority unless given one
(`Task.spawn(priority:)`; `Task.currentPriority`), starts cancelled if its
parent is, is cancelled with it, and its parent, once its own body has
finished, finishes only when every child has. `Task.detached` is the one
way out: no parent, medium priority. Cancelling marks the task, runs the
handlers it has installed (`withTaskCancellationHandler`, which runs one
at once in a task cancelled already) where the cancelling is done, wakes a
sleep it is in, and goes on down through its children; the task goes on
until it asks — `Task.isCancelled`, or `Task.checkCancellation()`, which
throws `CancellationError`, as `Task.sleep`, a clock's `sleep` and `sleep` do
when the sleep was cut short. Waiting for a task or a group's next child
does not end early: the children are cancelled with the waiter, and finish
as soon as they notice.

`async let x = value` spawns the value as a child task where it is
declared: the binder binds the value as the body of an async closure,
which may await and throw without saying so, and a read of `x` waits for
it and gives its result, or throws what it threw (so needs `try` as well
as `await`). As its scope ends, on every way out, the child is cancelled —
nothing, once it has finished — and waited for. Task groups are Sable.Core's
`TaskGroup` and `ThrowingTaskGroup`, made only by `withTaskGroup(of:)` and
`withThrowingTaskGroup(of:)`: `spawn` adds a child, `next()` and `for
await` give results in the order the children finish (the runtime's group
hears of each as it finishes, and wakes the task waiting), and the scope
waits for every child not taken — cancelling them first if a throwing
body threw — so none outlives it.

A task that can never go on — still waiting as the program ends, on
something no task will give — is dropped. Every frame has a drop function
as well as a resume function: for the state it stopped in, it drops and
frees the callee frame it waits in, then destroys the owned values live
there (the verifier's ownership walk finds them) and every slot, which
holds a value or nothing. The runtime drops each such task from its root,
so what the frames held is given back — an async generator held part-way
through runs its defers — and a program that ends with tasks stuck on each
other leaks nothing.

**Many cores** — the third stage (`tests/Sable.CodeGen.Tests/MultiCoreTests.cs`,
`AsyncLoweringTests.ACallIntoAnActorFromOutsideItHopsThereAndBack`).

- *The pool.* Tasks run on one worker thread per core (`SABLE_WORKERS`
  says how many; 0 is the single executor above, on the main thread),
  started as the first task is spawned, so a program with none starts no
  thread. Each worker has a Chase–Lev deque per priority band, which it
  pushes to and which every worker — itself included, so its own run in
  the order they became ready — takes from at the far end; a task made
  ready off the workers goes to a global injection queue, per band, which
  a worker looks at first every sixteenth time so that its own work cannot
  starve it, and `Task.yield()` goes there too, behind everything queued.
  A worker looks for work in the highest band first — its own, injected,
  then stolen — spins briefly (two workers at most), and parks on a
  condition of its own; a task queued wakes one only when none is looking.
  Tasks never block a worker. A task that must wait only notes what it
  waits for and returns up to the worker, which puts it where it will be
  woken once its resume function has returned, so another worker can
  never resume it early; what it waits for may have happened meanwhile,
  and the task asks again as it resumes. The task tree — parents,
  children, waiters, groups, cancellation — is one spin lock's, held for a
  few instructions; cancellation handlers run once it is let go.
- *Timers.* One heap of sleepers under a lock of its own, served by
  whichever worker looks for work once the earliest is due; a parked
  worker waits no longer than the earliest deadline, and a new earlier one
  wakes them. (One timer thread would be a thread more to wake; per-worker
  heaps would need stealing of their own.)
- *Priorities* order the run queues by band (high, medium, low,
  background). A task waiting for one of a lower priority escalates it,
  what it waits for and its children; one queued already is queued again
  in its new band, and whichever entry is taken first runs it.
  `Task.currentPriority` is still the priority given.
- *Actors.* Each actor has a serial executor, three words after its
  fields: an inbox a task pushes itself onto without a lock when the actor
  is held, a queue its holder keeps, by band then by arrival, and the
  holder, which a task that waits escalates to its own priority (the
  fourth stage; `SABLE_STATS` counts it). A call into
  an actor from outside it, a read of its state, or a call into
  `@MainActor` code hops (`sable_actor_hop`): the task takes the actor if
  it is free and goes on where it is — no suspension — or waits its turn,
  and hops back to where it was after. A call inside the actor, or to what
  is `nonisolated`, does not hop; code in an async closure always does,
  since a spawned one runs on no actor, and when it is there already the
  hop is a comparison. Actors are reentrant (docs/06 §6.7): a task
  suspended inside one lets it go, so other calls run, and takes it again
  as it resumes. The main task, and every task on the main actor, runs on
  the main thread, which runs them while the main task waits and as the
  program ends. `WaitList` parks tasks inside an actor's method until
  another call wakes them, and `Channel` is now an actor whose receivers
  park there instead of spinning on `Task.yield()`; what it carries is
  `Sendable`.
- *Atomic counts where values cross tasks.* An object's header has a
  shared bit; a retain or release of an object with it is atomic, and of
  one without it a plain load and store, the bit tested inline. It is set
  — on the object and everything it reaches, a walk that stops at what is
  shared already — where a value crosses from one task to another: a
  spawned task's closure (and so its captures), a task's result, an actor
  call's arguments and result, a cancellation handler. The compiler emits
  the crossings from what the Sendable analysis says crosses; anything
  else stays on its task, which one thread runs at a time, handed between
  threads with a release and an acquire. The counts of retains and
  releases are kept only under `SABLE_STATS`, so cores share no counter.
- *The rest of the runtime is thread-safe:* each thread keeps a few free
  blocks per size class and moves batches to and from the slabs under the
  class's lock; the weak references' table, the leak set, the string
  cursor cache and coverage counters are under locks; the exclusivity
  checks, the trap site and the last I/O failure are per thread. A
  `print` writes its line whole.
- *The interpreter stays on one thread*, the single executor's order
  (bands included); a program whose output does not depend on how its
  tasks interleave prints the same natively on the pool. Tests of how they
  interleave run natively with `SABLE_WORKERS=0` (`Programs.OneThread`).

Measured on an M1 Max (8+2 cores, the machine loaded at 7), with
`tools/bench/concurrency.sh` (`benchmarks/concurrency/`), against stage 2's
single executor built from the commit before:

| | stage 2 | no pool | 1 worker | 10 workers |
|---|---|---|---|---|
| spawn+await, from main | 150 ns | 270 ns | 480 ns | 910 ns |
| spawn+await, in a task | 150 ns | 270 ns | 275 ns | 780 ns |
| ping-pong through an actor, parked | — | 60 ns | 60 ns | 230 ns |
| ping-pong, yielding | 7 ns | 110 ns | 110 ns | 400–660 ns |
| fan-out of 16 CPU-bound tasks | 1030 ms | 1030 ms | 1030 ms | 160 ms |

The fan-out runs 6.5 times faster on ten cores (`AFanOutRunsInParallel`
asks for at least twice on four). What stage 2 did in a few nanoseconds
it did by having nothing to share: an actor call was a plain call, and a
task was never handed to another thread. One thread now pays for
sharing the spawned closure and the task's result, the tree's lock and
the actor hops (a yielding ping-pong hops four times a round); many pay
for handing tasks between cores, which a task that does little between
spawns and awaits cannot win back.

**I/O** — the fourth stage (`tests/Sable.CodeGen.Tests/ReactorTests.cs`,
`tests/lib/Sable.Net/`'s socket tests).

- *The reactor* is the platform layer's (`platform.h`): kqueue on macOS and
  the BSDs, epoll on Linux, an I/O completion port on Windows told of
  sockets' readiness by `ProcessSocketNotifications` — the same shape on
  each: a socket added once, edge-triggered for both directions, with a
  key; a wait until one is ready, a wake, or a deadline. (The Windows one
  is written to match and not yet built.) It is opened as the pool starts,
  or as the first socket is. One parked thread at a time waits in it — a
  worker, or with no pool the main thread — until the earliest sleeper's
  deadline, so sockets and timers are served by one wait; the other
  parked workers wait on their conditions, and a task queued while only
  the reactor's thread is parked wakes it (`EVFILT_USER`, an eventfd, a
  posted completion). A worker kept busy looks at the reactor, without
  waiting, every 64th task, so that sockets ready are heard of while no
  worker parks.
- *Sockets* (`runtime/net.c`) are nonblocking, each an `IoSource` that
  keeps what the reactor has said: a bit per direction, set as it reports
  readiness and cleared only when a call finds the socket would block with
  no report since the call began, so a report that comes while no task
  waits is not lost. A call that would block says so, and the library
  waits (`Runtime.waitForSocket`, the `task_io_wait` builtin, a place a
  task suspends) and calls again. A source is never freed — closed, it is
  used again with its generation bumped, which a handle carries with the
  source's place in a table (24 bits of place and 38 of generation) — so a
  report gathered for a socket since closed lands on memory that is still
  a source, and a stale handle names nothing. A call is a user of its source
  while it runs, and a close waits for the users before the system's own:
  else the number just closed could be another socket's by the time a call
  on another thread used it. A task that has suspended looks, under the
  source's lock, whether its handle still names the source, and is among its
  waiters before it looks whether it is cancelled: a cancel between the two
  would be missed the other way round. Any number of tasks wait on a source
  the same way, and a report wakes them all. Cancelling a task waiting
  on a socket wakes it, and it throws; closing the socket wakes it too.
  `Sable.Net` has the types (docs/06), all `@immutable` and so Sendable: a
  connection accepted in one task is served in another. The interpreter has
  them too, over .NET's sockets, its executor waiting in `Socket.Select`
  when nothing else can run, and lookups done at once. A
  source may also be a child's pipe, or an event no reactor watches — a
  child ended, a signal came — which the thread it happens on reports.
- *The blocking pool.* `Task.blocking { … }` (Tokio's `spawn_blocking`)
  suspends the task and runs the body on a thread kept for work that
  blocks, made as work comes and none is free, up to 512
  (`SABLE_BLOCKING_THREADS`); the task is queued again once it is done,
  with what the body gave or threw. Whole files (`File`, `Directory`) and
  processes stay synchronous calls, for synchronous code and build
  scripts alike; an async caller runs them there —
  `try await Task.blocking { try File.read(path) }` — as Tokio's `fs` does,
  since no system's readiness interface covers regular files. On one
  worker, four tasks each blocking a thread for 200 ms take about 200 ms,
  while a fifth ticks on (`BlockingWorkRunsOffTheWorkers`).

ThreadSanitizer (`tools/tsan/run.sh`: the runtime compiled with
`-fsanitize=thread`, and the program's own functions instrumented) reports
nothing on the concurrency benchmarks, `examples/04-async-service.sb`, and
programs like MultiCoreTests' — an actor counted by sixteen tasks,
channels between four senders and four receivers, main-actor code called
from eight tasks, a CPU-bound fan-out. It found, and so no longer does,
the allocator's slab counts updated under different classes' locks. In
the fourth stage it reports nothing on those, the sleepers, the loopback
fan-out and an echo server with blocking work either, once the last
release of a shared object became one acquire-release operation instead
of a release and an acquire fence after, which it does not model: a task
object dropped by its spawner while the task ran looked like a race.

**What crosses, checked** — the fourth stage (`tests/negative/85-task-captures.sb`,
`86-actor-isolation-holes.sb`, `MultiCoreTests`).

- *A task's captures.* What `Task.spawn`'s, `Task.detached`'s, a group's
  `spawn`'s, `Task.blocking`'s or an `async let`'s closure captures must
  be Sendable, and a `var` is captured only by its value (S3236): a class
  two tasks shared raced on its fields. The parameters that run a closure
  so are declared `@Sendable` (an attribute of a closure parameter, as
  `@escaping` is), and take a closure written there — whose captures are
  checked — a named function, or another `@Sendable` parameter. In an
  `unsafe` block the programmer vouches for what is shared. `Task` is
  `@immutable`, so a handle crosses; an `@immutable` class may hold an
  actor.
- *What is reached later.* A value stored into a field of an object that
  is shared already is shared as it is stored (`share_into`, a test of the
  header's bit, before every store of a value holding references into a
  class's field or an array's element; `share_into_at`, after a call lent such
  a place, for what it left there; docs/02 §2.7 has the whole of it), and an
  object a weak reference names is shared as the
  first one is made, so a load through it on another thread never races a
  plain count.
- *Actor state from closures.* An assignment to an actor's state from an
  async closure written inside it — `Task.spawn { count += 1 }` in an
  actor's method — hops onto the actor for the read and the write
  together, as its reads and calls did. A closure that cannot wait, run on
  another thread (`Task.blocking`'s), may not touch an actor's state at all
  (S3238).
- *Calls that would not wait* are rejected, as Swift rejects them (S3237):
  an actor's isolated member cannot meet a protocol's requirement — code
  reaching it through a generic or an existential would call straight in —
  and an isolated method cannot be taken as a value from outside its actor.
  A `nonisolated` member can do both. (Swift lets an isolated method meet
  an *async* requirement through a thunk that hops; that is not built.)

**Debugging** — the fourth stage. An async function's resume function
declares its frame's fields to the debugger as its variables — its
parameters and the locals it keeps across its awaits, by their own names,
through the box of one a closure captures — so lldb shows them in a
suspended-and-resumed function as in any other
(`DebuggerTests.LldbShowsTheLocalsOfAnAsyncFunction`). A trap's backtrace
with `SABLE_BACKTRACE` set is followed by the *async backtrace*: the
running task's calls, then those of the task that spawned it, waiting,
and so on up to the program's own task, read from each task's slab — a
frame for each call not yet finished, named by its resume function, with
the await it is at.

**Measured** — the fourth stage, on an M1 Max (8+2 cores, 64 GB), loaded
by other work as these ran, with `tools/bench/concurrency.sh` and
`benchmarks/concurrency/compare/` (.NET 11's tasks, `dotnet run -c
Release`; Swift 6.4's, `swiftc -O`; Go is not installed here, so no
goroutines). A million tasks each sleeping a second (`sleepers.sb`) take
1.46 s with no pool and 1.93 s on ten workers — the time is spawning
them — and 1.07 GB at the peak, 1.04 KB a task: the task, its slab's first
576-byte chunk inside it (896 bytes together), its `Task` object and its
closure. That is the stage-1 extrapolation, measured.

| | Sable, no pool | Sable, 1 worker | Sable, 10 workers | .NET tasks | Swift tasks |
|---|---|---|---|---|---|
| spawn+await, from main | 283 ns | 682 ns | 1262 ns | 323 ns | 558 ns |
| spawn+await, in a task | 298 ns | 347 ns | 946 ns | — | — |
| ping-pong through an actor, yielding | 108 ns | 113 ns | 716 ns | 1375 ns (two channels) | 528 ns |
| a million tasks sleeping 1 s | 1.46 s, 1.07 GB | 1.52 s | 1.93 s, 1.07 GB | 2.41 s, 0.55 GB | 2.27 s, 2.13 GB |
| fan-out over loopback (50 clients at once, 2000 requests) | 199 µs | 489 µs | 256 µs | 135–747 µs | — |

The fan-out (`fan-out-http.sb`) is a server task serving each connection
in a child task — a request read, 512 bytes written — and fifty client
tasks connecting, asking and reading to the end at once, forty times;
every wait is on the reactor. Its time per request on loopback swings
between runs by four or five times, .NET's alike, with the host's load.
(Fifty: past the listen backlog, 128 on macOS, connections are refused.)

Not yet: task-local values. A drop runs no defers of the async functions
dropped, only those of a generator whose object goes with them.

### Generators, as implemented

A generator (SYNTAX.md §5) is split as a coroutine in SIL
(`src/Sable.SIL/SilGen.Generators.cs`). The binder gives its result two
types (`Compilation.Generators.cs`): a sequence, a struct of what the call
captured (`self`, the arguments, a closure's context), and an iterator. The
body is lowered once, as an ordinary function — its *resume* function — and
then made resumable: every slot it allocated (every local, a loop's counter,
the array a loop walks, a range's bound) becomes a field of the iterator, a
`yield` stores the state to resume at and returns the element, and the entry
branches on the state. Nothing but slots may live across a yield, which
lowering checks. Storage that holds nothing is all zeros, so the iterator
starts with every slot's field empty, and copying or dropping it copies or
drops exactly what the body holds: no allocation, and a generator over a
range, specialised, inlines to a loop (`GeneratorLoweringTests`). A body with
a `defer` keeps its state in an object instead, whose deinit resumes the body
to leave it from where it stopped — `return` there, running the pending
defers — when the last iterator holding it goes. An async generator is the
same with an async `next()`, whose resume function is split again where it
awaits, as any async function is. A generator that throws gives a sequence whose `Failure` is its error:
the resume function throws, the iterator's `next()` passes the error on, and
the throwing path stores the finished state, after the defers pending have
run, as a return does; the generator's own call, which only captures, does
not throw. A generator in a protocol's extension is generic over a `Self`
of its sequence's and iterator's own, not the protocol's: a requirement
called on the iterator (`makeIterator()`, `next()`) binds the protocol's
Self to the iterator, and the body's Self, its associated types read through
it, is the conformer the call names, so each call site's opaque result is
specialised for its conformer. Async functions are split by the same transform
(`CoroutineSplit`), after the passes ("Async, as implemented").

### Throwing iteration, as implemented

`Iterator.next()` is `throws(Failure)`, `Failure: Error = Never` an
associated type of Iterator, Sequence and their async kinds, with
`Iter.Failure == Failure`; Collection requires `Failure == Never`. A
conformer's Failure is read off its `next()` (Never when it throws nothing),
a sequence's off its iterator. The binder reads a call's thrown type through
the requirements in force (`TypeEquivalence`), so `next()` over an array, or
over S where `S.Failure == Never`, throws nothing and needs no `try`; for a
loop through an iterator, `for try` marks `next()` as `try` would. Lowering
is unchanged: specialised, a Never-failure `next()` is an ordinary function,
and a throwing one a `try_apply` whose error leaves the loop. Sequence's
algorithms are `rethrows(Failure)`: `rethrows(E)` throws what the closures
do and E as well, read at each call, so they throw only where the sequence
or a closure does; the lazy views' iterators throw their base's Failure.

### 4.4 Exit criteria

- [x] Stdlib is self-hosted Sable, no C# in the shipping library — every
      module is Sable, and so are the primitives' members: `String`,
      `Char`, the numbers, `Bool`, `print`, `Runtime` and `Region.reset`
      are declared in `lib/Sable.Core`, over `@builtin`s (see "Sable.Core
      in Sable"). The primitive types themselves stay compiler-known, as
      the plan allows. The array family's members (`count`, `isEmpty`,
      `contains`, `slice`, `append`, `removeLast`, `clear`, and bytes'
      `toString`) are Sable too, in `lib/Sable.Core/Array.sb`, by array extensions,
      `extension<Element> Element[>..] { … }` (SYNTAX.md §11), which range
      over every element type. So are `Region.alloc`, `Task` (`spawn`, the async getter
      `value`, `yield`), `Decoded`, `Bytes.of` and the `InlineLayout`
      protocol `fromBytes` comes from, in `Runtime.sb`,
      `Task.sb` and `InlineLayout.sb`, their bodies the compiler's
      (`@intrinsic`, and `fromBytes` synthesised per `@inline_layout`
      struct as `@derive` synthesises `==`; see "Sable.Core in Sable").
      And `Range` and `ClosedRange` — what the binder
      makes of `a..<b` and `a...b` — are Sable (`lib/Sable.Core/Range.sb` and `ClosedRange.sb`:
      `lowerBound`, `upperBound`, `isEmpty`, `contains`, over a `Comparable`
      bound, and `count` where the bound is an Int), as are the one-sided
      `PartialRangeUpTo`, `PartialRangeThrough` and `PartialRangeFrom`
      (`..<b`, `...b`, `a...`) and `stride(from:to:by:)`. They lower as
      values: stored, passed, returned and iterated — a range of any integer
      type or of characters counting in its own type — while a `for` over a
      range written in the loop still builds none. An extension's `where`
      clause (`extension Range where Bound == Int`) holds in its members and
      decides on which constructions they are found.
      A `for` over anything but an array, a dictionary or a range goes
      through `Sequence` and `Iterator` (`lib/Sable.Core/Sequence.sb`) —
      `makeIterator()`, then `next()` until none — which `String` conforms
      to; `for await` the same through `AsyncSequence`, which
      `Sable.Concurrency`'s `Channel` conforms to. Nothing else is declared in C# (`CoreTypes`): only the types the
      compiler knows by sight, and the shells the binder desugars to
      (`Error`, `From`, `Optional`, `Dictionary`). I128, U128 and F16 are SIL
      types (`i128` and `half` in LLVM), with literals up to 128 bits and the
      same members, printing and conversions as the other numbers. The built-in `[Key: Value]`
      dictionary is Sable.Core's `Dictionary` (`lib/Sable.Core/Dictionary.sb`),
      a hash table written in Sable over a seeded `Hasher`, which the
      compiler knows only by name: its literal, subscript and iteration are
      the type's own members (docs/06, "Dictionaries and hashing")
- [x] Reflection: hello-world binary grows < 50 KB with reflection off —
      it grows not at all (51,736 bytes either way): reflection is code
      synthesised per type a program reflects on, and nothing else
- [x] Dictionary/List/String within 15% of their .NET equivalents on a
      standard benchmark set — Dictionary (`map-int` 1.10, `map-string`
      0.90), List (`list` 1.14) and String (`string` 0.72) are met, close
      to the line for `list`; so is sorting (`sort` 1.00, a stable merge
      sort against `List.Sort`'s introsort) — see "Against .NET" above
- [ ] A real program ships: a static site generator or an HTTP service, in
      Sable, used daily by the team — written (`examples/ssg`, see "The
      first real program"); daily use is still to come

---

## Phase 5 — Toolchain, package manager, IDE
**~3–4 months. Deliverable: a toolchain people can actually adopt.**

Full design in [05-toolchain.md](05-toolchain.md).

- `sable` single binary: `new init build run test bench fmt doc check add
  update publish clean bindgen repl`
- A manifest, `sable.lock`, semver resolution (PubGrub), workspaces,
  feature flags, build scripts with a capability sandbox. The manifest is
  `package.sb` (and `workspace.sb`) — Sable, evaluated in the interpreter
  under docs/05 §5.2.4's rules to the canonical `sable.toml`, which is cached
  by content and published in its place; `sable new`, `init`, `add` and
  `remove` write and edit it through the syntax tree
- Registry: content-addressed, immutable versions, signed, with a mirror story
- **Incremental compilation** on a query-based engine (Salsa / `rustc`-style),
  with a fingerprint-keyed on-disk cache. Design for it in the symbol layer in
  Phase 1; implement it here.
- **LSP server** reusing `SemanticModel` directly: completion, hover, go-to-def,
  find-refs, rename, diagnostics, inlay hints for inferred types *and inferred
  roots* (the root-inference inlay hint is the feature that makes the borrow
  checker learnable).
- Debugger: DWARF plus an lldb formatter plugin for `String`, `List`, unions,
  and existentials.

### `sable test` and `sable bench`, as implemented

- **The attributes.** `@test` marks a function that takes nothing and returns
  nothing; it may `throws(E)`, and an error it throws fails it. `@bench`
  marks one that takes nothing (its whole body is timed) or one
  `Sable.Testing.Bencher` (it times what it hands `b.iterate { … }`, set-up
  outside). Either is a module's function or a static method of a non-generic
  type; neither is generic or async. The checks run in every build, so a
  malformed test fails `sable build` too: S3100–S3110
  (`Compilation.Testing.cs`, reported by each file's semantic model). In an
  ordinary build the functions are dead code like any other.
- **Assertions** live in a new library module, `lib/Sable.Testing`, with no
  core changes beyond one builtin: `assert(cond[, message])`, `fail(message)`,
  `assertEqual(a, b)` and `assertNotEqual` for `Equatable` + `Debug` values —
  both shown as source would write them, with a caret under the first
  difference — and `assertEqualText(a, b)`, a line diff (LCS, `-left
  +right`) for multi-line text. A failed assertion writes `assertion failed:
  …` to stderr and traps (through `try!` on an error nothing can catch).
- **The harness is Sable source the compiler writes** (`TestHarness.cs`).
  The program is first checked alone, so its errors are its own; then, per
  module with tests, a file of *that* module lists a closure per test (so a
  test need not be public), and a file of module `SableHarness` imports just
  those lists (`import M.{ sableHarnessList0 }`, nothing else of the
  program's, so no name of the program's can clash with Sable.Testing's),
  builds `TestCase { name, location, run }` values and calls
  `Sable.Testing.runTests` — or `BenchCase`s and `runBenchmarks`.
  `SilGen.Lower(compilation, entryPoint)` starts the program there instead of
  at the program's own `@main`.
- **Isolation: a process per test.** The harness starts itself again
  (`Runtime.executablePath()`, a new builtin over `argv[0]`) with
  `--run-test <Module.name>` for each selected test, timing each, keeping
  its output and showing it only on failure. A trap — an assertion's, an index
  out of range, an overflow — fails that test and no other. With
  `--in-process`, or where there is no executable to restart (the SIL
  interpreter, whose `executablePath` is empty), the tests run in one process
  without times, so the interpreter and a native build print the same, and
  the first failure stops the run. Harness options: filters (substring of
  `Module.function`, any of several), `--exact`, `--list`, `--show-output`.
  Exit code 0 when all selected pass, 1 when one fails, 2 for bad usage.
- **Benchmarks** warm up (doubling a batch until 50 ms have passed), size a
  sample from that to last ≥ 5 ms, take 20 samples, and report mean, median
  and standard deviation per iteration. `--save-baseline <name>` writes
  `target/bench/<name>.txt` (or `--baseline-dir`): one tab-separated line per
  benchmark in picoseconds. `--baseline <name>` (or a kept `default`)
  compares: the change in the mean, flagged `REGRESSED` beyond the noise —
  twice the two runs' deviations relative to the old mean, never under 5% —
  `improved` below it, else `within noise`. `--quick` and `--samples <n>`
  shorten a run.
- **Commands.** `sable test <file.sb>... [filter...] [--doc]` (unoptimised
  by default, `--release` to optimise) and `sable bench <file.sb>...`
  (optimised) share `HarnessCommand`: build the harness, run it with the
  rest of the arguments, exit with its code. With no file named they test
  the package in the current directory (or the nearest above): its library,
  the files under its `tests/` and `benches/` — which an ordinary build
  leaves out — and its dependencies' libraries, its `[dev-dependencies]`
  and what they depend on among them (`PackageBuilder.TestProgram`).
- **`--doc`.** Each ` ```sable ` block of a `///` comment (not ` ```sable
  ignore `) becomes `@test fn doc_<item>_<n>()` in a file of the documented
  file's module, with that file's imports and Sable.Testing; the file has the
  original's name and the code its original line numbers, so an error in an
  example points into the doc comment.
- **Proved on the library:** `tests/lib/<Module>/<Type>Tests.sb` test the
  library's modules with `@test`/`@bench`, and the test suite runs them.
- **Later (#205).** A test may be `async`: each test's closure is
  `async () -> String?` and the harness's entry point and `runTests` are
  async, awaiting it. What a test
  throws is in its failure's report (`the test threw an error: Oops.Bad`,
  as interpolation describes it). `@test(traps)` passes by trapping and fails
  by returning, `@test(traps: "out of range")` also wants that in the trap's
  message; a failed assertion is not the trap. In one process such a test
  is ignored, said so. `sable test` now runs the processes itself
  (`TestRunner.cs`) from what the harness's `--describe` says: several at
  once (`-j`, one per processor by default), each line as its test
  finishes, failures reported in declaration order, the same text the
  harness's own runner prints; `--timeout <s>` stops and fails a test that
  runs longer, and one running over 60 s with no limit is said to be.
  `--harness-out <path>` (with `-g`) builds the harness and runs nothing, so
  the VS Code Test Explorer's Debug profile starts `<harness> --run-test
  <name>` under the debugger.
- `sable test --coverage` counts lines with counters in the code
  (`Sable.SIL/Coverage.cs`): before any pass, each block of a function from
  the package's own files (not `tests/`, not dependencies or the library)
  starts with the builtin `coverage_hit(n)`, which the runtime counts
  (`runtime/coverage.c`) and appends to the file `SABLE_COVERAGE` names when
  the program ends, or traps. Every test process appends to one file; the
  runner adds them up and prints the share of lines run per file with the
  lines that never ran, and writes `target/coverage/lcov.info`. A line counts
  as often as the most-run block it is in; code the optimiser removes is
  still a line, since the counters go in first. The SIL interpreter counts
  them too (`SilInterpreter.CoverageCounts`), the same as native code.
- Branch coverage: lowering, asked to (`SilGen.Lower(…, markDecisions)`,
  `SilGen.Decisions.cs`), records each decision of the source
  (`SilFunction.Decisions`: `if`, `guard`, `while`, `?:`, `&&`/`||`,
  `match`, `switch`, `if let`/`if case`, `??`, `try?`/`try!`/`catch`) and
  gives each outcome a block only it enters (`SilBlock.Outcome`) — one of
  its own on the way to a target entered other ways too — which
  `Coverage.Instrument` starts with a counter like a line's. Unmarked
  builds lower as before. An outcome's count is the sum of its blocks', a
  decision lowered more than once (a generic's, a `defer`'s) adds up. The
  runner prints branches run per file and each decision some of whose
  outcomes never happened, and `lcov.info` has `BRDA`/`BRF`/`BRH`.
- The VS Code extension's Test Explorer has a Coverage profile: it runs
  `sable test --coverage` for the selected tests and reports each file's
  lines and branches from `lcov.info` (`editors/vscode/src/coverage.js`).
- A benchmark may be `async` too: each bench's closure is
  `async (Bencher) -> Void`, `runBenchmarks` and the entry point are async,
  and `Bencher.iterateAsync` times a body that awaits, each iteration
  awaited before the next (one with no Bencher is timed whole so).
  `blackBox(value)` (Sable.Core, `BlackBox.sb`) keeps the optimiser from
  deleting or folding a benchmark's work: an `@intrinsic` SilGen lowers to
  the value stored in a slot, the builtin `black_box` given the slot's
  address, and the value taken out again; natively the builtin is an empty
  `asm sideeffect` that may read and write any memory, so LLVM must store
  the value and cannot know what it loads back. The interpreter does
  nothing for it.
- Isolation: each test is a process of its own, so tests that share
  globals, or start tasks on the runtime's threads, never see each other;
  several run at once only as several processes.
- **Deferred:** snapshot and property testing (docs/06 lists them under
  Sable.Testing; #73, #107); a time limit inside the harness's own runner
  (`--in-process`, or the harness run directly rather than by `sable
  test`), which would need a builtin to wait on a child with one.

### Packages, as implemented

In `src/Sable.Driver/Packages/`, with a command per file under `Commands/`.

- **Manifest.** A TOML 1.0 reader (tables, arrays of tables, inline tables,
  dotted and quoted keys, every string and integer form, floats, booleans,
  arrays, comments; dates checked and kept as text) whose errors carry file,
  line and column, held to the library's parser (Sable.Serialize's Toml) by
  `TomlAgreementTests`: the driver reads its manifests before it can compile
  a library to read them with. `sable.toml` (written by hand, or what a `package.sb` evaluates
  to) is read into a `Manifest` — `[package]`, `[lib]`,
  `[[bin]]`, the three dependency tables, `[features]`, `[profile.dev]` and
  `[profile.release]`, `[workspace]`, `[build]`, `[registry] path` — and
  every validation error names the file, the line and the key. Profiles act
  on `opt-level` (clang's `-O0` to `-O3`, `-Os`, `-Oz`, and the SIL passes
  of that level: docs/05 §5.2.11), `debug` (at any
  level: an optimised build with debug information runs the SIL passes and
  keeps the entry point out of the C `main`, so the program's functions keep
  theirs), `lto` (`fat`, `thin` or off; on Linux through lld, used when clang
  can link with it), `codegen-units = 1` (compile whole even in an
  unoptimised debug build), `incremental = false` (the same),
  `strip`, `rpath`, `exclusivity`, `leak-check`
  (`sable run` sets `SABLE_CHECK_LEAKS`), `arc-checks` (compiled code's
  retains and releases trap on an object whose deinit has run),
  `debug-assertions` (`DEBUG` or `RELEASE` for `#if` in every package's
  files, whatever the opt-level) and `overflow-checks = false`
  (integer `+`, `-`, `*`, negation, `abs` and `as` wrap rather than trap:
  SIL has `wrapping_` forms of them, which the interpreter, the emitter and
  constant folding read as written; division by zero, shifts out of range
  and an overflowing `const` are still errors). `package.sb` sets all of
  them, `optSize: .size` or `.minSize` for `-Os` and `-Oz`. A build prints
  the warnings of the profile it uses: `panic = "unwind"` (a trap always
  aborts), a `codegen-units` count other than 1 (the units are the
  packages), and what the target cannot do (`lto` written where the link
  takes no bitcode, `strip` and `rpath` on Windows) have no effect.
- **Packages of many files.** A package is every `.sb` under `src/`:
  `src/main.sb` (or `[[bin]]`, or `src/bin/*.sb`) is a binary's entry, the
  rest is the library. A binary is compiled with its package's library and
  every dependency's, each file parsed with its own package's features (so
  `#if feature(x)` works). `Pipeline.Compile` takes many trees, and reports
  each error against its own file. Output goes to `target/dev/` or
  `target/release/`.
- **Commands.** `new`, `init`, `build` and `run` (no file: the package in or
  above the current directory; a `.sb` argument keeps the single-file
  behaviour), `check` (front end only), `clean`, `add` (registry or
  `--path`, `--dev`; the manifest is edited as text so comments survive, and
  an edit that does not resolve is undone), `remove`, `update [pkg]`,
  `tree`, `publish [--dry-run] [--allow-dirty]`, `yank [--undo]`, `vendor`.
- **Resolution.** Cargo's requirement syntax (`1.2` = `^1.2`, `~`, `=`,
  comparisons, wildcards, comma-joined) over a version-set algebra; a
  requirement's upper bound excludes the bound's pre-releases, and a
  pre-release is chosen only when a requirement names one. PubGrub with
  unit propagation, conflict-driven learning and pub's derivation-tree
  explanations (numbered lines, "which depends on", "depends on both"),
  plus a note when a failure comes from two major versions of one package.
  Features are packages of their own (`name[feature]` depends on its
  package at the same version and on what the feature turns on, `dep:`
  included), so the resolver unifies them across the graph. A weak
  `dep?/feature` is applied after resolution, at the version chosen, when
  something else turned `dep` on, and resolution runs again until there is
  nothing left to turn on.
- **Lock file.** `sable.lock`, written deterministically (sorted, no times,
  same bytes for the same resolution), holds each package's version, source
  and dependencies, and each registry package's sha256. It resolves with
  every feature of every member, so feature flags do not churn it; builds
  prefer its versions, `update` re-resolves, `--locked` refuses to change
  it, and a checksum that disagrees with the registry is an error.
- **Workspaces.** `[workspace] members` (with `*` globs) and `exclude`;
  members share one lock and one `target/` at the root; a command run in a
  member works on it, at the root on all of them, `-p` picks one. Path
  dependencies resolve transitively.
- **Registry.** A directory (`$SABLE_REGISTRY`, else `~/.sable/registry`,
  else `[registry] path`): `index/<name>.json` and `blobs/<sha256>.tar`.
  Archives are deterministic ustar; a version is immutable once published;
  yanking hides a version from new resolutions but not from a lock that has
  it. Each entry is signed with ECDSA P-256 (key at `~/.sable/key`, made on
  first publish, mode 0600); the first publisher's key owns the package, and
  fetching verifies the owner, the signature and the checksum. `publish`
  type-checks first, refuses path-only dependencies and a dirty git tree,
  and `--dry-run` lists every file, its size and the checksum. Fetched
  packages are unpacked into `~/.sable/cache`; `vendor` copies them into
  `vendor/`, which builds read first; with the cache or `vendor/` holding
  what the lock names, nothing reads the registry.
- **Network registry** (docs/05 §5.5, "As built"). The registry may be a URL:
  `HttpRegistry` speaks a small HTTP protocol, and `RegistryServer` (`sable
  registry serve`) serves it over a directory registry. Reading is open and
  checked by the client as before; writing takes a token (`sable login`)
  and the owner's signature. Owners are keys, several per package (`sable
  owner`); names are namespaced `org/name`, unprefixed ones reserved for the
  standard ecosystem's tokens. `publish` also wants a tag for the version
  and passing tests; `search`, `install`/`uninstall` and `cache list|prune|clean|warm`
  exist.

Deferred: workspace inheritance
(`version.workspace = true` and `{ workspace = true }` are refused,
`[workspace.package]` and `[workspace.dependencies]` warned about). Removed
from the format, as errors: `[reflect]` (what is reflected on is said in
code, `@reflectable` or `extension T: Reflectable {}`) and `[targets]`'s
`runtime` (there are no runtime profiles; its `reflection = false` is
built). Profiles of a package's own (`[profile.<name>]` with `inherits`,
`--profile <name>`, output in `target/<name>/`) are built, as are
`package.readme`, `keywords` (in the index, searched), `include`/`exclude`
(globs of what is published) and `publish = false`; `categories`,
`homepage`, `documentation` and `[workspace] resolver` are warned about and
ignored, as is an unknown key of `[build]`, `[registry]` and `[generators]`,
and a `debug` level short of full. A dependency's `package =` and
`registry =` are refused: code imports a package's modules, so a rename means
nothing, and every dependency comes from the workspace's one registry. Cargo's
`[target.'cfg(...)'.dependencies]` is refused in favour of `when`. Every
manifest warning is printed by `check`, `build`, `run`, `test` and `bench`.
`publish` checks neither for a tag nor for passing tests; `org/name`
packages are accepted but unprefixed names are not reserved; module names
are not namespaced by package, so two packages declaring the same module
merge; the cache has no garbage collection; there is no network registry.

### The language server, as implemented

`sable lsp` (`src/Sable.LanguageServer`, `src/Sable.Driver/Commands/LspCommand.cs`):
JSON-RPC 2.0 over stdio with `Content-Length` framing, System.Text.Json and
nothing else. The workspace is the open documents plus the `.sb` files under
the root (less `target/`, `vendor/` and hidden directories). Each package
under the root — found by its manifest, `package.sb` or `sable.toml`, and
resolved as `sable check` resolves it — is a compilation of its own: its
files, parsed with the features it is built with by default, and every
library file of every package it depends on (path, registry or vendored,
with theirs), so the members of a workspace are compiled apart. Files in no
package are one loose program, as before. Each compilation has the library
modules its trees import (the library's trees are shared, parsed once). A
change to a manifest or to `sable.lock` reads the packages again. Edits are incremental
(`TextDocumentSyncKind.Incremental`) and reparsed by the incremental parser;
positions are UTF-16 columns, as the protocol defaults to. The message loop
applies document changes in order; each request runs on the thread pool
against the snapshot current when it arrived, and `$/cancelRequest` cancels
it. A compilation is not thread-safe, so whatever binds holds its gate, one
member at a time.

- Diagnostics for every file of the workspace, open ones first, debounced
  (250 ms of quiet; a change cancels a round under way), bound member by
  member. Declaration and signature diagnostics carry no file
  (`GetAllDiagnosticsByFile` says so); they are placed by span and the name
  their message quotes. The type errors go out first; then the lints (at the
  package's `[lints]` levels, on a file with no errors of its own) and the
  checks on SIL (moves, exclusivity, roots: each package lowered once its types
  are right). Each diagnostic links to its `docs/diagnostics/` page
  (`codeDescription`) when it has one; unused code is tagged as such. A client
  that can pull (`textDocument/diagnostic`, `workspace/diagnostic` for the
  files not open) is not pushed to; it is sent `workspace/diagnostic/refresh`
  when a round changes something. A package's analysis is reused while none of
  its files change, so an edit elsewhere does not rebind or relower it.
- The workspace is every folder the editor has open, kept as folders are added
  and removed (`didChangeWorkspaceFolders`); a manifest saved, changed on disk
  or the lock file changing reads the packages again. A client that allows it
  is asked to watch `*.sb`, `sable.toml` and `sable.lock`; one that shows
  progress is shown the loading. The setting `sable.diagnostics.workspace`
  (initialisation options or `didChangeConfiguration`) turns off the files
  nobody opened.
- Formatting the document, a range (its lines, and any the formatter joins to
  them) and on typing `}` (what it closes); a file with a syntax error has its
  whole top-level declarations without one formatted. A place in the text is
  matched to one in the formatted text by the non-whitespace characters before
  it, which formatting keeps.
- Hover (signature, container, doc comment; an expression's type; like a
  completion, bound speculatively against the newest compilation), definition
  (into a copy of the library file when it lands there), references (a
  parameter whose label is its name is also named by each argument's label, in
  any file), rename
  with `prepareRename` (refused for anything the library or the compiler
  declares, and for keywords), document symbols, signature help.
- Declaration (the definition), type definition (through optionals,
  references and arrays), implementation (the workspace's types conforming to a
  protocol or inheriting from a class, however indirectly, and their members
  of a requirement's name), document highlights (the declaration as a write),
  workspace symbols (syntax alone, by the query's letters in order), folding
  ranges (brackets over several lines, runs of imports and of comments),
  selection ranges (the token, then each node around it), semantic tokens
  (full and range: types by kind, parameters, locals, fields, cases,
  functions and methods; declaration, readonly, static and library
  modifiers), call hierarchy (callers by the references' enclosing members,
  callees by the bound calls) and type hierarchy (supertypes from the
  declaration, conforming extensions and `@derive`; subtypes in the
  workspace). Quick fixes for the lints: what each message suggests (`_` before
  an unused name, a parameter renamed keeping its label, the import removed,
  `let` for `var`) and `@allow(lint)` on the declaration. Quick fixes for the
  compiler's own errors: `try` before a call that can throw, a match's missing
  cases, a stub of each requirement a type lacks (the bodies `unreachable()`,
  which `Sable.Core` now has: a trap of type `Never`), the import of a module —
  the workspace's or any library one — with a name not found, and the label a
  call's parameter has. `source.fixAll` and the `sable.fixAll` command
  (`workspace/executeCommand`, applied through `workspace/applyEdit`) take
  every first fix at once. Code lenses count each type's and function's
  references (resolved when shown; `sable.showReferences` is the client's
  command that lists them), and document links take an import to its
  module's first file and a URL in a comment to its page.
  `completionItem/resolve` adds a symbol's doc comment to the item shown.
- Completion: members after `.` and `?.` (statics after a type name), the
  names in scope — locals declared before the caret, parameters, the enclosing
  types' members, the module, imports, `Sable.Core` — and keywords; module
  names after `import`. A function is written as a call with a placeholder per
  argument, under its label, for a client that takes snippets. At the start of
  an argument, the labels the call's parameters take come first. After a bare
  `.`, the cases and statics of the type the context wants (a parameter's, an
  annotated declaration's, an assignment's or comparison's other side, the
  function's result). A public name of another module of the compilation that
  the file does not import is offered with the `import` it needs, and so is
  one of a library module nothing imports yet (every library module is
  compiled once, in the background, for its names).
- Inlay hints: the type of each `let`/`var` written without one; the
  parameter an argument without a label is for, where the call takes more than
  one and the argument is not already that name; a closure's inferred
  parameter and result types; and the inferred roots — after a function's reference result, `roots(a, b)`, the
  parameters it may refer into, from root inference's summaries
  (`SilFunction.ResultRoots`, read from a separate lowering so the server's
  compilation is not held); and on a local reference, what it refers into
  (`let q = &p` shows `roots(p)`), worked out in the front end through
  fields, joins, other references and calls by those summaries. Lowering runs
  only when the requested range has a function returning a reference or a `&`.

The semantic model grew what an editor needs (`SemanticModel.Tooling.cs`,
`tests/Sable.Symbols.Tests/ToolingTests.cs`): member models, which bind one
declaration; speculative member models, which bind a declaration's new text
against the symbols an older compilation has; `LookupSymbols(position)`
(the binder records its scope where asked); `GetMembersOf(type)`;
`GetSymbolAt` and `FindReferences`, type names included (the type binder
records what each written name resolved to).

**Latency.** A compilation is rebuilt only once the workspace has been quiet,
with every signature bound before it is used. A completion or signature help
never waits for one: it binds the member being edited, as it is now, against
the newest compilation there is. Only a member the newest compilation does not
have — one being written from scratch — waits for a new compilation. Hover
binds the member under the cursor the same way. References and rename, after
edits inside members' bodies only, answer from an older compilation — the one
with the most files already bound — with each edited member bound again
speculatively and the rest of its file moved by the edits (a body cannot
change what anything outside it means); any other edit waits for a
compilation of exactly the current text.
`tools/Sable.LspBench` writes a 205,180-line project (180 files, nine modules
importing one another), starts `sable lsp` as a process, and types statements
into function bodies a key at a time (40–120 ms apart), asking for completion
after each and pausing so the server rebuilds in the background (two dozen rebuilds
per run), timed at the client. Release build, macOS arm64, four runs of 831
completions: p50 1.0–1.7 ms, **p95 5.2–9.4 ms**, p99 10–24 ms, max 15–34 ms;
after a `.` alone, p95 2.7–12.6 ms. For contrast, a hover just after an edit,
which waited for a compilation of the whole project, took 125–170 ms, and the
first diagnostics came 1.3–1.5 s after start (reading and parsing 205k lines
included).

The benchmark also times references just after an edit in a body. They used
to wait for a new compilation and then bind every file naming the symbol in
it — all 180 here — every time: 10–28 s per request on a heavily loaded
machine (load average 60–200, other builds running). Answered from the
compilation already bound, the first takes as long (binding it), and the
next five 0.4–0.65 s on the same machine. The first diagnostics now skip the
quiet period after loading (there is no typing to wait out), and a small
program is compiled while the files are read, so the compiler's code is
jitted by the time the workspace's compilation starts: that compilation
went from 1.6–2.1 s to 0.9–1.2 s, and first diagnostics from about 3.5 s to
1.9–2.4 s, measured back to back under the same load. Unloaded figures are
still to be taken again.

Deferred: diagnostics for files that are not open; completion of argument
labels, implicit members (`.Case` with no receiver) and snippets; references
to argument labels; root hints when the workspace does not lower to SIL (a
program with errors, or constructs the lowering does not take yet); code
actions, semantic tokens, workspace symbols, and watching files the client
does not report. (Formatting through the server came later, with the VS Code
extension: `textDocument/formatting` is `SyntaxFormatter`'s output as one edit
over the lines that change, and nothing for a file with a syntax error.)

**The VS Code extension** (`editors/vscode`, plain JavaScript checked by
`tsc --checkJs`, no bundler) is the client for all of it, and drives the rest of
the toolchain:
- The language server through `vscode-languageclient`. Its status is shown in
  the status bar, and a quick fix on each coded diagnostic opens the code's
  `sable explain` page.
- Run and Debug above `main` and in the editor title.
- Tasks for `build`, `check`, `run`, `test`, `bench`, `fmt` and `clean` in each
  package, with problem matchers for diagnostics (`$sable`) and test failures
  (`$sable-test`).
- The Test Explorer, from `sable test --list`. A run is one `sable test <names>
  --exact` per package, read as it prints; a failure carries the differing
  values as expected and actual output.
  A Debug profile builds the package's harness with `-g --harness-out` and
  debugs each selected test in it, one after another.
- A `sable` debug type. It builds the package (its dev profile has debug
  information) or a lone file with `-g`. It writes the formatter plugin with
  the new `sable debug --write-plugin`, and becomes an `lldb-dap` (or
  CodeLLDB) configuration that imports the plugin.
- A grammar (raw and multi-line strings, interpolation, directives, doc
  comments), Sable blocks in Markdown, snippets (each checked to type-check),
  and a `sable.toml` schema for Even Better TOML.

`npm test` runs the grammar under `vscode-textmate` against 50 expectations
and over every `.sb` file in the repository, and checks that none ends inside
a construct. The language server, test-output parsing and the problem matcher
were checked against the real `sable`. Debugging was checked by driving Xcode's
`lldb-dap` over DAP with the configuration the extension produces: a
breakpoint in a package build is verified and hit, frames are named, and a
`String` local shows its text. Not checked: CodeLLDB, and the extension inside
VS Code's own test host.

Later (#206): `npm test` also runs the extension's own code under plain node
against a stand-in for the `vscode` module and the language client
(`test/vscode-mock.js`, no display and no VS Code download): activation
against the manifest, the server's command and settings, tasks, Run and
Debug, lenses, quick fixes, debug configurations (the lldb-dap one included;
CodeLLDB's only as far as being chosen), and the Test Explorer against a
`sable` that prints what the real one does. The server takes
`inlayHints.enable` and `lints.enable` as initialization options or
configuration, which the extension sets from `sable.inlayHints.enable` and
`sable.lints.enable`; `sable.path` and `${workspaceFolder}` are those of the
folder a file, task or test is in. Beyond VS Code, `editors/` has Vim and Neovim (syntax,
filetype, indentation, and `lsp/sable.lua` for Neovim 0.11), Helix
(`languages.toml`), Emacs (`sable-mode` with Eglot) and Sublime Text (the
TextMate grammar, generated from VS Code's), checked by
`editors/test/editors.test.js` — in Vim itself when it is installed, which
CI's runner has. Not yet: a tree-sitter grammar (no tree-sitter CLI here to
generate and test one), and so Zed and highlighting in Helix; Emacs is not
loaded in any test.

### Formatting, docs, explain, repl and lints, as implemented

- **`sable fmt [paths…] [--check]`** (and `sable fmt -` for a filter) formats
  in place with `SyntaxFormatter`, skipping `target/` and hidden directories,
  and refuses a file with syntax errors. The formatter is tested idempotent,
  tree- and token-preserving and comment-keeping on every `.sb` file in the
  repository and every `sable` block in `docs/` and `spec/`. Running it over
  all of them found and fixed: `else if` split across lines, a label split
  from its loop, `try?`/`as?` glued to the next token, `- 1` patterns, a `}`
  written into a line comment, inactive `#if` regions growing a blank line
  per pass and double-indented (and the lexer not seeing an indented
  directive in one), blank lines after comments lost, closure headers moved
  off the brace's line. Rules added: the author's line breaks inside
  expressions are kept, with one continuation indent (gofmt's rule, since
  there is no line width); a declaration's, an expression's or a guard's
  one-statement body written on one line stays there, and so do an
  `if`/`else` chain of such bodies, a closure with a header around one
  expression, and a type's body of members without bodies of their own
  (`struct P { var x: Int }`, `enum Dir { north, south }`, `;` between
  members); `operator == (`; a run of match arms whose `=>` the author lined
  up stays lined up (kept, as a line break is, not imposed). `lib/`,
  `examples/`, `tests/corpus/` and `tests/programs/` are formatted, and a
  test keeps them so; `tests/negative/` is not, since its expectation
  comments sit in a column of their own.
- **`sable explain <code>`** prints `docs/diagnostics/S####.md`, embedded in
  the driver; `--list` lists them. There is a page for every code, those a
  user rarely meets included (S1005 from the single-construct parsers, the
  builtins' S3082, S7001 and S7002, the binder's fallback S3099).
  `ExplainTests` compiles each page's example and requires exactly its code,
  and its fix to be clean; a few pages (S0001, S1002, S1009, S2003, S3038,
  S7000, and those five) explain without an example, since no short program
  isolates them. A report of diagnostics from `build`, `run`, `check`,
  `test`, `bench` and `debug` ends with a note naming `sable explain` and the
  codes in it that have a page, as rustc's does; `sable lint` points at it
  under each warning.
- **`sable doc [paths] [--std] [-p member] [--out dir] [--json] [--open]`**: HTML (index with a
  filter box, a page per module and per type, signatures rendered from the
  symbols with type names linked, `///` and `//!` comments as light Markdown)
  or a JSON symbol graph (symbols, `memberOf`/`conformsTo`/`extends` edges).
  Tested on the whole standard library: every public declaration appears and
  every link resolves. In a package, with no paths, it documents the
  package's library as a build compiles it — with its dependencies, not its
  `tests/` — into `target/doc` at the workspace root; `--open` opens it in
  `$BROWSER` or the system's browser. Not yet: doc tests, search beyond the
  index.
- **`sable repl`**: `ReplSession` (separate from the console) keeps imports,
  declarations, bindings and the entries that ran, under the SIL
  interpreter. An entry runs as a function of its own; each binding at its
  top level becomes a global of the session (its type written out), set up
  where the binding stood. The program is lowered with
  `SilGen.LowerForSession` — each global has its own initialiser, run when
  the global is first reached, and nothing destroys them at the end — and
  run with the heap and globals the earlier entries' programs left
  (`SilInterpreter.Session`), so nothing earlier is computed or done again.
  Earlier entries' functions are compiled with each new one, so a closure
  one made still has its code. A failed entry's bindings are forgotten; a
  binding that cannot be a global (its value needs a name bound inside the
  entry) lasts only for the entry. An expression is bound to `resN` and
  shown with its type; `:type`, `:reset`, `:help`, `:quit`. The interpreter
  runs without a step limit (Ctrl-C interrupts an entry) and 100,000 calls
  deep. On a terminal, `LineEditor` gives readline's editing keys, ↑/↓
  history (kept in `~/.sable/repl_history`) and Tab completion of keywords
  and the session's names. In a package the session is
  compiled with the package's library and its dependencies, so `import`
  takes their modules (`--no-package` for a session without).
- **Lints** (`src/Sable.Symbols/Lints/`): `Lint` over a file's
  `SemanticModel` and bound bodies, `LintConfiguration` (levels by name,
  `FromTable` for a `[lints]` table), `@allow(name)`/`@warn(name)` on any
  declaration, unknown names reported (S3399). Five lints, S3300-S3304:
  `unused_local`, `unused_parameter` (not for overrides or protocol
  requirements), `unused_import`, `var_never_mutated`, `shadowed_parameter`.
  Unreachable code is already the compiler's warning S6005. Entry point
  `Lints.Run(compilation, configuration)`. `sable check` runs them over the
  package's own files (not its dependencies'), at the levels of the
  manifest's `[lints]` table, once the package type-checks; a lint at
  `error` fails the check. `sable lint` does the same without the rest of
  the check, with levels from the command line over the manifest's; `sable
  lint <paths>` lints loose files with levels given on the command line.
  Later (#204): `unused_result` (S3308), and `shadowed_local` (S3390: a
  `let` in an inner block hiding one from a block around it; rebinding in
  the same block and `if let x = x` are not), `self_assignment` (S3391),
  `unused_private` (S3392: a private function or type named nowhere outside
  its own declaration, every file of the module searched), and
  `constant_arithmetic` (S3394: arithmetic on numbers written out that
  overflows, or division by a zero written out, in a body — a `const`'s is
  already the compiler's S3201). Deprecated use, unknown and misplaced
  attributes are the compiler's (S3095, S3092, S3093), and a `catch` whose
  `do` throws nothing has its body reported unreachable (S6005). A lint's
  report can carry a fix (`Diagnostic.Fixes`, text edits): prefixing `_`,
  renaming a parameter but keeping its label, removing an import or a
  self-assignment, `var` to `let`, `let _ =` before a dropped result.
  **`sable fix`** applies them to the package's own files (or to loose
  files, with `sable lint`'s options), compiling and linting again after
  each round until nothing is left, putting the files back if a round ever
  broke the program; `--check` says what it would do. The language server
  offers each lint's fix as its preferred quick fix. **Plugins**: a .NET
  assembly whose public `Lint` subclasses are lints, loaded beside the
  compiler by `[lints] plugins = ["path.dll"]` or `sable lint --plugin`;
  their lints report as S3397, take levels and `@allow` like built-in
  ones, and one that throws is reported (S3398) while the rest run. Not
  yet: plugins written in Sable (they would need the semantic model exposed
  to Sable code), and `_ = f()` (SYNTAX.md's discard) does not parse, so
  the fix writes `let _ =`.

### Debugging, as implemented

`sable build -g` (and `sable debug`) builds at `-O0` with the SIL passes off,
so every local stays in its slot (`src/Sable.CodeGen/LlvmEmitter.Debug.cs`).
With a level as well (`sable build -g -O2`, or a profile with `debug = true`
and an `opt-level` above 0) the build is optimised with debug information:
the opt-level's SIL passes run, the inliner giving what it copies the call's
place, and the debugger sees what they and LLVM leave — a frame LLVM inlined
is still named, with its arguments. `ProfileTests` holds the DWARF at
`-O1` to `-O3` and `-Os` to Apple's `dwarfdump --verify` (LLVM's
`llvm-dwarfdump` on Linux, when installed) and lldb to the frames.
Every function has a `DISubprogram` named by its full Sable name —
`Demo.Point.length`, `Demo.main$closure0` — with no linkage name: the symbol
is the same name behind `sable.`, nothing to demangle, and `b Demo.describe`
finds it (the plugin sets `target.language` to C, so that no lldb reads the
dots as Swift's or C++'s scopes and looks up only `describe`). A compiler-made
function (a protocol witness) is artificial, at line 0, and `step` goes
through it: its subprogram names its target (`DW_AT_trampoline`), it is in a
`<compiler-generated>` file, and with an LLVM before 20 — which writes a
line-0 function as one row, out of which lldb 18 steps straight back — its
prologue is given line 1 there, so the body's line 0 starts a row of its own.
The DWARF is version 5. Each nested block, and each loop, `if`, `switch` and
`do` (which bind names for their bodies), is a `DILexicalBlock`, so a local
shows only where it can be named and a shadowing one hides what it shadows;
a global is a `DIGlobalVariable` named by its last part (`target variable`,
`p limit`). Parameters are spilled to slots as clang
does, so they read correctly anywhere in the function; slots are made in
the prologue at the function's line, so a line breakpoint and `next` land on
statements, one line at a time; a local a closure captures by reference is
read through its box, and inside a closure each capture reads as a local of
its own name (the context's fields are now named for what they captured).
The runtime is compiled apart, optimised and without `-g`, so stepping goes
over it as over libc. `dwarfdump --verify` passes.

Every type is described as laid out. Sable's name is a typedef over a type
whose name says its kind, which the formatters match by regex: `String` →
`SableString *`; `[T]` → `[T]$list`, holding the buffer pointer (dictionaries
are `[(K, V)]`); a class → `C$ref`, holding a pointer to `C$object` (header,
then fields); a union → `U$union`, a `$tag` enumeration of the case names and
a `$payload` C union; `T?` → `T?$optional` (an optional class is the nullable
reference); a closure → `$closure` (`$function`, `$context`); `any P` →
`any P$existential`. An enum without payloads is a DWARF enumeration; a tuple
a struct with members `0`, `1`, …; `Char` is `char32_t`. Every class's type,
and every type the program has metadata of, is also a member of an unread
global, `sable.debug.types` (one in each unit), which keeps it past `dsymutil`
and lets the plugin find a dynamic type by the name in its metadata.

`tools/lldb/sable_lldb.py` (summaries and synthetic children, Python 3.9+):

```
(String) greeting = "hello, world"
([Int]) numbers = count = 3 { [0] = 10  [1] = 20  [2] = 30 }
(Values.Shape) rect = Rect(width: 2, height: 3)
(Int?) absent = none
(any Values.Named) bird = Values.Bird { [value] = Values.Bird { legs = 2  [strong] = 1 } }
(Values.Node) node = Values.Node { value = 7  label = "seven"  next = none  [strong] = 2 }
([String: Int]) ages = count = 2 { ["one"] = 1  ["two"] = 2 }
((Int) -> Int) times = Values.main$closure0
```

A class reference shows its dynamic class's fields and `[strong]`, its
reference count; an existential its dynamic type and `[value]`; a closure its
function, and its captures as children. Load it with `command script import
tools/lldb/sable_lldb.py` (or put that in `~/.lldbinit`). On macOS `sable
build -g` also copies it into the `.dSYM` (`Contents/Resources/Python`), which
lldb loads after `settings set target.load-script-from-symbol-file true`;
Linux has no such place — lldb does not read `.debug_gdb_scripts` — so it is
imported by hand there. `sable debug <file.sb> [--batch] [-o <command>]… [--
<arguments>]` builds with `-g` into a temporary directory and starts lldb
(`$LLDB`, else `lldb`) with the plugin loaded; in a package, `sable debug
[--bin name] [-p member]` builds the binary with the dev profile, as `sable
run` would, and debugs that.
`tests/Sable.CodeGen.Tests/DebuggerFormatterTests.cs` drives the real lldb;
it passes vacuously where lldb cannot trace a process — as in the emulated
x64 container on an arm64 Mac, where lldb 21 launches the program but it
stops at once with SIGILL. There only the DWARF check runs, with
`llvm-dwarfdump-21 --verify`; the formatters are verified on macOS.

A union case whose payload fields are named (`EnumType.PayloadLabels`) has a
payload described as a struct of them, `U.Case$case`, laid out as the tuple
is, which the plugin shows as the case's fields.

A generic type's instance is named as Sable writes it (`NominalType.DisplayName`):
`HashMap<String, Int>`, where its symbols and its metadata say
`HashMap$of$String$Int`; the plugin finds the class by the metadata's name
and shows the type's. A generic function's instance is too
(`SilFunction.DisplayName`): `Demo.describe<Int>`, `Sable.Collections.Stack<Int>.push`,
a protocol's default implementation under the type it is for — in a
backtrace, and for `b 'Demo.describe<Int>'` (the symbol is still
`sable.Demo.describe$of$Int`). An `@inline_layout` value is described as the
block holding its bytes (`S$inline`, over `$block`: header, length,
capacity, then its fixed-size fields packed as `InlineLayout` lays them
out), which the plugin shows as those fields; a pointer into the bytes is a
pointer to the fields. The standard library's files are in the compiler, not
on disk: their text is carried in the DWARF 5 line table
(`DW_LNCT_LLVM_source`, `Pipeline.EmbeddedSources`), so stepping into the
library shows its lines. An optimised build's unit says `isOptimized`.
For Windows the same nodes are written as CodeView (the
`"CodeView"` module flag), which lld gathers into the PDB beside the
executable, and which Windows' debuggers read (`AWindowsTargetIsDescribedInCodeView`
compiles a Windows object and finds `.debug$S` in it, no DWARF); the debugger
tests still drive lldb with the plugin's DWARF-shaped names, and are run on
macOS and Linux only.

*Values whose type only metadata knows* (#297 stage 9). In code compiled once
(`-generics=unspecialised`) a variable of a type laid out at run time — `T`,
`T?`, `(T, Int)`, `M.Pair<T, Int>`, a union over `T`, and an `inout` of one —
is described as `SableValue$dynamic` under the name it is written with: a slot
of two words, where the value is and its type's metadata, written where the
function starts (an async function's, where each of its parts does), so that
both are read at any place in it. The plugin reads the metadata and shows the
value as the program's own type of that name; one of a type made at run time,
which no DWARF describes, from the metadata's field descriptors, with an
enum's case told as the runtime lays it out (`lay_out_enum`). An array's
elements, a generic class's fields and what an existential holds are found
from the buffer's, the object's and the existential's own metadata. The
metadata parameters are artificial variables, `$metadata.T`, left out of
`frame variable` and a frame's arguments; the plugin names types and frames by
them:

```
frame #0: Gen.show<Gen.Point>(value=(x: 1, y: 2), list=count = 2, maybe=none) at gen.sb:58
(Gen.Point) value = { x = 1  y = 2 }
([Gen.Point]) list = count = 2 { [0] = (x = 3, y = 4)  [1] = (x = 5, y = 6) }
(Gen.Pair<Gen.Point, Int>) st = { first = (x = 1, y = 2)  second = 3 }
(any Rt.Zero) made = Rt.Crate<Rt.Meters> { [value] = { item = (value = 7, text = "long") } }
```

Frames are named through lldb's `frame-format`, whose `${function.name-with-args}`
the plugin replaces with a script of its own (an lldb of 17 or later; a format
without it is left alone), so in `bt` and where the program stops. A front end
that asks lldb for a function's name itself (lldb-dap, CodeLLDB) shows the
name the function was compiled with, `Gen.show<T>`, as does
`b 'Gen.show<T>'`, which stops for every type; so does a frame whose metadata
cannot be read. A value of an associated type only the registry gives, which
the function does not otherwise use, is `<type not known here>`.

Nothing the plugin reads is trusted: at a function's first instruction its
variables are whatever the stack held. Each record is checked before it is
used, each count, name and depth is bounded, and what does not hold up is
shown as a marker: `<uninitialised>`, `<invalid metadata at 0x10>`,
`<invalid list at 0x…: count = …, capacity = …>`, `<invalid object at 0x…>`,
`<invalid case 17>`. A slot's initial zeros have no line, so a breakpoint on a
function stops at its first statement, its parameters there to read. What was
read of a type is kept per process. Verified with macOS's lldb alone: not with
Linux's, whose older ones may not take a formatter's type name
(`get_type_name`) or have the frame's description (`GetDescriptionWithFormat`).

Not yet, for such values: the labels of a union case's fields, where the type
was made at run time (the metadata has none); an indirect capture seen from
outside the closure, which is shown as the one-element array that holds it;
and a Sable shared library's types, looked up in the executable alone.

Not yet: an `@inline_layout` value's variable-size fields (bounded and
grow-only arrays, whose offsets are only found by walking the bytes); the
library's text under CodeView, which has no place for it; expression
evaluation in Sable syntax (`expression` is lldb's C); a language code of
Sable's own (the unit says `DW_LANG_C`, which is what lets lldb evaluate
expressions at all); `inlinedAt` (a debug build inlines nothing, and an
optimised one's SIL inliner gives what it copies the call's place, while
LLVM's own inlining writes `inlinedAt` itself); `b Demo.describe` finding
every instance (`breakpoint set -r 'Demo\.describe<'` does); debugger tests
against a PDB on Windows; the plugin loaded without importing it on Linux,
where lldb reads no script from the binary; and gdb.

### Incremental builds, as implemented

In `src/Sable.Driver/Build/` (`IncrementalBuild`, `InterfaceFingerprint`),
`SilGen.LowerUnit` and `SilUnit.cs` in Sable.SIL, and `NativeObjects` in
Sable.CodeGen. `sable build` and `sable run` on a package go through it;
`--timings` prints each phase's time and what was compiled or reused.

- **Units.** A binary is compiled as units: its entry file, each package's
  library (its own first), and the standard library it imports (with the
  core). Each unit becomes one object file; the objects and a cached
  runtime object are linked by clang. The compilation is still the whole
  program's — every declaration of every unit, so names, signatures,
  layouts and vtables come out the same everywhere — but a unit's lowering
  (`SilGen.LowerUnit`) binds and lowers only its own files' bodies and
  declares the rest. What several units may need is made in each that
  needs it and linked `linkonce_odr`, so the linker keeps one: generic
  instances (lowered where they are used, like C++ templates), witness
  thunks, reflection functions, globals' storage, and class metadata, whose
  address is a class's identity to `is` and `as?`. The unit with the entry
  point sets up and tears down every global. This is the dev profile's
  path (an unoptimised debug build runs no SIL passes and no LTO, so
  nothing needs the whole program); an optimised build is compiled whole,
  with or without debug information, and only an unchanged one is skipped.
- **The cache** is `target/<profile>/cache/`. Every key is a SHA-256 over
  the toolchain — the compiler's assemblies (which embed the standard
  library), `clang --version` with `$CC`, the OS and architecture — the
  profile's settings (optimisation, debug directory, exclusivity), and
  content: each file's name, text and the features it is parsed with.
  Timestamps are never part of a key.
  - `builds/<binary>.txt`: the key over every input of the last build and
    the executable's size, time and hash. A build whose inputs hash to it,
    with the executable as it was left (size and time, else content), does
    nothing more: it reads and hashes the files and stops.
  - `objects/<key>.o` and `.abi`: one per unit, keyed by its own files'
    content and the **interface fingerprints of the units it depends on**
    (`UnitGraph`: what it imports or names in full, units sharing its
    modules, and so on; the entry unit depends on all), plus every unit's
    extensions'. The interface is a hash per file
    (`InterfaceFingerprint`) of its tokens with the bodies left out that
    only their own unit lowers: functions, initialisers, deinits, operators
    and getters with no type parameters, no `some` or `&` in the
    signature, no attribute and no `const`, at module level or in a non-generic struct,
    class, union, enum or actor, or in an extension of a type the program
    declares only as such. Generic bodies, protocol defaults, subscripts,
    setters, initial values and default arguments count as interface, since
    other units lower them; where such code is, its tokens' line and column
    count too (they reach debug information and the units that instantiate
    it), and elsewhere they do not, so a body growing by a line moves
    nothing. So a change to an ordinary body recompiles one unit; a change
    to an interface recompiles the units that depend on it; a change to an
    extension's interface recompiles every unit.
  - Not in `target/`: the standard library's unit — Sable.Core and the
    modules the program imports — as `std.o`, the archive of it,
    `libsable_std.a` (`sable_std.lib` on Windows), which the program
    links against, and its `.abi`; and the C runtime's object and archive
    (`libsable_runtime.a`). Both are entries of the shared build cache,
    `$SABLE_HOME/cache/<key>/` (docs/05 §5.4, "The build cache"), keyed by
    the compiler's own identity, the source, the target and the settings
    that change code, and made under a temporary name and renamed into
    place. The link takes the units' objects, then the library's archive,
    then the runtime's. A static library C links (`[lib] kind = "static"`)
    takes the objects instead, joined whole.
  - `target/resolution.txt`: the last resolution of a graph of local
    packages, keyed by every local manifest, the lock file and the
    request; while they are unchanged the resolver does not run. A graph
    with registry packages is resolved every time.
- **Checks on the way.** Units whose objects are cached are not
  type-checked again (they passed with the same inputs); the program's
  declaration-level diagnostics are always collected. A unit's root
  inference cannot see other units' summaries and assumes the worst of
  them, so it may reject what the whole program accepts: then — or if the
  units' records of each class's vtable and each struct's deinit (the
  `.abi` files) disagree, or the link fails — the program is compiled whole
  instead, with a note. A stale or inconsistent object never reaches an
  executable. (Today a class declared inside a function body that
  subclasses a class of another unit of its module which is not `open`
  always ends this way: the other unit does not bind that body, so it
  thinks the superclass has no subclasses. An `open` class is dispatched
  dynamically by every unit, whatever subclasses it sees. So does a
  static stored property of a type declared in a body.) Objects are written to a temporary name and renamed into
  place; past `max(256, 8 × units)` objects the least recently used go (an
  object's own time is left alone: lldb's debug map checks it).
- **Parallel.** Files are read, hashed and parsed in parallel; the
  program's declarations are bound while its interface is fingerprinted;
  units' IR goes to clang in parallel (one process per core); the
  toolchain's hash is worked out while packages resolve. Lowering and
  emission run one unit at a time on the one compilation, which is not
  thread-safe. `sable` uses the server GC (parsing on every core
  allocates fast).
- **Fixed on the way:** debug information gives every variable and member a
  type (clang 21 crashed on a closure's or a `String` field's without);
  `SilGen` named overloads by a scan per function (quadratic: 0.5 s on this
  program, per unit); `SemanticModel.GetAccessorDeclaration` gives a
  getter's symbol without binding its file's bodies; the resolver asked
  the package source for versions, and built every candidate list, at
  every step.
- **Proved** by `tests/Sable.CodeGen.Tests/IncrementalTests.cs`: after a
  private body change, a public signature change, a generic function's
  change, a global's initial value changing, adding and removing a file
  (removing one still used fails the build), pointing a dependency
  elsewhere, toggling a feature, switching profile, reverting an edit, an
  error and its fix, and corrupted cached objects, the program prints what
  a whole build of the same sources from nothing prints; unchanged builds
  are `fresh`; reverted states compile nothing. Interface fingerprints are
  tested directly, and programs from `examples/` and `benchmarks/` dealt
  into up to six units behave as when compiled whole. Before landing, every
  program the CodeGen tests build (227 of them) was also built split into
  one, three and eight units against the whole program: no difference.

**Measured** on an Apple M1 Max (10 cores, 64 GB), macOS, Apple clang 21,
.NET 11, `sable` built Release (JIT; other work running, load ≈ 8), on
`tools/incremental/genworkspace.py`'s workspace: 100 library packages in a
DAG (each depends on up to three earlier ones, always the previous) of
about 1,100 lines each — structs, unions, a class, a generic function,
closures, loops, string interpolation — and an app using all of them:
110,382 lines, 501 files (`genworkspace.py <dir>`, then `sable build
--timings` in `<dir>/app`). Wall-clock times of `sable build`, median of 3–5:

| Build (dev profile unless said)              | Before (whole program) | After        |
|----------------------------------------------|------------------------|--------------|
| Cold                                         | 11.6 s                 | 5.7 s        |
| No change                                    | 11.6 s                 | 0.12 s       |
| A function body in one package               | 11.6 s                 | **0.88 s**   |
| … in the package every other depends on      | 11.6 s                 | 0.86 s       |
| … in the app's `main`                        | 11.6 s                 | 0.78 s       |
| A new public function (interface change)     | 11.6 s                 | 5.8 s        |
| Release, cold / no change                    | 38 s / 38 s            | 34 s / 0.13 s|

With `sable` published ReadyToRun (`dotnet publish -c Release -r osx-arm64
-p:PublishReadyToRun=true`) a body edit takes 0.62 s. Where the 0.88 s goes
(`--timings`): resolution 12 ms (cached), reading and hashing 14 ms,
parsing 150 ms, interface fingerprints 130 ms alongside binding the
declarations 165 ms, checking the changed unit 100 ms, lowering it 115 ms,
verifying 26 ms, emitting 30 ms, clang 73 ms, linking 63 ms, and about
100 ms of process start. Before, a cold dev build was parse 1.2 s, bind
2.9 s, SIL 4.2 s, verify 1.0 s, emit 2.3 s and clang 9.3 s single-threaded
(under heavier load; 11.6 s with less).

**Deferred.** The query engine with red-green marking (docs/05 §5.4) is not
built: the cache-and-partition design meets the target, because a warm
build's remaining cost is the front end over the whole program's
declarations (parse, bind signatures, ~0.3 s here), not lowering or
codegen. What a query engine would add: invalidation by dependency rather
than "every unit" when an interface changes (today a signature change
anywhere is a ~6 s rebuild here); keeping parsed trees and bound
declarations in a long-lived process (a build daemon, or the LSP server)
so a warm build re-parses only what changed; and early cut-off when a
change's result is unchanged. (Since done: invalidation by dependency, then
by the declarations a unit may use — a new public function in the package
every other uses compiles 2 units of the 110k-line workspace rather than
101, 2.2 s rather than 13–14 s (#182 stage 9, docs/05 §5.4, "Dependencies
by name") — and a binary `.sbi` that units whose objects are kept are parsed and bound
from — docs/05 §5.4, "As built". Declarations are still bound from source
text, the interfaces', on each build; docs/05 §5.4 measures what symbols in
the `.sbi` would save (at most about 0.25 s of a 2.8 s body edit of
`examples/ssg`) and lists what reads a symbol's source and must move onto
the symbols first. Since done too (#181): an optimised
build compiles unit by unit, its opt-level's SIL passes on each unit and
ThinLTO across them — the release profile's default, `lto = "fat"` asking
for the whole program; a package's native library is built so, cached, as
a binary is; and constant evaluation binds only the files declaring a
constant or a `comptime` block, where it bound every body of the program —
the standard library's included — on every build.) Also deferred: escape and root summaries
across units (a unit assumes the worst, and the program is compiled whole
when that rejects it); lowering units in parallel; packages built once
for several binaries or members (the entry unit depends on every unit,
and each binary's units are keyed by the program it is in); a `.dSYM` for
separately compiled builds (lldb reads DWARF from the cached objects
through the debug map; `sable clean` removes them); `sable test` and
`sable bench` still compile whole.

### Source generators, as implemented

docs/08 (`tests/Sable.CodeGen.Tests/SourceGeneratorTests.cs`,
`tests/Sable.LanguageServer.Tests/GeneratorTests.cs`). The files under a
package's `generators/` directory are one program, compiled with
`Sable.Generators` (`lib/Sable.Generators`), a `main` the compiler writes,
and a public thunk per module that calls each `@generator` function
(`Generators/SourceGenerators.cs`). Before compiling, the program's
signatures, imports and effects are checked, using the manifest's effect
check. The compiled SIL is kept in memory, keyed by the program's text and
the toolchain.

Each top-level type that carries a generator's attribute becomes a request.
A request is lines of fields describing the declaration as written, which
the program reads from standard input in the interpreter. The program
prints each run's files and messages as records. A run is cached on the
generator program and the request's text, and the requests the cache does
not have go through one interpreter run. When a run traps, it is reported at
its attribute, and the rest go on from the next request.

`PackageGenerators` runs this for every package a target compiles, with the
generators of the package itself and of its direct dependencies (and its
dev-dependencies for `sable test`). It writes the output to
`target/generated/<package>/<generator>/` and removes files that are no
longer written. `PackageBuilder` puts each generated file right after the
file it was written for, and into that file's unit in an incremental build.
The language server runs a member's generators on its documents for each
analysis, compiles their output as dependency files, and shows their
messages where they point. Its dependencies' generators run too, over their
files as the editor has them. Each `generators/` directory is a project of
its own, checked against the rules as it is edited, and so is each snapshot
case under `generators/tests/`.

Before the requests are written, the package's files, its dependencies' and
what their generators wrote are bound without bodies (`GeneratorRequests`).
That resolves each field's, payload's, parameter's and return type into a
`TypeRef` line table, written arguments-first so the reader builds each type
from those before it. A type's conformances are every protocol it
`Satisfies`, which covers extensions. Locations are `declaration:part`
indexes into the spans the request kept, so `output.error(_, at:)` lands on a
field, a case or a parameter. A package generator (`fn (_: Package, into:)`)
gets one request with all its declarations and the manifest's
`[generators] inputs`. With `always: true` it runs with none, and its files go
beside the package's first file with a module, or into `inModule:`.
`sable test` runs `generators/tests/<case>/` first (`GeneratorSnapshots`): it
generates, compiles the case with the output, and compares both with
`expected/`. `--bless` rewrites `expected/`.

### Not built in Phase 5

Every command of docs/05 §5.1 exists (with `debug`, `lint`, `yank` and
`completions` beside them). `sable bindgen` binds what the compiler's half of
the foreign-function interface passes — integers, floats, `Bool` and structs
of them — and lists the rest; pointers and C strings wait for the library
half (`Sable.Interop`, #74). `sable toolchain` installs from a build, as
there are no released toolchains to download. Build scripts (§5.6) run in
the interpreter's sandbox; the network registry has a reference server, not
a deployment.

### Exit criteria
- [x] Cold build of a 100-package graph < 60 s; warm incremental < 1 s
  (5.7 s cold and 0.88 s after a function body edit, on the 110k-line
  graph above; an edit to an interface still rebuilds every unit, 5.8 s)
- [x] LSP responds to completion in < 50 ms at p95 on a 200k-line project —
      p95 5.2–9.4 ms on 205k lines, typing into function bodies while the
      server rebuilds in the background (`tools/Sable.LspBench`)
- [ ] A third party publishes a package without talking to the team — the
      tooling is there (`sable new`, `login`, `publish`, a signed network
      registry and its server, `explain`, `doc`), and a test runs the whole
      path against a local server; what is missing is a deployed registry
      and a third party

---

## Phase 6 — Self-hosting
**~6 months. Deliverable: the compiler compiles itself.**

### Strategy: port in dependency order, bottom-up

```
  Stage 0:  C# compiler (the host) compiles stdlib + runtime        [Phase 4]
  Stage 1:  C# compiler compiles the Sable-written lexer+parser
  Stage 2:  … symbols + binder
  Stage 3:  … SIL + passes
  Stage 4:  … codegen (LLVM C API bindings, written in Sable)
  Stage 5:  Sable compiler compiles itself → compiler₁
  Stage 6:  compiler₁ compiles itself → compiler₂
            assert compiler₁ output == compiler₂ output, byte for byte
```

**The fixed-point test in Stage 6 is the acceptance criterion.** It catches an
enormous class of miscompilation.

### Why bother

1. It is the most demanding test the language will ever get, and it finds
   ergonomic problems no toy program finds.
2. Contributors should not need .NET installed to hack on the compiler.
3. "The compiler is written in itself" is a credibility threshold for adoption,
   fairly or not.

### Keep the C# compiler alive

Maintain the C# implementation as a **differential-testing oracle** for at
least a year past self-hosting: compile the corpus with both, diff the SIL.
Discrepancies are bugs in one of them, and that is far cheaper than finding
them in production.

### Fuzzed from its first day

`sable fuzz` (docs/05 §5.1, #252) is the toolchain's own fuzzing, ready
before the port starts, so that each stage is fuzzed as it lands rather than
when the C# fuzzers (tools/fuzz, `CompilerFuzzTests`, `ResolverFuzzTests`)
retire. A `@fuzz` function takes the fuzzer's bytes (`U8[:]`); the program is
built as a library whose exports C calls once an input, with LLVM's
SanitizerCoverage, AddressSanitizer and UBSan, linked with libFuzzer; the
corpus and what fails (minimised, summed up by distinct failure) are kept
under `target/fuzz/<target>/`, and `--replay` runs them again with any clang.
The nightly workflow fuzzes the repository's `@fuzz` targets
(`tools/fuzz/stdlib.sb`, `fromBytes` in `tools/fuzz/inline_layout.sb`).
**Deferred:** `sable test` replaying every saved input; `@derive(Arbitrary)`
and a grammar-aware mutator built on the syntax tree; a dictionary from the
lexer's tables; differential targets (self-hosted against C#, the
interpreter against native code, `-O0` against `-O3`); and coverage from the
same counters for `sable test --coverage`.

### Exit criteria
- [ ] Stage 6 fixed point reached
- [ ] Self-hosted compiler within 1.5× the C# compiler's speed
- [ ] Both compilers agree on SIL for the entire corpus

---

## Phase 7 — Hardening to 1.0
**~4–6 months.**

- **Language specification freeze.** The documents in this repository become
  normative, with a conformance test suite.
- **ABI stability decision.** Recommendation: *do not* commit to a stable ABI
  at 1.0. Promise source stability and ship `@frozen` as an opt-in for the
  libraries that need it. Swift's ABI stability cost years and constrains it
  permanently; it is a 3.0 problem at the earliest.
- Error-message pass: every one of ~900 diagnostics reviewed for a worked
  example, a cause, and a fix.
- Security: fuzzing the compiler, the `fromBytes` validators, the parser, and
  the package resolver. Reproducible builds. Signed releases. A CVE process.
- Platform matrix: macOS arm64/x64, Linux arm64/x64 (glibc + musl),
  Windows x64, plus WASM as a stretch.
- Documentation: a book, an API reference, a migration guide for C#/Swift/Rust
  programmers, and a "why does the borrow checker reject this" cookbook.

---

## 4.8 Schedule summary

| Phase | Months | Cumulative | Risk |
|---|---|---|---|
| 0 Syntax | 3 | 3 | Low |
| 1 Semantics | 4.5 | 7.5 | Medium |
| 2 SIL + codegen | 4 | 11.5 | Medium |
| 3 Memory model | 5 | 16.5 | **High** |
| 4 Runtime + stdlib | 4.5 | 21 | Medium |
| 5 Toolchain | 3.5 | 24.5 | Low |
| 6 Self-hosting | 6 | 30.5 | Medium |
| 7 Hardening | 5 | 35.5 | Low |

Solo, multiply by roughly 2.5. The phases are not evenly parallelisable: Phase
0–2 are hard to split beyond two people; Phases 4–5 absorb as many as you have.

## 4.9 The five things most likely to kill this

1. **Root inference does not scale.** The analysis is the unvalidated part of
   the design. *Mitigation: prototype it against a toy IR during Phase 1. If it
   does not work, fall back to "ARC everywhere plus regions", which is still a
   good language.*
2. **Monomorphisation explodes compile times.** Rust's lesson. *Mitigation:
   layout-equivalence sharing and `@polymorphic` designed in at Phase 2, not
   bolted on at Phase 7.*
3. **The type checker becomes exponential.** Swift's lesson. *Mitigation: the
   step budget from the first commit, and a performance test per release.*
4. **Scope creep in the standard library.** *Mitigation: the Phase 4 list is a
   ceiling, not a floor. Everything else is a package.*
5. **Building it all before anyone uses it.** *Mitigation: the Phase 1 go/no-go
   where 10,000 lines of real Sable get written against a front end with no
   backend. Nothing finds design mistakes faster.*
