# 2. The Memory Model

This is the document to get right. Everything else in the language is
recombination of known ideas; the memory model is where Sable has to make a
decision nobody else has made in quite this shape.

---

## 2.1 The three tiers

```
                 identity?   escapes?   reflectable?   cost
  Tier 0  struct     no         no          static     zero — inline bytes
  Tier 1  region     no       scoped        static     bump pointer, bulk free
  Tier 2  class     yes        yes          full       header + ARC
```

### Tier 0 — `struct`

A `struct` is a value. It has no header, no identity, no reference count. It is
copied on assignment (unless `@noncopy`), laid out contiguously, and may be
embedded directly in other structs, in arrays, and in union payloads.

```sable
struct Vec3 { x: F32, y: F32, z: F32 }      // 12 bytes, exactly
struct Ray  { origin: Vec3, dir: Vec3 }      // 24 bytes, no indirection
```

Structs may conform to protocols. A struct used as `some P` stays a struct. A
struct used as `any P` is boxed — and the box is a Tier 2 allocation, which is
why `any` is visible in the syntax.

### Tier 1 — regions

A **region** is a bump-allocated arena with a statically known scope. It is the
bridge between "I want a graph of objects" and "I do not want to pay for a heap".

```sable
fn parse(src: String) -> Ast.Module {
    region arena {                      // reserve, bump, release in one store
        let tokens: Token[>..] = arena.alloc()
        lex(src, into: tokens)
        return parseModule(tokens, in: arena).toOwned()
    }                                   // whole arena released here
}
```

Values allocated in a region are referenced with `&` references whose **root is
inferred** — see §2.4. A region reference may not outlive its region, and the
compiler proves that statically with no annotations in the signature. `toOwned()`
copies out of the region into Tier 0/Tier 2 storage.

Regions are the answer for compilers, parsers, request handlers, frame-scoped
game state, and every other "allocate a thousand little things, throw them all
away at once" workload. They are where Goose's performance story actually lands
in a language that still has a heap.

### Tier 2 — `class`

A `class` is a heap object with identity, a header, and a reference count.

```sable
class Document {
    var title: String
    private var revisions: Revision[>..]
    weak var owner: Workspace?
}
```

Header layout (64-bit):

```
  offset  size  field
       0     8  type metadata pointer   (also the vtable/witness-table anchor)
       8     4  strong count
      12     4  weak count + flags (immortal, deiniting, side-table, pinned)
```

16 bytes. Swift's is 16. .NET's is 16 (sync block + MT pointer). This is the
price of identity and it is not negotiable if you want reflection and weak refs.

---

## 2.2 Automatic reference counting

ARC follows Swift closely, because Swift's design is correct and well-trodden.

- **Strong** references keep the object alive. Default.
- **`weak`** references do not, and read as `T?` — zeroed on deinit via a side
  table.
- **`unowned`** references do not and read as `T` — a dangling read traps in
  debug builds and is UB in release (`unowned(safe)` is the default, which keeps
  the trap in release too; `unowned(unsafe)` opts out).
- **`@immortal`** marks objects never released — string literals, singletons,
  metadata. The count check short-circuits on a flag bit.

### Deterministic destruction

`deinit` runs at the moment the last strong reference goes away. This is a
feature, not an implementation detail — it is why `defer`-free RAII works and
why file handles close when you expect. Release order within a deinit is
field-declaration order, reversed.

### Cycles

ARC leaks cycles. Sable does three things about it:

1. **`weak`/`unowned`** — the manual answer, as in Swift.
2. **Static cycle warnings** — the compiler builds a type-level
   strong-reference graph and warns on strongly-connected components that have
   no `weak` edge, with the cycle printed. Not sound (dynamic shape matters) but
   catches the parent/child mistake every time.
3. **`sable check --leaks`** — a debug-build heap walker that reports
   unreachable-but-retained subgraphs at exit. No background collector ships in
   release builds; optional cycle collection is a library (`Sable.Collections.Cycles`)
   for programs that genuinely need it.

### ARC elimination — the part that matters

Naive ARC is slow. The passes that make it not slow, in the order they should
be implemented:

| Pass | What it does | Where |
|---|---|---|
| **Local pairing** | Delete `retain`/`release` pairs with no escape between them | SIL, function-local |
| **Borrowed parameters** | A `ref` parameter is guaranteed live by the caller, so the callee emits nothing | SIL, from borrow analysis |
| **Guaranteed `self`** | Methods take `self` borrowed, not owned | Calling convention |
| **Escape-based promotion** | A `class` that never escapes its frame becomes a stack value with no header | SIL → LLVM |
| **Uniqueness (`isUnique`)** | Copy-on-write containers skip the copy when the count is 1 | Runtime intrinsic |
| **Release hoisting/sinking** | Move releases out of loops, merge on join points | SIL |
| **Cross-module borrow summary** | Escape facts serialised into the module interface so they work without LTO | Module metadata |

That last one is important and is a place to beat Swift: Swift loses most of
these at module boundaries without whole-module optimisation. Sable serialises a
per-function **escape summary** (`params: [borrowed, owned, escaping]`,
`returns: [fresh, borrowed-from-0, escaping]`) into the `.sbi` interface file,
so the optimisation survives separate compilation.

---

## 2.3 Ownership and moves

Sable is **copy-by-default**, not move-by-default. `let b = a` copies `a` if `a`
is a struct (a bitwise copy plus retains for any class fields) and retains if
`a` is a class reference. This is the C#/Swift behaviour and it is the right
default for a language that wants to feel approachable.

Move semantics appear in exactly three places:

### `@noncopy` types

```sable
@noncopy struct FileHandle {
    private let fd: I32
    deinit { sys.close(fd) }
}

let a = FileHandle.open("x")
let b = a                 // moves
use(a)                    // error: 'a' was moved to 'b' at line 2
```

Use-after-move is a compile error with the move site reported. This is Rust's
affine checking, restricted to the types that actually need it: handles, locks,
GPU resources, one-shot channels, tokens.

### Explicit `move`

```sable
let owned = move buffer   // forces a move of a copyable type; 'buffer' is dead after
```

Useful to suppress a retain when you know the source is dead, and to express
transfer in APIs.

### `consuming` / `borrowing` parameter modifiers

```sable
fn consume(consuming h: FileHandle)   // callee takes ownership, caller's binding dies
fn peek(borrowing h: FileHandle)      // callee may not store it anywhere
fn mutate(inout v: Vec3)              // exclusive mutable access for the call
```

Defaults: `borrowing` for class and `@noncopy` parameters, by-value copy for
small structs, `borrowing` for large structs (the ABI decides; it is observable
only through exclusivity).

### Exclusivity

Swift's law of exclusivity: while an `inout` access is in progress, no other
read or write to the same storage may occur. Enforced statically where the
accesses are to provably distinct storage, and with dynamic access markers
(`beginAccess`/`endAccess`) where they are not. Dynamic checks are on in debug,
off in release, and `-fexclusivity=checked` keeps them on in release for
programs that want it.

---

## 2.4 Root inference — borrow checking without annotations

This is the Goose idea that transplants best, and it is the reason Sable has no
lifetime syntax.

**Every reference has a static root**: the variable, parameter, region, or
global that bounds its lifetime. The compiler infers the root of every reference
expression and rejects any flow where a reference could outlive its root.

```sable
fn longest(ref a: String, ref b: String) -> ref String {
    return a.count >= b.count ? a : b
}
```

In Rust this needs `<'a>` on both parameters and the return. In Sable, the
return's root is inferred as `{a, b}` — the *join* of the two parameter roots —
and the caller checks that the result does not outlive either argument. No
annotation is written, and no annotation is inferred into the signature either:
the **root set is part of the function's serialised interface**, computed by the
compiler and displayed by `sable doc`, but not written by hand.

### The rules

1. A reference's root set is a set of *static anchors*: parameters, locals,
   regions, `static` storage, or `heap` (for Tier 2 ARC references, which have
   no static bound).
2. Assigning a reference to a location requires the location's root to outlive
   every anchor in the reference's root set.
3. Returning a reference is legal iff its root set contains only parameters,
   `static`, or `heap`.
4. Storing a reference into a region requires every anchor to outlive that
   region.
5. A function is **specialised per root shape** when that matters for codegen
   (Goose does this; it is monomorphisation on lifetimes rather than on types).

### What this rejects that Rust accepts

Signatures that need lifetimes *unrelated* to parameters — self-referential
structs, custom arena types with lifetime parameters, and anything requiring
variance reasoning. Sable rejects them with a diagnostic naming the fix:

```
error[S0412]: cannot return a reference rooted in local 'tmp'
  --> src/parse.sb:41:12
   |
41 |     return &tmp.name
   |            ^^^^^^^^^ 'tmp' dies at the end of this function
   |
help: three ways to fix this
   |  - return an owned value:        return tmp.name.toOwned()
   |  - allocate in the caller's region: fn f(in r: region) -> &String
   |  - return the whole value:       return tmp
```

Rejecting a legal-but-complex program with a good error is a far better trade
for this language's audience than admitting it behind a lifetime syntax.

### Interaction with ARC

Root inference and ARC are complementary, not competing. A Tier 2 reference has
root `heap` — it is always legal to store and return, and it costs a retain. A
Tier 0/1 reference has a static root — it is restricted, and it costs nothing.
The borrow checker's job is to let as much code as possible stay in the second
category. **Every reference the checker proves is non-escaping is a
retain/release pair the optimiser deletes.**

---

## 2.5 Inline layout — Goose's data model, by declaration

### `@inline_layout`

An `@inline_layout` struct is laid out as one contiguous block containing **no
absolute pointers**. Variable-size fields are stored in place, in declaration
order, with their sizes in the block.

```sable
@inline_layout
struct Order {
    id:       U64
    customer: U8[..32]       // inline, bounded at 32 bytes
    lines:    Line[>..]      // grow-only inline array
    parent:   Order&<u32>?   // relative reference: 4 bytes, 0 means null
}

@inline_layout
struct Line { sku: U8[..16], qty: U16, price: U32 }
```

Consequences, all of them inherited from Goose:

- **Zero allocations** for the whole nested structure. The Goose README's
  worked example of this shape is 29 bytes with 0 allocations against 160/1 in
  C++ and 153/4 in Rust.
- **Iterable, not indexable**, when elements are variable-size. `for line in
  order.lines` walks; `order.lines[3]` is a compile error unless `Line` is
  fixed-size. Build a side index of references if you need random access.
- **In-place construction.** Values are built at their final destination;
  there is no "construct then copy".
- **Position independence.** No absolute pointers means the block can be
  `memcpy`'d, written to disk, or sent over a socket as-is.

### Relative references

```sable
Order&              // machine address, 8 bytes
Order&<u32>         // 4-byte offset from the field's own address
Order&<u16 in pool> // 2-byte offset from the base of a named pool
Order&<u32>?        // offset 0 means null
```

Relative references are what make inline layout composable: a tree can have
parent links without acquiring pointers, and therefore without losing
position-independence.

### Zero-copy serialisation

```sable
let bytes = Bytes.of(order)            // no serialisation pass: it is already bytes
Files.write("order.bin", bytes)

// later, possibly in another process:
match Order.fromBytes(buf) {
    case .Ok(let order) => process(order)
    case .Invalid(let why) => log.warn("rejected: \(why)")
}
```

`fromBytes` validates framing, every tag, every length, and every relative link
before returning. Corrupt or hostile input returns `.Invalid`, never a bad
reference. This is a validate-once-then-use model, not a trust-the-bytes model.

`@inline_layout` is a conformance as well as a layout: every such struct
conforms to `Sable.Core`'s `InlineLayout`, whose one requirement is
`static fn fromBytes(_ bytes: U8[>..]) -> Decoded<Self>`, and the compiler
writes the body for the struct's layout, as `@derive` writes `==`. Generic
code asks for it by name (`fn load<T: InlineLayout>(…)`), and `Bytes.of`
takes only a `T: InlineLayout`.

### Variant-sized unions

```sable
union Shape   { Circle(r: F32), Rect(w: F32, h: F32), Poly(pts: Vec2[>..]) }
union Shape.. { Circle(r: F32), Rect(w: F32, h: F32), Poly(pts: Vec2[>..]) }
```

- **Fixed mode** (`Shape`) reserves the largest payload. Values are indexable
  and the case may be reassigned in place. This is the default and what every
  other language means by a tagged union.
- **Variable mode** (`Shape..`) gives each value exactly its variant's size.
  Arrays of them are sequential and compact, and a reference may point into a
  payload — but **a value cannot change variant**. Immutable-shape data: ASTs,
  message logs, serialised protocol frames.

A 100k-node AST in variable mode is typically 40–60% of its fixed-mode size,
because the largest node is usually much larger than the median node.

### The array family

Borrowed wholesale from Goose, because the taxonomy is correct:

| Type | Meaning | Resizable | Stable refs |
|---|---|---|---|
| `T[k]` | fixed size `k`, known at compile time | no | yes |
| `T[]` | size fixed at construction | no | yes |
| `T[..k]` | inline capacity `k`, grows and shrinks within it | within cap | yes |
| `T[..]` | inline capacity, inferred | within cap | yes |
| `T[>..]` | **grow-only** — the common case | yes | yes |
| `T[>..<]` | grow and shrink — stacks, queues, heaps | yes | no |
| `T[:]` | non-copying slice view | no | borrows |

`String` is `U8[>..]` with a UTF-8 invariant. `List<T>` in the standard library
is a Tier 2, heap-backed, copy-on-write container for the cases where none of
the above fit.

---

## 2.6 How the tiers compose — a worked example

```sable
@reflectable
class Scene {                        // Tier 2: identity, observers, reflection
    private var entities: Entity[>..] // Tier 0 inline array of Tier 0 structs
    private let frameArena: region    // Tier 1, reset every frame

    fn update(dt: F32) {
        frameArena.reset()                       // one store
        region frame = frameArena {
            let visible: Entity&[>..] = frame.alloc() // refs rooted in 'frame'
            cull(entities, into: visible)             // no allocation, no ARC
            for e in visible { e.step(dt) }           // borrowed, zero retains
        }                                             // released, one store
    }
}

@inline_layout
struct Entity {                      // Tier 0: 48 bytes, no header, no count
    transform: Mat4x3
    flags:     EntityFlags
    name:      U8[..24]
}
```

The `Scene` pays for ARC once, at the top. Everything below it is bytes. The
per-frame working set is one bump pointer. And `Scene` is `@reflectable`, so a
level editor can enumerate it, bind to it, and serialise it.

That layering — **ARC at the boundaries, values in the interior, regions for
the churn** — is the idiom the language is designed to make natural.

---

## 2.7 Concurrency and memory

- Tier 0 values are `Sendable` when all fields are.
- Tier 2 classes are not `Sendable` unless they are `@immutable` or `actor`-
  isolated. Refcount operations on shared classes are atomic; on classes the
  compiler proves are thread-local they are not. The header's flag word carries
  the decision at runtime for the cases the compiler cannot prove.
- Regions are thread-affine by construction: a region reference is not
  `Sendable`, which falls directly out of root inference (`region` is not a
  `heap` root).
- `actor` types get a serial executor and `await`-mediated access, Swift-style.
  Data races on actor state are a compile error, not a runtime hazard.

---

## 2.8 Open questions to settle before Phase 3

1. **Is `region` a type or a declaration?** Treating it as a value (`in r: region`)
   is more flexible and composes with root inference; treating it as a lexical
   form is simpler to check. *Leaning: a value with a `region` type that cannot
   be stored in Tier 2 storage.*
2. **COW containers vs inline arrays.** Two growable-array stories is one too
   many. Can `List<T>` just be `T[>..]` in a Tier 2 box? *Probably yes; verify
   the slicing ergonomics first.*
3. **Does `@inline_layout` compose with generics?** A `Box<T>` where `T` is
   variable-size needs layout to be computed per instantiation. Monomorphisation
   handles it, but `any P` cannot hold a variable-size value. *Restriction:
   variable-size types are not existential-compatible.*
4. **Atomic vs non-atomic refcounts.** Decide whether to carry the flag bit
   (runtime branch, Objective-C-style) or to require `@shared`/`@local`
   annotation. *Leaning: flag bit, with the compiler setting it statically where
   it can prove locality.*
