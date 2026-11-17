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
4. `async` → a state machine (coroutine split, see Phase 5). Interim, done:
   an async function lowers as an ordinary one, and each task runs on a
   stack of its own — see "Async, as implemented" under Phase 4
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
      consumed exactly once on every path); 284 of the 334 `fn` declarations
      in `tests/corpus` and `examples` lower so far — dictionaries, slices,
      existentials, class inheritance, computed properties and, since Phase
      3, the sized integers and inline layout among them — the rest waiting
      on recursive generic unions, mutating requirements through
      existentials, a few operators on enums, and async (Phase 5). Classes
      are complete as docs/03 describes them (`tests/.../ClassTests.cs`):
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
      a read once it is gone. Not yet: in structs, or in capture lists.
- [x] 6 — deinit ordering (subclass first, fields last to first), the
      immortal fast path, and a stack fast path (§3.7: an object made in
      its frame is counted as any other, and never freed). Atomic counts
      are deferred to the concurrency runtime (Phase 4.3, `Sable.Concurrency`):
      with one thread there is nothing for the flag bit of docs/02 §2.8 to
      choose between, and nothing to measure its cost against.

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
    `result: fresh | anchors(S) | heap`) and serialise it into the `.sabi`.
   Summaries make the analysis modular — the whole point.
5. **Specialisation.** Where codegen differs by root shape (region vs heap),
   emit specialised bodies, keyed by the anchor signature.

Diagnostics must name the root, the escape site, and at least two concrete
fixes. A borrow checker with bad errors is a borrow checker nobody uses.

Status: a first, working slice (`src/Sable.SIL/RootChecker.cs`). References
are coherent in the front end — `T&` reads as its target, a place becomes a
reference where one is wanted (`return a` from `-> ref T`, a `ref` argument,
each arm of `c ? a : b`), and a value with no place cannot (S3060) — and
lower to addresses. Anchors (1) are a local's slot, a parameter, or `heap`;
propagation (2) goes through field projections, ref variables, joins and
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
in arrays, summaries serialised into a `.sabi` (the `.sabi` itself is
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
Statically, each `inout` and `ref` argument, and a mutating method's
receiver, is a path from a variable through fields and elements (an
element is the whole collection); two that overlap, one of them a change,
are error S3062, naming both arguments and the fix — `bump(&p.a, &p.a)`,
`swap(&a[i], &a[j])`, `copy(&n, &n)`, `p.absorb(&p.a)` are rejected, and
`bump(&p.a, &p.b)` is not. Where the compiler cannot tell — storage inside
an object, reached through two references — the call is bracketed by
`access_begin_modify`/`access_begin_read` and `access_end`, and the
runtime traps on an access that conflicts with one in progress. The
markers are kept in debug and `-O0` builds and dropped from optimised
ones unless `--exclusivity=checked` keeps them (docs/02 §2.3); the
interpreter always checks. Deferred: instrumenting plain reads of object
fields during an access — only accesses passed to calls are tracked.

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
region ended is rejected, and `build(in r) -> ref Node` has `roots(0)`.
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
block passed by value, is rejected (a `ref` parameter's is fine); and a
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

Status: done, keeping the header and the count (`src/Sable.SIL/EscapeAnalysis.cs`).
A value escapes if it, or a copy, may be returned, thrown, stored anywhere
but a local slot, put inside another value, captured, held weakly, or
passed to a parameter that escapes or to a callee not known here; which
parameters escape is a per-function summary computed for the module to a
fixed point. A class's allocating initialiser is made where it is called,
so the caller owns the allocation; then `alloc_ref [stack]` and
`inline_new_stack` put what does not escape — and is not made in a loop —
in the frame. ARC is unchanged: the last release runs the deinit and the
fields' releases, in order, and frees nothing; an inline block that
outgrows its room in the frame moves to the heap. The JSON program loses
187 allocations; the order record of §3.8 loses all of them. Deferred:
dropping the header and the count for promoted objects whose count is
provably never read, and promotion inside loops.

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
      ports in `benchmarks/c` — same allocations, checks and output — are
      timed beside it. Closing that gap was code generation, not ARC:
      inout and ref parameters are LLVM `noalias` (exclusivity guarantees
      it), array headers and elements carry distinct TBAA tags, array
      count, element access and append are emitted inline, a loop that only
      reads an array and writes its elements makes it unique once before
      the loop (`HoistUniqueness`), string equality decides by identity and
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
      `benchmarks/orders.sab` builds a million of them with 2,000
      allocations, all in the thousand that go out as bytes and back
- [x] Round-trip `Bytes.of` / `fromBytes` survives a 10M-case fuzzer with no
      memory-safety failure on corrupt input — `tools/fuzz/run.sh` builds
      `tools/fuzz/inline_layout.sab` with AddressSanitizer and
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

As implemented so far: with `SABLE_BACKTRACE` set, a trap prints a backtrace
after its message, one frame per line, named through `dladdr` (on Linux the
program links with `-rdynamic` so its functions can be named). Each frame is
looked up one byte before its return address, because a call that never
returns leaves that address past the end of its caller. Under LTO most of a
small program is inlined into `main`, so expect few frames.

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

A first slice, in `lib/Sable.Core/Reflection.sab` over `Reflect`, a struct
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

### The library, as begun

The library is Sable source in `lib/<Module>/*.sab`, embedded in the driver
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
  `escapedHtml`.
- `Sable.Collections`: `Stack`, `Queue`, `HashMap` and `HashSet` (open
  addressing over `Hashable`, iterating in insertion order), `Deque` (a ring
  buffer of optional slots, so a removed element is released at once),
  `PriorityQueue` (a binary heap, smallest first or by a stored ordering
  closure), `OrderedDictionary` (keys and values in arrays, positions in a
  `HashMap`; removal closes the order up), `BitSet` (64 to a word, with
  union, intersection, subtraction and subset), and a stable merge sort —
  `sort(&values)` in place, `sorted(values)` a copy, either by `<` or by a
  predicate (`tests/Sable.CodeGen.Tests/CollectionLibraryTests.cs`).
- `Sable.IO`: `File` (read, readLines, write, append, exists, remove),
  `Directory` (exists, create, list — sorted byte by byte so every platform
  agrees — and remove), `Path` (join, fileName, directory, extension,
  withoutExtension), `Console` (the standard streams, `readLine`,
  `readLines`) and `Environment` (variables, arguments), failing with
  `IoError`. It is written over `Runtime`, a `Sable.Core` struct of static
  `@builtin` calls (`lib/Sable.Core/Runtime.sab`, see "Sable.Core in Sable"
  below) that the runtime implements in C (`sable_io_*`) and the interpreter
  in .NET; a call that may fail gives a value either way and `io_succeeded`
  says whether to keep it, which `Runtime`'s Sable wrappers turn into an
  optional. `main` now takes `argc`/`argv`. The test harness gives programs
  standard input and compares standard error between the two.
- `Sable.Time`: `Duration` (to the nanosecond, Comparable and Hashable,
  printed in its largest whole unit), `Instant` on the monotonic clock,
  `Clock.sleep` and `Clock.measure`, and `DateTime` in UTC — fields,
  ISO 8601 weekday and day of the year, `iso8601()`, `parse`, arithmetic —
  over proleptic Gregorian 400-year eras, so dates before 1970 work. Over
  three more runtime calls (`time_monotonic`, `time_wall_clock`,
  `time_sleep`). Not yet: time zones, and a sleep that lets other tasks run
  (it holds the scheduler's baton).
- `Sable.Process`: `Process.run` runs a program found on PATH to its end,
  its input written and its output and errors read together through
  `poll` (so neither side blocks on a full pipe), giving `Completed`
  (status — 128 plus the signal when one ended it — output, errors);
  `ProcessError.NotFound` when nothing by that name starts. Also the
  current directory and the process id. Not yet: pipelines, streaming a
  running child's output, signals, and `exit`.
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
- `Sable.Concurrency`, written in Sable over the core's `Task`: `Channel`
  (unbounded, closable, `receive` awaits), `TaskGroup` (results in the order
  the tasks were added), and a `sleep` that yields so that other tasks run
  meanwhile. Tasks switch only where they await, so none of it needs a lock.
  Not yet: bounded channels, cancellation, `Mutex` and `Atomic` (useless
  until tasks run in parallel), and deadlock detection: a task waiting on
  nothing yields for ever.

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
`lib/Sable.Core` — `String.sab`, `Char.sab`, `Numbers.sab` and
`Runtime.sab`, beside `Protocols.sab` and `Reflection.sab`. At the floor is
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
this was measured on) and every benchmark in `benchmarks` and
`benchmarks/versus-dotnet` build to the same number of bytes, and run at the
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

To make this possible `Sable.Core` is now part of every compilation —
embedded in `Sable.Symbols` and appended to whatever trees a compilation is
given (`CoreLibrary`) — rather than something the driver adds, so a test
binding three lines sees the same `String` a program does. Deleted from C#:
`CoreTypes`' declarations of those members and of `Runtime` and `print`,
and `SilGen`'s `CoreMethod`, `RuntimeCall`, the string and character cases
of `ComputedProperty`, the `print` special case and `MakeOptional`.

### The first real program

`examples/ssg/ssg.sab` is a static site generator, some 640 lines of Sable: it
reads a directory of Markdown pages with front matter and a template,
renders a pragmatic Markdown subset (headings with unique ids, paragraphs,
emphasis, code, links and images, nested lists, quotes, rules, fenced code)
and writes a page each, an index newest first, a page of tags, and the
static files; `sable run examples/ssg/ssg.sab -- <site> <out>` (`run` now
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
members (`==`, `<`, `hash()`, `debugDescription()`) as symbols on the type and
the protocols to its base list, and builds their bodies as bound trees from
the fields (`Compilation.Derive.cs`, `DerivedBodies.cs`), which SIL lowering
takes like any other body. `==` is field-wise, `<` lexicographic in declaration
order, `hash()` folds the fields' hashes (kept below 10^9+7, so it cannot
overflow), and a union compares case by case — by case first, in declaration
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
would contradict it. Not yet: `Copy` and `Serializable` (the latter waits for
reflection), optional and array fields (neither conforms to the protocols
yet), and a directly recursive union, which SIL lowering does not lay out.

### Against .NET

The exit criterion below, measured: `benchmarks/versus-dotnet/*.sab` and
their C# ports in `benchmarks/dotnet` (Release, tiered PGO), run by
`tools/bench/versus_dotnet.py`, which checks the two print the same line.
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
| list       |   121.4 |    138.1 |  1.14 |
| map-int    |   130.9 |    145.9 |  1.11 |
| map-string |    48.4 |     52.7 |  1.09 |
| sort       |   108.8 |    137.2 |  1.26 |
| string     |    53.2 |     52.6 |  0.99 |

The .NET side varies by up to 15% from run to run on `string` and
`map-string` (46–60 ms); the Sable side by a few percent. Before this work
`sort` was 4.6 times .NET (a merge sort that made two arrays a level),
`string` 2.7 times, and `map-string` trapped: the Sable-written string hash
overflowed past a dozen bytes. What closed the gap, by cause:

- **Library.** `HashMap` is one array of entries (key, value, and the 31-bit
  tag that grows the table without rehashing) under a power-of-two table of
  index-and-tag slots, mixed with a xorshift since Sable has no wrapping
  multiply; the sort is bottom-up over one scratch array.
- **Runtime.** A size-class allocator for objects and element buffers up to
  256 bytes (malloc and free were most of the string benchmark), and the
  large buffer freed last kept for the next (the system otherwise maps and
  zeroes it afresh); string hashing and comparison in C
  (`Runtime.hashString`, `Runtime.compareStrings`); integers written without
  `snprintf`; `split` on an ASCII separator by `memchr` into an array made at
  its size; strings not zeroed before they are written, nor their scalars
  counted when already known.
- **Compiler.** Interpolations and chains of string `+` are one allocation
  (`string_concat_many`); `removeLast` has an inline fast path, as `append`
  had; an array element read for a call is borrowed even where the array's
  own borrow ended first, which takes the retains and releases off a sort's
  comparisons and a `HashMap` probe's; a `for` over an array lets its copy
  go as the loop ends. String `<` and its derived operators now bind to
  Sable.Core's operator — there was no builtin to lower them to.

What is left: `sort` is a stable merge sort against .NET's unstable
introsort, and pays a bounds check and a uniqueness check on every move;
`list` is mostly the operating system faulting in fresh pages, which .NET's
warmed-up heap already has; a `HashMap<String, _>` hit still copies the key
across the call to `==`, which SIL cannot yet inline. `Sable.Text`'s
`joined` (and so `TextBuilder.build`) was quadratic, `result = result + part`;
it is now one runtime call (`Runtime.joinStrings`) that sizes the result once.

### Async, as implemented

Done ahead of `Sable.Concurrency`, with the core of it in `Sable.Core`
(`tests/Sable.CodeGen.Tests/AsyncTests.cs`). Tasks are scheduled
cooperatively: each runs on a thread of its own, but one lock is held by
whichever task is running, so exactly one runs at a time and switches only
where a task waits — awaiting a task not yet finished, or `Task.yield()`. The
interleaving is therefore deterministic, the interpreter reproduces it, and
reference counts stay non-atomic. An async function is an ordinary function,
and `await` is its operand; `Task.spawn { … }` queues a task (a class holding
the runtime's handle, the body and the result), `await task.value` waits for
it, and the program ends once every task has. Actors lower as classes: with
one task running at a time a call into one runs alone, and the binder makes
a call from outside it `await`. `async let` binds; reading it needs `await`.
A class or actor whose fields all have initial values is made with `init()`.

This trades the planned stackless state machine (§2.2, item 4) for a stack
per task — far less compiler for the same semantics, at the cost of memory
per task and of thread creation. Not yet: `async let` runs its child to
completion at the declaration rather than alongside; no task groups,
cancellation, `for await` or async sequences, no sleeping or I/O to wait
on, and no parallelism — all `Sable.Concurrency`'s, along with the switch
to the state machine where tasks are many.

### 4.4 Exit criteria

- [ ] Stdlib is self-hosted Sable, no C# in the shipping library — every
      module is Sable, and so are the primitives' members: `String`,
      `Char`, the numbers, `Bool`, `print`, `Runtime` and `Region.reset`
      are declared in `lib/Sable.Core`, over `@builtin`s (see "Sable.Core
      in Sable"). The primitive types themselves stay compiler-known, as
      the plan allows. What is still declared in C# (`CoreTypes`), and why:
      the array family's members (`count`, `append`, `slice`, …), built per
      element type, since a declaration cannot yet range over every array
      type; `Region.alloc`, generic and giving a reference into the region;
      `Task` (`spawn` builds the task object around a closure, `value` is an
      async getter); `Bytes.of`, `fromBytes` and `Decoded`, inline layout's,
      synthesised per type; and `Range`'s bounds, declared beside the type
      the binder makes of `a..<b` (and not yet lowered). The I128, U128 and F16 types no
      longer declare `toString`, `abs`, `min` and `max`: nothing could lower
      them, and SIL has no such types yet
- [x] Reflection: hello-world binary grows < 50 KB with reflection off —
      it grows not at all (51,736 bytes either way): reflection is code
      synthesised per type a program reflects on, and nothing else
- [ ] Dictionary/List/String within 15% of their .NET equivalents on a
      standard benchmark set — Dictionary (`map-int` 1.11, `map-string`
      1.09), List (`list` 1.14) and String (`string` 0.99) are met, by a
      small margin for `list`; sorting (`sort` 1.26) is not (see "Against
      .NET" above)
- [ ] A real program ships: a static site generator or an HTTP service, in
      Sable, used daily by the team — written (`examples/ssg`, see "The
      first real program"); daily use is still to come

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
