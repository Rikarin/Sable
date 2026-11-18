# 1. Overview and Design Pillars

## 1.1 What this language is for

Sable targets the band that is currently underserved: **applications that want
C#'s expressiveness and library ergonomics, but ship as a native binary with
predictable latency.** Games, creative tools, audio and simulation software,
CLI tooling, embedded Linux services, and anything where a 200 ms GC pause or a
150 MB runtime is disqualifying.

It is explicitly *not* trying to be:

- A kernel/firmware language. There is a runtime. It is small, but it exists.
- A Rust replacement. Rust's affine-by-default model buys guarantees Sable
  trades away in exchange for ergonomics.
- A scripting language. No interpreter in the shipping story (though the
  compiler hosts one for `const` evaluation and the REPL).

## 1.2 The six pillars

### Pillar 1 — Protocol-oriented, not inheritance-oriented

Classes form single-inheritance chains if you want them, but the intended unit
of abstraction is the **protocol**: an interface with associated types, default
implementations, generic constraints, and retroactive conformance via
`extension`. This is Swift's model, with C#'s explicit-implementation escape
hatch for name collisions.

Protocols are used in two modes and the distinction is visible in the syntax:

- `some P` — an opaque type, monomorphised, zero cost, resolved statically.
- `any P` — an existential, boxed, dynamically dispatched through a witness table.

Making the boxing visible at the use site is the single highest-leverage
decision in the language. Swift added `any` late and it was worth it.

### Pillar 2 — Closed unions with exhaustive matching

Two distinct features share the word "union" in most languages. Sable keeps
them separate:

- **Nominal unions** (`union Shape { Circle(r: F64), Rect(w: F64, h: F64) }`) —
  tagged algebraic data types. Closed, exhaustively matched, payload-carrying.
  This is Rust's `enum` and Swift's `enum` with associated values.
- **Structural unions** (`Int | String | None`) — anonymous sum types in the
  TypeScript style, usable in signatures without declaring a type. These are
  compiled to a nominal union under the hood, with flow-sensitive narrowing via
  `is` tests. Restricted to a bounded set of cases (no open-world subtyping).

Pattern matching is a first-class statement *and* expression, with binding
patterns, tuple patterns, record patterns, range patterns, `where` guards, and
compile-time exhaustiveness checking that reports the missing cases by name.

### Pillar 3 — A tiered memory model, with ARC as the default

This is where the design earns its keep, and where your four requirements
(ARC, borrow checking, Goose's layout model, runtime reflection) genuinely
conflict. The resolution is in [02-memory-model.md](02-memory-model.md); the
short version:

| Tier | Declared as | Cost | Reflection |
|---|---|---|---|
| 0 | `struct` | Inline, contiguous, no pointers, no refcount | Static only |
| 1 | `region` / arena-bound `class` | Bump-allocated, freed in one store at scope exit | Static only |
| 2 | `class` | Heap, ARC, weak/unowned | Full runtime reflection |

Tier 0 is the default for data. Tier 2 is the default for *identity* — things
with lifecycle, observers, or polymorphic identity. The compiler's escape
analysis promotes Tier 2 to Tier 1 and Tier 1 to Tier 0 wherever it can prove
it is safe, so idiomatic code drifts down the table without being rewritten.

### Pillar 4 — Borrow checking as an *optimisation enabler*, not a wall

Full Rust-style borrow checking is incompatible with "feels like C#". But the
useful 80% is cheap:

- **Exclusivity on `inout` parameters** (Swift's law of exclusivity, enforced
  statically where provable and dynamically where not).
- **Move-only types** (`@noncopy struct`) for resource handles — files, sockets,
  GPU buffers, locks. Use-after-move is a compile error.
- **Borrowed parameters** (`ref` / `ref mut`) with *inferred* lifetimes, in
  Goose's style: no annotations, the compiler infers the root each reference is
  anchored to and rejects anything that could outlive it. Signatures that would
  need an explicit lifetime in Rust are simply rejected in Sable with a message
  pointing at the fix (take ownership, or return a region-bound value).

The payoff is not just safety — it is that a function known not to escape its
arguments needs **no retain/release pair at all**. Swift's ARC overhead is
dominated by conservative retains at call boundaries; a borrow checker that only
answers "does this escape?" removes most of them.

### Pillar 5 — Goose's layout model, available by declaration

The genuinely new idea in Goose is not "no heap" — arenas are old. It is that
**composite values are laid out inline, recursively, with no interior
pointers**, and that links between them are *relative* offsets rather than
machine addresses. That buys three things at once: fewer allocations, better
cache behaviour, and position-independence (a structure can be `memcpy`'d to
disk and read back without a parse step).

Sable adopts this as an opt-in layout attribute rather than a whole-language
mandate:

```sable
@inline_layout
struct Order {
    id:     U64
    lines:  Line[>..]        // grow-only inline array, capacity in the block
    note:   U8[..64]         // inline string, bounded
    parent: Order&<u32>?     // relative reference, 4 bytes, 0 == null
}
```

An `@inline_layout` struct is a single contiguous block containing no absolute
pointers. It may be serialised with `Bytes.of(order)` and revived with
`Order.fromBytes(buf)` — which validates framing, tags, lengths and every
relative link before returning, exactly as Goose does, so hostile input yields
`None` rather than a wild reference.

See [02-memory-model.md §5](02-memory-model.md) for the array family and the
restrictions (variable-size elements are iterable but not indexable, a
variant-sized union's case cannot be reassigned in place, etc.).

### Pillar 6 — Reflection you pay for by the slice

.NET's reflection is magnificent and is also the main reason `ILLink` is hard.
Sable's model:

- Every type has **static metadata** — size, alignment, field offsets, name,
  protocol conformances. Always present, used by the runtime itself, cheap.
- **Full reflection** (member enumeration, dynamic invoke, attribute reading,
  dynamic construction) requires the type to be `@reflectable`, or to be reached
  from a `@reflectable` root, or to be named in a `reflect` block in the
  manifest.
- A **whole-program reflection graph** is computed at link time. `sable build`
  prints what reflection pulled in and what it cost, in bytes.

This keeps the "serialize any object", "bind a UI to a view model", "load a
plugin" stories alive while keeping hello-world under a megabyte.

## 1.3 Influences, itemised and honest

| From | What is taken | What is rejected |
|---|---|---|
| **C#** | Syntax shape, `namespace`/`using`, properties, `async`/`await`, LINQ-shaped query methods, nullable reference types, Roslyn's compiler architecture | GC, IL, AppDomains, class-first design |
| **TypeScript** | Structural unions, `type` aliases, flow-sensitive narrowing, `satisfies`, declaration-merging-free module model | Structural typing everywhere, `any` |
| **Swift** | Protocols with associated types, `some`/`any`, ARC, value semantics, extensions, labelled arguments, `guard`, typed `throws` | Objective-C interop baggage, source-breaking evolution, whole-module optimisation as a correctness crutch |
| **Rust** | Move semantics for `@noncopy` types, exhaustive matching, `Result`-shaped errors, Cargo's manifest/lock/workspace design, trait coherence rules | Lifetime annotations, affine-by-default, `unsafe` as the universal escape hatch |
| **Goose** | Inline recursive data layout, relative references, variant-sized unions, root inference for borrow checking, in-place construction, zero-copy load | The no-heap mandate, the single-resizable-value-per-stack constraint, C as the only backend |
| **Go** | Build speed as a design requirement, one formatter with no options, toolchain as a single binary | Interface satisfaction by accident, nil zero values |

### 1.4 On Goose's benchmark claims

Goose's README reports a geometric mean over 16 benchmarks of roughly **1.13×
to 1.19× faster than the best safe Rust**, 3.5× faster than idiomatic C++, and
~1.25× lower peak memory than safe Rust. These are self-reported, the benchmark
set is small and chosen by the author, and the margin against Rust is narrow
enough to be sensitive to the C backend used (MSVC vs clang changes it).

Treat the headline as **"the layout model is competitive, and the allocation
count is dramatically lower"** rather than "this language is faster than Rust".
The allocation figure is the one worth believing and the one worth copying: the
README's worked example is an order record at **29 bytes / 0 allocations** in
Goose against 160 bytes / 1 allocation in C++ and 153 bytes / 4 allocations in
Rust. That is a structural advantage, not a benchmark artefact, and it is what
Pillar 5 is chasing.

### 1.5 The conflict you should know about up front

**ARC and Goose's model are not natural allies.** Goose is fast partly *because*
it has no reference counts, no object headers, and no identity — a value is
bytes at an address owned by a scope. ARC requires a header, requires identity,
and requires that a value can be pointed at from many places with an unknown
lifetime. Runtime reflection pushes the same direction: it wants headers,
vtables, and type pointers.

A language that mandates both gets the worst of each. So Sable does not mandate
either. It makes Tier 0 (`struct`, inline, headerless) the *default for data*
and Tier 2 (`class`, ARC, reflectable) the *default for identity*, and it makes
crossing between them explicit and visible. The thing to get right in the
implementation is the **promotion analysis** — the pass that notices a `class`
never escapes and demotes it to a stack-allocated, refcount-free value. That
pass is where the performance story actually lives, and it should be built in
Phase 3, not deferred.

## 1.6 Explicit non-goals

- **No implicit numeric conversion** that can lose information. `Int → I64` yes;
  `I64 → I32` requires `as` and is checked in debug builds.
- **No null by default.** `T` cannot be null; `T?` can. Flow analysis narrows.
- **No exceptions for control flow.** `throws(E)` is a typed, checked effect
  compiled to a return-value discriminant, not to unwinding tables. Panics
  (`abort`) are separate, unrecoverable, and used for invariant violations.
- **No macros, ever.** This is a decision, not a deferral. Compile-time
  code execution (`const fn`, `comptime` blocks) computes values. `@derive`
  writes the built-in conformances. Source generators (docs/08) cover what
  is left: Sable functions that read declarations and write new files, which
  are ordinary Sable. Generators add code and cannot rewrite it, so the
  grammar never needs quote and splice syntax, there is no hygiene problem,
  and every tool reads generated code as it reads any other code. A proposal
  for macros has to show something that a generator cannot do, and why that
  is worth the cost (docs/08 §8.10).
- **No inheritance from generic base classes across modules** without an
  explicit `open` declaration — devirtualisation matters more than flexibility.
