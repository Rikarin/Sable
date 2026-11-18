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

- [ ] Every construct in `SYNTAX.md` parses
- [ ] Round-trip invariant holds on a 2,000-file corpus
- [x] Incremental reparse of a 10,000-line file after a one-character edit
      completes in < 2 ms — about 1 ms in Release (0.9 ms at best, 1.2-1.5 ms
      median under heavy load), against 20-35 ms for a full parse
- [x] `sable fmt` is idempotent and reproduces the corpus modulo normalisation
      — `FormatterRepositoryTests` holds `lib/`, `examples/`, `tests/corpus/`
      and `tests/programs/` to being formatted already
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
    `result: fresh | anchors(S) | heap`) and serialise it into the `.sbi`.
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
      `benchmarks/orders.sb` builds a million of them with 2,000
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

A first slice, in `lib/Sable.Core/Reflection.sb` over `Reflect`, a struct
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
  `@builtin` calls (`lib/Sable.Core/Runtime.sb`, see "Sable.Core in Sable"
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
`lib/Sable.Core` — `String.sb`, `Char.sb`, `Numbers.sb` and
`Runtime.sb`, beside `Protocols.sb` and `Reflection.sb`. At the floor is
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
every benchmark in `benchmarks` and `benchmarks/versus-dotnet` compiles to
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

The exit criterion below, measured: `benchmarks/versus-dotnet/*.sb` and
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
      makes of `a..<b` and `a...b` — are Sable (`lib/Sable.Core/Range.sb`:
      `lowerBound`, `upperBound`, `isEmpty`, `contains`, over a `Comparable`
      bound), and lower as values: stored, passed, returned and iterated,
      while a `for` over a range written in the loop still builds none.
      Nothing else is declared in C# (`CoreTypes`): only the types the
      compiler knows by sight, and the shells the binder desugars to
      (`Error`, `From`, `Optional`, `Dictionary`). The I128, U128 and F16 types no
      longer declare `toString`, `abs`, `min` and `max`: nothing could lower
      them, and SIL has no such types yet. The built-in `[Key: Value]`
      dictionary is compiler-known as the arrays are — its entries an array
      of key and value pairs, its indexing and `for (key, value) in d` lowered
      directly — and its members are Sable (`lib/Sable.Core/Dictionary.sb`:
      `count`, `isEmpty`, `contains`, `remove`, `removeAll`, `keys`,
      `values`), `count` and `removeAll` `@builtin`s on the entries. Its key
      search is still linear; `Sable.Collections.HashMap` is the one that
      hashes
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
- **Proved on the library:** `tests/lib/CollectionsTests.sb` and
  `tests/lib/TextTests.sb` test Sable.Collections and Sable.Text with
  `@test`/`@bench`, and the test suite runs them.
- **Deferred:** a `blackBox` to keep the optimiser from deleting a
  benchmark's work (it needs a core builtin); async tests; a parallel test
  runner; timeouts for a hung test; printing the error a throwing test threw;
  `#[should_panic]`-style expected traps; snapshot and property testing
  (docs/06 lists them under Sable.Testing).

### Packages, as implemented

In `src/Sable.Driver/Packages/`, with a command per file under `Commands/`.

- **Manifest.** A TOML 1.0 reader (tables, arrays of tables, inline tables,
  dotted and quoted keys, every string and integer form, floats, booleans,
  arrays, comments; dates kept as text) whose errors carry file, line and
  column. `sable.toml` (written by hand, or what a `package.sb` evaluates
  to) is read into a `Manifest` — `[package]`, `[lib]`,
  `[[bin]]`, the three dependency tables, `[features]`, `[profile.dev]` and
  `[profile.release]`, `[workspace]`, `[build]`, `[registry] path` — and
  every validation error names the file, the line and the key. Profiles act
  on `opt-level` (clang's `-O0` to `-O3`, `-Os`, `-Oz`), `debug` (at any
  level: an optimised build with debug information runs the SIL passes and
  keeps the entry point out of the C `main`, so the program's functions keep
  theirs), `lto` (`fat`, `thin` or off; on Linux through lld, used when clang
  can link with it), `codegen-units = 1` (compile whole even in an
  unoptimised debug build), `strip`, `rpath`, `exclusivity` and `leak-check`
  (`sable run` sets `SABLE_CHECK_LEAKS`). A build prints the warnings of the
  profile it uses: `panic = "unwind"` (a trap always aborts), `arc-checks`
  (there are no optional ARC checks) and `overflow-checks = false`
  (arithmetic is always checked) have no effect.
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

Deferred: build scripts (`[build]` is read and validated, and a build warns
that it is not run — the capability sandbox needs runtime support), and with
them `[build-dependencies]` (checked, not resolved or built, and warned
about); git dependencies (refused with an error); workspace inheritance
(`version.workspace = true` and `{ workspace = true }` are refused,
`[workspace.package]` and `[workspace.dependencies]` warned about); `[reflect]`
and `[targets]` (accepted with a warning: nothing looks a type up by name
yet, and there is no cross-compilation). A dependency's `package =` and
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

- Diagnostics for open documents, debounced (250 ms of quiet), bound member by
  member. Declaration and signature diagnostics carry no file
  (`GetAllDiagnosticsByFile` says so); they are placed by span and the name
  their message quotes.
- Hover (signature, container, doc comment; an expression's type), definition
  (into a copy of the library file when it lands there), references, rename
  with `prepareRename` (refused for anything the library or the compiler
  declares, and for keywords), document symbols, signature help.
- Completion: members after `.` and `?.` (statics after a type name), the
  names in scope — locals declared before the caret, parameters, the enclosing
  types' members, the module, imports, `Sable.Core` — and keywords; module
  names after `import`.
- Inlay hints: the type of each `let`/`var` written without one, and the
  inferred roots — after a function's reference result, `roots(a, b)`, the
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
have — one being written from scratch — waits for a new compilation; hover,
references and rename always use one of exactly the current text.
`tools/Sable.LspBench` writes a 205,180-line project (180 files, nine modules
importing one another), starts `sable lsp` as a process, and types statements
into function bodies a key at a time (40–120 ms apart), asking for completion
after each and pausing so the server rebuilds in the background (two dozen rebuilds
per run), timed at the client. Release build, macOS arm64, four runs of 831
completions: p50 1.0–1.7 ms, **p95 5.2–9.4 ms**, p99 10–24 ms, max 15–34 ms;
after a `.` alone, p95 2.7–12.6 ms. For contrast, a hover just after an edit,
which waits for a compilation of the whole project, takes 125–170 ms, and the
first diagnostics come 1.3–1.5 s after start (reading and parsing 205k lines
included).

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
- **`sable doc [paths] [--std] [--out dir] [--json]`**: HTML (index with a
  filter box, a page per module and per type, signatures rendered from the
  symbols with type names linked, `///` and `//!` comments as light Markdown)
  or a JSON symbol graph (symbols, `memberOf`/`conformsTo`/`extends` edges).
  Tested on the whole standard library: every public declaration appears and
  every link resolves. Not yet: `--open`, doc tests, search beyond the index.
- **`sable repl`**: `ReplSession` (separate from the console) keeps imports,
  declarations and the statements that ran, and runs each entry as the next
  statement of a `main` that replays them, under the SIL interpreter; an
  expression is bound to `resN` and shown with its type; `:type`, `:reset`,
  `:help`, `:quit`. Replay re-runs side effects (I/O, clocks); a smarter
  scheme would snapshot the interpreter's state instead.
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
  `error` fails the check. `sable lint <paths>` lints loose files with levels
  given on the command line. Plugin loading of third-party lints is not
  built.

### Debugging, as implemented

`sable build -g` (and `sable debug`) builds at `-O0` with the SIL passes off,
so every local stays in its slot (`src/Sable.CodeGen/LlvmEmitter.Debug.cs`).
Every function has a `DISubprogram` named by its full Sable name —
`Demo.Point.length`, `Demo.main$closure0` — with no linkage name: the symbol
is the same name behind `sable.`, nothing to demangle, and `b Demo.describe`
finds it. A compiler-made function (a protocol witness) is artificial, at
line 0, and `step` goes through it. Parameters are spilled to slots as clang
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
a struct with members `0`, `1`, …; `Char` is `char32_t`. Every class's type is
also a member of an unread global, `sable.debug.classes`, which keeps it past
`dsymutil` and lets the plugin find a dynamic type by the name in an
object's metadata.

`tools/lldb/sable_lldb.py` (summaries and synthetic children, Python 3.9+):

```
(String) greeting = "hello, world"
([Int]) numbers = count = 3 { [0] = 10  [1] = 20  [2] = 30 }
(Values.Shape) rect = Rect(2, 3)
(Int?) absent = none
(any Values.Named) bird = Values.Bird { [value] = Values.Bird { legs = 2  [strong] = 1 } }
(Values.Node) node = Values.Node { value = 7  label = "seven"  next = none  [strong] = 2 }
(Sable.Collections.HashMap$of$String$Int) map = … count = 2 { ["one"] = 1  ["two"] = 2 }
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
(`$LLDB`, else `lldb`) with the plugin loaded.
`tests/Sable.CodeGen.Tests/DebuggerFormatterTests.cs` drives the real lldb;
it passes vacuously where lldb cannot trace a process — as in the emulated
x64 container on an arm64 Mac, where lldb 21 launches the program but it
stops at once with SIGILL. There only the DWARF check runs, with
`llvm-dwarfdump-21 --verify`; the formatters are verified on macOS.

Not yet: lexical scopes (every local is in its function's scope, so a
shadowed name shows twice); union payload field names (SIL keeps a tuple, so
`Rect(2, 3)` not `Rect(width: 2, height: 3)`); readable names for generic
instances (`HashMap$of$String$Int`); `@inline_layout` values, which show as a
pointer to their bytes; the standard library's own source lines (its
functions name a file the debugger cannot find); expression evaluation in
Sable syntax (`expression` is lldb's C); and gdb.

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
    content and the **program's interface fingerprint**. The interface is
    not a binary `.sbi` file (docs/05 §5.4) but a hash per file
    (`InterfaceFingerprint`) of its tokens with the bodies left out that
    only their own unit lowers: functions, initialisers, deinits, operators
    and getters with no type parameters, no `some` or `ref` in the
    signature and no attribute, at module level or in a non-generic struct,
    class, union, enum or actor, or in an extension of a type the program
    declares only as such. Generic bodies, protocol defaults, subscripts,
    setters, initial values and default arguments count as interface, since
    other units lower them; where such code is, its tokens' line and column
    count too (they reach debug information and the units that instantiate
    it), and elsewhere they do not, so a body growing by a line moves
    nothing. So a change to an ordinary body recompiles one unit; anything
    else recompiles every unit of the program (finer, dependents-only
    invalidation is the query engine's job — below).
  - `objects/runtime-<key>.o`: the C runtime, compiled once per settings.
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
  subclasses another unit's class always ends this way: the other unit
  does not bind that body, so it thinks the superclass has no subclasses.
  So does a static stored property of a type declared in a body.) Objects are written to a temporary name and renamed into
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
change's result is unchanged. Also deferred: a binary `.sbi` (the
interface is a fingerprint, and each unit still binds every file's
declarations from source); separate compilation for optimised builds
(they need the whole program for the SIL passes and LTO — ThinLTO over
units would parallelise their 26 s of clang); escape and root summaries
across units (a unit assumes the worst, and the program is compiled whole
when that rejects it); lowering units in parallel; packages built once
for several binaries or members (a unit's key includes the whole
program's interface, so each binary has its own objects); a `.dSYM` for
separately compiled builds (lldb reads DWARF from the cached objects
through the debug map; `sable clean` removes them); `sable test` and
`sable bench` still compile whole.

### Not built in Phase 5

Of docs/05 §5.1's commands, all but two exist (with `debug`, `lint` and
`yank` beside them). `sable bindgen` waits for the library half of the foreign-function
interface (`Sable.Interop`'s C strings and pointers, #74) — the compiler's
half, `@extern`, `@export` and C's calling convention, is built; `sable toolchain` waits for released
toolchains to manage. Build scripts (§5.6) are read but not run, and the
registry is a directory, not a network service.

### Exit criteria
- [x] Cold build of a 100-package graph < 60 s; warm incremental < 1 s
  (5.7 s cold and 0.88 s after a function body edit, on the 110k-line
  graph above; an edit to an interface still rebuilds every unit, 5.8 s)
- [x] LSP responds to completion in < 50 ms at p95 on a 200k-line project —
      p95 5.2–9.4 ms on 205k lines, typing into function bodies while the
      server rebuilds in the background (`tools/Sable.LspBench`)
- [ ] A third party publishes a package without talking to the team — the
      tooling is there (`sable new`, `publish`, a signed registry, `explain`,
      `doc`); what is missing is a network registry and a third party

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
