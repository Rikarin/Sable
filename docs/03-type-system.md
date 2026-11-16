# 3. The Type System

Syntax is in [../spec/SYNTAX.md](../spec/SYNTAX.md). This document is about
semantics and the representation decisions that follow from them.

---

## 3.1 Nominal, with structural unions as an island

Sable is nominally typed. Two structs with identical fields are distinct types.
The single exception is **structural unions** (`Int | String`), which are
compared by their case set after normalisation:

1. Flatten nested unions: `(A | B) | C` → `A | B | C`
2. Deduplicate by nominal identity
3. Sort by a stable type ordering (module path, then name, then generic args)
4. Collapse subsumption: `Int | Never` → `Int`; `T | T?` → `T?`

Two structural unions are the same type iff their normalised case lists are
equal. They are lowered to a compiler-synthesised nominal union whose cases are
named after the member types, so there is exactly one runtime representation
per normalised set.

**Bounded, not open.** A structural union may not contain `any P` for a
protocol with open conformance, because the case set would not be closed. This
is the restriction that makes exhaustiveness checking decidable.

---

## 3.2 Protocols and witness tables

### Conformance is explicit and coherent

A conformance exists only if declared, in the type's declaration or an
`extension`. Coherence rule, taken from Rust: a conformance `T: P` may be
declared only in the module that defines `T` or the module that defines `P`.
This guarantees at most one conformance per pair, globally, which is what makes
`any P` sound and makes conformance lookup a static map rather than a search.

The escape hatch for the third-party case is a wrapper struct:

```sable
struct JsonWrapper<T> : Serializable { let inner: T }
```

### Representation

A **witness table** is a static, read-only record:

```
struct WitnessTable {
    metadata:    *TypeMetadata       // the conforming type
    protocol:    *ProtocolDescriptor
    witnesses:   [*fn]               // one slot per protocol requirement
    assoc_types: [*TypeMetadata]     // one per associated type
    assoc_conf:  [*WitnessTable]     // one per associated-type constraint
}
```

Generated at compile time, placed in read-only data, deduplicated by the linker.

### `some P` — opaque types

```sable
fn makeIterator() -> some Iterator<Int>
```

The concrete type is fixed at the definition site and hidden from the caller.
The caller monomorphises against the real type, so this costs nothing — it is an
abstraction barrier, not an indirection. Two calls to the same function return
the same concrete type; the compiler knows it, the programmer does not.

### `any P` — existentials

```
struct Existential {
    witness: *WitnessTable
    storage: [3 words] | *Box      // inline buffer, or a pointer when too big
}
```

Four words. Values of three words or fewer with trivial copy semantics live
inline; anything else is boxed on the heap with ARC. This is Swift's layout and
the three-word threshold is the right one empirically.

**Variable-size types cannot be existentials.** An `@inline_layout` struct with
a `T[>..]` field has no statically known size, so it cannot go in the inline
buffer and cannot be boxed without copying to a fixed layout. This is a
restriction the programmer sees as a diagnostic, and it is the price of Pillar 5.

### Associated types and recursive constraints

```sable
protocol Collection {
    associatedtype Element
    associatedtype Slice: Collection where Slice.Element == Element
}
```

Recursive protocol constraints are permitted. The conformance checker works on
a demand-driven graph with cycle detection; a genuinely circular requirement
(`where Self.Slice == Self.Slice.Slice` with no base case) is reported as an
error rather than hanging.

### Default implementations and dispatch

A protocol extension member that is **not** a protocol requirement is
statically dispatched on the static type. A member that **is** a requirement is
dynamically dispatched through the witness table. This is Swift's model and it
is a known source of confusion, so the compiler warns when an extension member
shadows a requirement-like name in a way that will surprise.

---

## 3.3 Unions

### Representation

For a fixed-mode union with cases `C0..Cn`:

```
  tag:     smallest unsigned integer that holds n+1 values
  payload: max over cases of their payload size, aligned to max alignment
```

Three optimisations, all required for the standard library to be efficient:

1. **Spare-bit packing.** If every payload leaves unused bits (pointer
   alignment bits, `Bool` padding, invalid enum values), the tag goes in those
   bits. `T?` for a class `T` is one pointer, with `none` as the null pointer.
2. **Single-payload optimisation.** A union with one payload case and `k`
   empty cases uses the payload's extra inhabitants for the empty cases.
   `Outcome<T, Never>` is exactly `T`.
3. **Indirect cases.** A recursive case is boxed automatically; the box is a
   Tier 2 allocation and participates in ARC. `@indirect` on a case forces it;
   the compiler applies it where needed to break infinite size.

For a variable-mode union (`union X..`):

```
  tag:     as above
  payload: exactly the size of this case
```

Values are not relocatable between cases, arrays of them are sequential and
walked rather than indexed, and references may point into a payload because the
payload never moves.

### Exhaustiveness

Checked by the usefulness algorithm from Maranget's *Warnings for pattern
matching* — the same algorithm Rust uses. It handles nested patterns, ranges,
or-patterns, and array patterns with rests, and it produces a *witness* — a
concrete example of an unmatched value — which is what makes the diagnostic
useful:

```
error[S0631]: match is not exhaustive
  --> src/shape.sab:12:11
   |
12 |     match s {
   |           ^ pattern '.Poly(_)' not covered
   |
note: '.Poly' was added to 'union Shape' in module Geometry
help: add a case, or 'default => ...'
```

`@frozen` unions promise never to gain a case, so downstream modules may match
them exhaustively without `default`. Non-`@frozen` public unions require a
`default` in other modules — this is the library-evolution escape valve and it
is worth having from day one.

---

## 3.4 Generics

### Monomorphisation by default

Every distinct instantiation gets its own code. Pros: zero-cost abstraction,
inlining across the boundary, layout specialisation (a `List<Vec3>` stores
`Vec3` inline). Cons: code size, compile time.

Mitigations, in the order they should be implemented:

1. **Identical-code folding** at link time (`--icf=all`). Catches
   `List<*T>` for every class `T`, which is most of the duplication.
2. **`@polymorphic`** — an opt-in single boxed instantiation taking witness
   tables as runtime arguments, for generic code that is cold or deeply nested.
3. **Layout-equivalence classes.** Two instantiations whose type arguments have
   the same size, alignment and value-witness behaviour share a body. A pass in
   the mid-end, not the linker, so it works before inlining decisions.

### Constraint kinds

| Constraint | Meaning |
|---|---|
| `T: P` | conforms to protocol `P` |
| `T: Widget` | is or inherits from class `Widget` |
| `where T.Element == U` | same-type |
| `T: Sized` | statically known size (excludes variable-size types) |
| `T: Copyable` | not `@noncopy` — **implicit unless `~Copyable` is written** |
| `T: Sendable` | safe to cross an isolation boundary |
| `where N > 0` | const-generic predicate, evaluated at instantiation |

`~Copyable` is the opt-out that lets a generic container hold move-only types:

```sable
struct Box<T: ~Copyable> { private var value: T }
```

### Variance

Generic *types* are invariant. `List<Dog>` is not a `List<Animal>` — this is
non-negotiable for a mutable container.

Protocol existentials may be variant when the protocol declares it:

```sable
protocol Producer<out T> { fn next() -> T? }    // covariant: T only in output
protocol Consumer<in T>  { fn accept(_ x: T) }  // contravariant: T only in input
```

Declaration-site variance (Kotlin/C# style) is checked: an `out` parameter may
not appear in an input position. Use-site variance is not supported — it
complicates inference for a case that is rare in practice.

---

## 3.5 Type inference

A **bidirectional constraint-based** solver, in the Swift/Hindley-Milner-with-
subtyping family but deliberately more limited to keep it fast and predictable.

### Scope of inference

| Inferred | Not inferred |
|---|---|
| Local `let`/`var` types from the initialiser | Function parameter types |
| Generic arguments at call sites | Function return types (except `=>` bodies) |
| Closure parameter and return types from context | Stored property types |
| Literal types from context | Public API signatures |
| Union case from leading-dot syntax | |

**Public signatures are always fully explicit.** This is a hard rule: it makes
module interfaces stable, makes error messages local, and makes parallel
type-checking of modules trivially possible.

### Algorithm

1. Generate constraints over the expression tree (conversion, conformance,
   member-lookup, literal-default constraints).
2. Simplify: propagate equalities, resolve unambiguous overloads, apply
   defaults (integer literal → `Int`, float literal → `F64`).
3. Solve the remainder by ranked search over overload choices, with a score
   penalising implicit conversions and `any` boxing.
4. Apply the best solution; if several tie, report ambiguity with the candidates.

**A hard limit on solver steps** (default 10,000, configurable) exists from
day one. Swift's "expression too complex" errors come from not having designed
for this; a hard limit with a diagnostic that says *which subexpression* blew
the budget and suggests adding an annotation is strictly better than an
exponential type checker.

### Flow-sensitive narrowing

```sable
fn f(x: Int | String | None) {
    if x is Int { /* x: Int */ }
    guard let y = x else { return }    // y: Int | String
    if x != none { /* x: Int | String */ }
    match x { case let s as String => /* s: String */ }
}
```

Narrowing is tracked per-binding in a flow lattice over the union's case set,
and is invalidated on assignment to the binding or on a call that could mutate
it (for `var` captured by reference).

---

## 3.6 Effects

Three effects are tracked in the type system. All are part of a function's
type, all are inferred for closures and explicit for declarations.

| Effect | Marker | Propagation |
|---|---|---|
| Throwing | `throws(E)` | `try` at the call site; `rethrows` for higher-order |
| Async | `async` | `await` at the call site |
| Isolation | `@MainActor`, `actor` member | `await` across boundaries |

They compose: `async fn f() throws(IoError) -> T` has type
`async (…) throws(IoError) -> T`.

Typed throws are erased to `any Error` at `throws` with no type. Conversion
between error types at a `try` site uses a `From` conformance, applied
implicitly — this is the one implicit conversion in the language, and it is
worth the exception because the alternative (`try map(f(), to: AppError.io)`
everywhere) is what makes Rust's `?` need `thiserror`.

---

## 3.7 Nullability

`T` cannot be `none`. `T?` is sugar for `Optional<T>`, a union:

```sable
@frozen union Optional<T> { Some(T), None }
```

With spare-bit packing, `T?` for a class or reference is a single pointer.
There is no separate "nullable reference types" analysis as in C# — optionality
is in the type system from the start, so there is no warning-only mode, no
`!`-suppression culture, and no generics/nullability mismatch.

Sugar that exists: `?.`, `??`, `??=`, `!` (traps), `if let`, `guard let`,
optional-chaining through subscripts and calls, and implicit promotion `T → T?`
at assignment and argument positions.

---

## 3.8 Subtyping summary

The subtyping relation is deliberately small:

```
  T            <: T?                      (optional promotion)
  Derived      <: Base                    (class inheritance)
  T            <: any P        when T: P  (existential erasure, boxes)
  T            <: A | B        when T ∈ {A, B}   (union injection)
  Never        <: T                       (bottom)
  (A) -> B     <: (A') -> B'   when A' <: A and B <: B'   (function variance)
  @noncopy T   </: anything               (no subtyping for move-only types)
```

Notably absent: structural record subtyping, numeric subtyping (`I32 </: I64` —
widening is an explicit `as`), and array covariance.
