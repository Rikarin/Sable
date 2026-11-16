# 7. References

Annotated. For each entry: what it is, and **what to take from it**. Ordered by
how directly it bears on building Sable, not by prestige.

---

## 7.1 Compiler architecture

### Roslyn — the .NET Compiler Platform
`github.com/dotnet/roslyn`, and the `docs/` folder in that repository.

The reference implementation of the architecture in `04-implementation-plan.md`.
Read, in order: `docs/compilers/Design/Bound Node Design.md`, the source of
`Microsoft.CodeAnalysis.SyntaxNode` and `GreenNode`, and `Binder.cs`.

**Take:** the red/green tree, full-fidelity trivia, the lazy symbol model, the
`SemanticModel` as a public product, and the discipline of designing for the
IDE from the first commit. **Leave:** the IL backend, and the degree to which
VB and C# share a layer that neither quite fits.

### Swift Intermediate Language
`github.com/swiftlang/swift`, `docs/SIL/` (formerly `docs/SIL.rst`).

The single most important document for Phase 2–3. SIL exists precisely because
ARC optimisation and ownership checking cannot be done on LLVM IR, which is the
same reason Sable needs it.

**Take:** the ownership model (`@owned` / `@guaranteed` / `@trivial`), the
ownership verifier, textual SIL as a testing format, and the pass pipeline
structure. **Leave:** the Objective-C interop opcodes and the parts shaped by
ABI stability commitments.

### The rustc Dev Guide
`rustc-dev-guide.rust-lang.org`

**Take:** the query-based incremental compilation chapters (the model for
`05-toolchain.md §5.4`), the MIR borrow-check chapters, and the monomorphisation
and codegen-unit discussion — which is the clearest public account of the
compile-time cost of monomorphisation and what to do about it.

### LLVM
`llvm.org/docs/LangRef.html`, `llvm.org/docs/tutorial/` (Kaleidoscope),
and `llvm.org/docs/CodeGenerator.html`.

**Take:** everything. Also read the Swift and Rust frontends' IRGen for how a
high-level language actually lowers — particularly how they model value
witnesses and dynamic layout.

### LLVMSharp
`github.com/dotnet/LLVMSharp` — the C# bindings for the LLVM-C API, which is
what Phase 2 builds against. Check the LLVM version it tracks before committing.

### Books
- **Cooper & Torczon, *Engineering a Compiler* (3rd ed.)** — the standard
  text; the chapters on instruction selection and register allocation are the
  ones you will actually reread.
- **Nystrom, *Crafting Interpreters*** (`craftinginterpreters.com`, free) —
  the best explanation of recursive-descent parsing and error recovery written.
  Read Part II before writing the Phase 0 parser.
- **Appel, *Modern Compiler Implementation in ML*** — for SSA construction and
  dataflow framing, which is what the SIL passes are built on.

---

## 7.2 Type systems

- **Pierce, *Types and Programming Languages* (TAPL)** — the foundation.
  Chapters 15–19 (subtyping, recursive types, objects) and 22–23 (inference,
  universal types) are the directly relevant ones.
- **Pierce (ed.), *Advanced Topics in Types and Programming Languages*** —
  for variance, existentials, and dependent-ish features.
- **Dunfield & Krishnaswami, "Complete and Easy Bidirectional Typechecking for
  Higher-Rank Polymorphism" (ICFP 2013)** — the algorithm `03-type-system.md
  §3.5` is based on. Short, readable, implementable in a weekend.
- **Maranget, "Warnings for pattern matching" (JFP 17(3), 2007)** — the
  exhaustiveness and usefulness algorithm, including witness generation. This
  is what Rust implements. Also **"Compiling pattern matching to good decision
  trees" (ML Workshop 2008)** for the lowering in Phase 2.
- **Swift's generics implementation** — `docs/Generics/` in the Swift
  repository, which covers the requirement machine, associated-type inference,
  and conditional conformance. The only serious public treatment of
  protocol-with-associated-type systems at production scale.
- **Rust's trait coherence** — RFC 2451 and the orphan-rule discussions. The
  coherence rule in `03-type-system.md §3.2` is taken from here, and the
  rationale matters more than the rule.

---

## 7.3 Memory management — the core reading

This is the section to read first and most carefully, because it is where
Sable's design is least settled.

### Reference counting

- **Swift's ARC**: the Swift book's *Automatic Reference Counting* chapter for
  semantics; `SILOptimizer/Mandatory/` and `SILOptimizer/ARC/` in the compiler
  for the optimiser. **Take:** the whole model, including `weak`/`unowned`, the
  side table, and the `@guaranteed` calling convention.
- **Shahriyar, Blackburn, Frampton et al., "Down for the Count? Getting
  Reference Counting Back in the Ring" (ISMM 2012)** and **"Taking Off the
  Gloves with Reference Counting Immix" (OOPSLA 2013)** — the empirical case
  that reference counting is competitive with tracing once you apply deferral,
  coalescing, and non-atomic counts for thread-local objects. Directly informs
  the ARC optimisation list in `02-memory-model.md §2.2`.
- **Lobster's compile-time reference counting** — `strlen.it/lobster` and the
  language's memory-management page. Van Oortmerssen's earlier language does
  static analysis to eliminate most refcount operations; it is the direct
  ancestor of Goose's model and of Sable's Pass 12.
- **Bacon, Attanasio, Lee, Rajan, Smith, "Java without the Coffee Breaks"
  (PLDI 2001)** and Bacon & Rajan's cycle-collection work (ECOOP 2001) — if
  optional cycle collection is ever added, this is the algorithm.

### Ownership and borrowing

- **Goose** — `github.com/aardappel/goose`. The README is the primary source
  for root inference, inline layout, relative references, and variant-sized
  enums. Read it twice; most of `02-memory-model.md §2.4–2.5` derives from it.
- **Rust's NLL RFC (2094)** and **Polonius** (`github.com/rust-lang/polonius`)
  — the borrow checker reformulated as a Datalog problem. Even if Sable does
  not adopt lifetimes, Polonius's framing of the analysis as a fixpoint over
  facts is the right way to structure the root-inference pass.
- **Swift's ownership documents** — `docs/OwnershipManifesto.md`, SE-0377
  (`borrowing`/`consuming`), SE-0390 (noncopyable structs), SE-0176 (exclusive
  access to memory). The closest existing design to Sable's "move semantics
  only where needed" position, and the proposals document the trade-offs that
  were argued over.
- **Tofte & Talpin, "Region-Based Memory Management" (Information and
  Computation, 1997)** — the origin of regions, and still the clearest account
  of region inference.
- **Cyclone** (`cyclone.thelanguage.org`, and Jim et al., USENIX 2002) — safe C
  with regions and a "fat pointer" story. Tried most of what Sable's Tier 1 is
  attempting, two decades earlier, and the retrospective papers on *why it did
  not catch on* are worth as much as the design papers.
- **Hylo** (formerly Val) — `hylo-lang.org`. **Mutable value semantics**: a
  language that achieves memory safety with no reference counting and no
  lifetime annotations, using subscripts and projections instead of references.
  The most intellectually serious alternative to Sable's whole approach, and
  the one to read if root inference fails in Phase 3.
- **Vale** — `vale.dev`. Generational references: a cheap runtime check in
  place of static lifetime proof. A fallback design if the static analysis
  proves too restrictive in practice.
- **Project Verona** — `github.com/microsoft/verona`. Regions as a
  first-class concurrency and ownership primitive, with region-based isolation
  for data-race freedom. Directly relevant to `02-memory-model.md §2.7`.
- **Pony** — `ponylang.io`, and Clebsch et al., "Deny Capabilities for Safe,
  Fast Actors" (AGERE 2015). Reference capabilities as a type-system answer to
  aliasing, and ORCA, a concurrent cycle collector for actors.
- **Austral** — `austral-lang.org`. Linear types done minimally and explained
  unusually clearly; the rationale document is a good model for how to explain
  `@noncopy` to programmers.

### Layout and data representation

- **Goose's inline-layout section**, again — the order-record comparison
  (29 bytes / 0 allocations vs 160/1 in C++ and 153/4 in Rust) is the number
  Pillar 5 is chasing.
- **FlatBuffers** (`flatbuffers.dev`) and **Cap'n Proto**
  (`capnproto.org`) — zero-copy, position-independent data with relative
  offsets, in production for a decade. Cap'n Proto's "pointers" section and
  FlatBuffers' vtable design are the prior art for `&<u32>`; their
  *validation* code is the prior art for `fromBytes`.
- **Data-oriented design**: Mike Acton's "Data-Oriented Design and C++"
  (CppCon 2014) for the motivation; **Zig**'s `MultiArrayList` and
  **Jonathan Blow's Jai** talks for how a language can make SoA layouts
  ergonomic. A future `@soa` attribute belongs in the same family as
  `@inline_layout`.

---

## 7.4 Languages to study closely

| Language | Why | Where |
|---|---|---|
| **Swift** | The closest relative. Protocols, ARC, value semantics, SIL, and a decade of public design argument in `swift-evolution`. | `swift.org`, `github.com/swiftlang/swift-evolution` |
| **C#** | Syntax, tooling, library design, and `csharplang` — the best public record of language-design decision-making that exists. | `github.com/dotnet/csharplang` |
| **Rust** | Ownership, Cargo, error messages, and the RFC process. | `rust-lang.github.io/rfcs` |
| **Goose** | The layout and root-inference model. | `github.com/aardappel/goose` |
| **Lobster** | Goose's predecessor; compile-time refcount elimination. | `strlen.it/lobster` |
| **Hylo** | Mutable value semantics — the serious alternative design. | `hylo-lang.org` |
| **Nim** | ARC/ORC as a configurable memory strategy, plus destructors and move semantics in a GC-adjacent language. Nim's `--mm:arc` work is the closest thing to Sable's tiering that shipped. | `nim-lang.org` |
| **Zig** | `comptime`, explicit allocators, and a build system as a library. Read `comptime` before designing `const fn`. | `ziglang.org` |
| **Odin** | Implicit context pointer carrying an allocator — an alternative to `region` as a parameter. | `odin-lang.org` |
| **Mojo** | MLIR-based, ARC-ish, with `owned`/`borrowed`/`inout` conventions almost identical to the proposed ones. | `modular.com/mojo` |
| **Koka** | Perceus: precise, garbage-free reference counting with reuse analysis. If ARC optimisation stalls, Perceus is the published way forward. | `koka-lang.github.io` |
| **Crystal** | What a Ruby-shaped language with LLVM and whole-program type inference costs in compile time. A cautionary tale about global inference. | `crystal-lang.org` |
| **Carbon** | Explicit, well-documented reasoning about generics (checked vs templated) and ABI. | `github.com/carbon-language/carbon-lang` |

---

## 7.5 Toolchain and ecosystem

- **The Cargo Book** (`doc.rust-lang.org/cargo/`) — the manifest, feature
  resolution, workspaces, and profiles, all of which `05-toolchain.md` copies
  with small changes.
- **PubGrub** — Natalie Weizenbaum's "PubGrub: Next-Generation Version Solving"
  and `github.com/dart-lang/pub` — the resolver algorithm, and specifically
  its ability to *explain* a failure.
- **Salsa** (`github.com/salsa-rs/salsa`) — the incremental-computation
  framework extracted from rustc's query system. The model for Phase 5.
- **Language Server Protocol** (`microsoft.github.io/language-server-protocol/`)
  — and `rust-analyzer` as the best-engineered open implementation, especially
  for how it handles incompleteness and cancellation.
- **gofmt's rationale** — Rob Pike's "Go: Ten years and climbing" and the
  original gofmt discussions, for why the formatter must have no options.
- **Reproducible Builds** (`reproducible-builds.org`) — required reading
  before Phase 7's release engineering.

---

## 7.6 Sources for the Goose claims in these documents

The Goose figures quoted in `01-overview.md §1.4` and `02-memory-model.md` come
from the project's own README and from coverage of its October 2026 release.
They are **self-reported benchmarks** over a 16-benchmark suite chosen by the
author, and the margin against safe Rust (~1.13×–1.19×) is narrow enough to be
sensitive to the C backend used. Verify against `bench/results.md` in the
repository before repeating them anywhere.

- [github.com/aardappel/goose — The Goose Programming Language](https://github.com/aardappel/goose)
- [GIGAZINE — Goose runs faster and uses less memory than C++ and Rust](https://gigazine.net/gsc_news/en/20261027-goose/)
- [Medium — Goose vs Rust: Can a New Language Beat Rust at Memory-Safe Performance?](https://medium.com/@jamilxt/goose-vs-rust-can-a-new-language-beat-rust-at-memory-safe-performance-cdc399321428)
- [byteiota — Goose Language: Faster Than Rust, No Lifetime Annotations](https://byteiota.com/goose-language-faster-than-rust-no-lifetime-annotations/)
- [lavx.hu — Goose Claims Speed Wins Over C++ and Rust With Zero Heap Allocations](https://news.lavx.hu/article/goose-programming-language-claims-speed-wins-over-c-and-rust-with-zero-heap-allocations)

---

## 7.7 A suggested reading order for Phase 0

1. *Crafting Interpreters*, Part II — one week
2. Roslyn's `GreenNode` and `SyntaxNode` sources — one week
3. Maranget's pattern-matching papers — two days
4. Dunfield & Krishnaswami — two days
5. Swift's SIL documentation — one week
6. The Goose README, carefully, with notes — one day
7. Hylo's mutable-value-semantics papers — two days, as the control group

Then write the lexer.
