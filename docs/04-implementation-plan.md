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
│   └── corpus/                # .sab files that must parse/compile
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
- **Every test is a `.sab` file plus expected output.** Directive-driven, lit
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

- [ ] Every construct in `SYNTAX.md` parses
- [ ] Round-trip invariant holds on a 2,000-file corpus
- [ ] Incremental reparse of a 10,000-line file after a one-character edit
      completes in < 2 ms
- [ ] `sable fmt` is idempotent and reproduces the corpus modulo normalisation
- [x] A syntax-tree visualiser exists (Roslyn's Syntax Visualiser equivalent)
      — `tools/Sable.Visualiser`
- [ ] Fuzzer runs for 24h without a crash or an assertion failure

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
- [ ] **Go/no-go: is the language actually pleasant?** Write 10,000 lines of
      real Sable (a JSON parser, an HTTP client, a small game) against the type
      checker alone, before any backend exists. Fix the language now, not later.

---

## Phase 2 — SIL and the first native binary
**~4 months. Deliverable: `sable build hello.sab && ./hello`.**

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
- **SIL is textually printable and parseable.** Tests are `.sil` files. This is
  the difference between a debuggable compiler and a mystery.

### 2.2 Lowering (BoundTree → SIL)

Order matters; each of these is a self-contained lowering:

1. `defer` / `using` → explicit cleanup on every exit path
2. `match` → decision tree (Maranget's compilation, not a linear chain)
3. Closures → boxes + `partial_apply`; non-escaping closures stay on the stack
4. `async` → a state machine (coroutine split, see Phase 5)
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

### 2.4 Minimal runtime (C)

~3,000 lines: allocation, refcounting, metadata records, error propagation,
`abort`, and a `main` shim. See `06-runtime-and-stdlib.md`.

### 2.5 Exit criteria

- [x] Hello world links and runs on macOS arm64 and Linux x64 — and so does
      every native test, the JSON exit program included: on Linux x64 in a
      container (`tools/linux/test.sh`, Ubuntu clang 21), each builds, runs
      and agrees with the interpreter
- [x] A 2,000-line program (JSON parser + pretty printer) compiles and passes
      its tests — `tests/programs/json.sab`, 2,100 lines: strict RFC 8259
      parsing with line/column errors and opt-in comments, trailing commas,
      duplicate-key and depth checks; compact, pretty, sorted and ASCII-only
      printing; JSON Pointer (RFC 6901); Merge Patch (RFC 7386); comparison,
      transforms and statistics. Its own suite of 237 checks passes natively
      and in the interpreter, which agree, with nothing leaked
- [ ] SIL verifier is clean on the corpus — clean on everything that lowers,
      before and after the passes, ownership included (every owned value
      consumed exactly once on every path); 240 of the corpus's 345 function
      declarations lower so far — dictionaries, slices, existentials, class
      inheritance and computed properties now among them — the rest waiting
      on the integer types other than Int, recursive generic unions, mutating
      requirements through existentials, and async (Phase 5)
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
      a read once it is gone. Not yet: in structs, or in capture lists.
- [ ] 6 — deinit ordering (subclass first, fields last to first) and the
      immortal fast path are done; atomic counts wait for threads.

Measured by `tools/bench/run.py` (`benchmarks/history.tsv`): against naive
insertion, retains fall by 75% (binary trees), 92% (sort), 77% (shapes),
71% (JSON), 49% (words) and 100% (n-body), and time by 7–44%.

### 3.2 Move checking (~3 weeks)

Dataflow over SIL for `@noncopy` types: every value consumed exactly once on
every path, with the consuming site reported in the use-after-move diagnostic.
This is a straightforward dataflow problem and it is a good warm-up for 3.3.

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
    `result: fresh | anchors(S) | heap`) and serialise it into the `.sabi`.
   Summaries make the analysis modular — the whole point.
5. **Specialisation.** Where codegen differs by root shape (region vs heap),
   emit specialised bodies, keyed by the anchor signature.

Diagnostics must name the root, the escape site, and at least two concrete
fixes. A borrow checker with bad errors is a borrow checker nobody uses.

**Risk note:** budget 10 weeks, expect 16. This pass is where an ambitious
schedule goes to die, and it is also the only part of the design that has not
been validated at scale by someone else. Prototype it standalone, against a
toy IR, *before* Phase 3 starts — ideally during Phase 1 as a side project.

### 3.4 Exclusivity (~3 weeks)

Static checking where accesses are to provably distinct storage; dynamic
`begin_access`/`end_access` markers otherwise, elided in release builds.

### 3.5 Regions (~4 weeks)

- `region` as a value type with a bump allocator in the runtime.
- Region references get `region(r)` anchors and fall out of 3.3 for free —
  this is the payoff for doing root inference properly.
- `reset()`, nested regions, region-to-owned copy-out (`toOwned()`).
- Thread affinity by construction (a region anchor is not `Sendable`).

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

### 3.7 Escape analysis and promotion (~4 weeks)

The pass that makes idiomatic code fast: a `class` that does not escape its
frame becomes a stack value with no header and no refcount. Driven by the same
summaries as 3.3.

### 3.8 Exit criteria

- [x] A benchmark suite exists and is tracked per-commit — `benchmarks/`
      (binary trees, n-body, shapes, sort, words, and the JSON exit
      program), run by `tools/bench/run.py --record`, which appends time,
      allocations, retains and releases per commit to
      `benchmarks/history.tsv`
- [ ] ARC overhead on the suite < 5% vs a manually-managed equivalent
- [ ] Root inference rejects no program in the corpus that should compile
- [ ] `@inline_layout` reproduces the Goose order-record result: a nested
      record in **0 allocations**, within 20% of Goose's byte count
- [ ] Round-trip `Bytes.of` / `fromBytes` survives a 10M-case fuzzer with no
      memory-safety failure on corrupt input

---

## Phase 4 — Runtime, reflection, and the core library
**~4–5 months. Deliverable: a standard library you can write programs with.**

### 4.1 Runtime completion

Metadata records, protocol conformance registry, generic metadata caching,
existential boxing, dynamic casts, the error ABI, backtraces, the memory
allocator (a size-class allocator — mimalloc is the reference to copy or embed).

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

### 4.3 The core library (`Sable.Core`)

Order of construction:

```
Sable.Core         primitives, Optional, Outcome, String, Array family,
                   Comparable/Hashable/Equatable, iteration protocols
Sable.Collections  List, Dictionary, Set, Deque, OrderedDictionary, Heap
Sable.Text         unicode, formatting, parsing, regex
Sable.Math         fixed/float math, SIMD vectors, random
Sable.IO           File, Directory, Stream, Path, stdin/stdout
Sable.Serialize    Encoder/Decoder protocols, JSON, binary, the zero-copy path
Sable.Concurrency  Task, TaskGroup, Actor runtime, Channel, Mutex, Atomic
Sable.Net          Socket, TcpListener, Http client
Sable.Time         Instant, Duration, Clock, Calendar
Sable.Process      spawn, environment, signals
Sable.Diagnostics  Logger, tracing, metrics
Sable.Testing      the test harness `sable test` drives
```

**Write the standard library in Sable from day one of this phase.** It is the
first large program and the best source of language bugs.

### 4.4 Exit criteria

- [ ] Stdlib is self-hosted Sable, no C# in the shipping library
- [ ] Reflection: hello-world binary grows < 50 KB with reflection off
- [ ] Dictionary/List/String within 15% of their .NET equivalents on a
      standard benchmark set
- [ ] A real program ships: a static site generator or an HTTP service, in
      Sable, used daily by the team

---

## Phase 5 — Toolchain, package manager, IDE
**~3–4 months. Deliverable: a toolchain people can actually adopt.**

Full design in [05-toolchain.md](05-toolchain.md).

- `sable` single binary: `new init build run test bench fmt doc check add
  update publish clean bindgen repl`
- `sable.toml` manifest, `sable.lock`, semver resolution (PubGrub), workspaces,
  feature flags, build scripts with a capability sandbox
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

### Exit criteria
- [ ] Cold build of a 100-package graph < 60 s; warm incremental < 1 s
- [ ] LSP responds to completion in < 50 ms at p95 on a 200k-line project
- [ ] A third party publishes a package without talking to the team

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
