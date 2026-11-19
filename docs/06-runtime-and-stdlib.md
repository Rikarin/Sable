# 6. Runtime, Reflection, and the Framework

## 6.1 Runtime size and shape

The runtime is written in C (freestanding-capable, no libc dependency beyond
what the platform layer provides) and is statically linked. Target: **under
200 KB** for the full runtime, under 40 KB for the minimal profile.

```
Sable.Runtime/
├── alloc.c         size-class allocator (mimalloc-derived)
├── refcount.c      retain/release, weak side table, deinit dispatch
├── metadata.c      type metadata records, generic metadata cache
├── conformance.c   protocol conformance registry and lookup
├── existential.c   boxing, dynamic casts
├── region.c        bump allocators, reservation, reset
├── error.c         typed error propagation ABI
├── task.c          executors, task allocation, continuations
├── actor.c         serial executors, actor mailboxes
├── reflect.c       the reflection API's native half
├── backtrace.c     symbolication
└── platform/       darwin.c linux.c windows.c wasm.c
```

Profiles:

| Profile | Contents | Size |
|---|---|---|
| `minimal` | alloc, refcount, metadata, error | ~40 KB |
| `standard` | + conformance, existential, region, task, actor | ~140 KB |
| `full` | + reflect, backtrace | ~200 KB |

Chosen per-target in the manifest. `minimal` plus `--deny-reflection` is the
embedded story.

## 6.2 Object header and metadata

```c
typedef struct {
    const TypeMetadata *type;   // 8 bytes
    uint32_t            strong; // 8..12
    uint32_t            weak_and_flags;  // 12..16
} SableObjectHeader;            // 16 bytes

typedef struct {
    MetadataKind  kind;          // class | struct | union | existential | ...
    uint32_t      size, stride, align_mask;
    const char   *mangled_name;
    const ValueWitnessTable *vwt;     // copy, destroy, move, ...
    const FieldDescriptor   *fields;  // always present (static reflection)
    uint32_t      num_fields;
    const ConformanceRecord *conformances;
    const FullTypeInfo      *full;    // non-null only for @reflectable
    // class-only: superclass, vtable
} TypeMetadata;
```

As implemented so far (`runtime/objects.c`), the metadata of a
counted object is smaller: the destroy function, the name, an array's
element size, copier and destroyer (a class keeps its superclass there),
the instance's size and alignment, and a visitor that hands each strong
reference the object holds — a class's fields, an array's elements, through
optionals, structs, unions and closure contexts, but not weak or unowned
references — to `sable_heap_visit`. A class's vtable follows. The visitor is
what the leak check walks (docs/02 §2.2): with `SABLE_CHECK_LEAKS` set every
live object is kept in a set, and a program that ends with objects alive
lists them by type and prints the reference cycles among them, each as the
types along it, each step naming the field that holds the next object
(`Test.Pet.toy -> Test.Toy.pet -> Test.Pet`).

**Immortal objects.** An object with the immortal flag (bit 0 of the flags)
is never counted: `sable_retain`, `sable_release` and compiled code's inline
forms of them leave it alone, `isUnique` is false for it, and it is never
released. String literals are made so, in read-only memory. So is every
object an `@immortal static let` holds (SYNTAX.md §20): once the global's
initial value is stored, `sable_make_immortal` is given each strong
reference the value holds — through structs, tuples, unions and closure
contexts, as the visitor finds them — and sets the flag, and the global is
not destroyed as the program ends. Only those objects become immortal; what
they hold is counted as before, so an object an immortal one lets go of is
released as any other is. The leak check's rule: an immortal object is not
a live object, and neither is any object reachable from an immortal one
through strong references when the program ends — the check walks from the
immortal objects with the same visitors before it counts what is left, so
what remains is reported as before. The interpreter keeps the same rule.

A class's metadata has one more field before its vtable (which is field 9):
its type information, `SableTypeInfo`, which arrays and the runtime's own
objects leave null. This is the format code casting to a protocol builds
on; it only grows, at the end of each record.

```c
typedef struct { const char *name; const char *type_name;
                 int64_t offset; int64_t kind; } SableField;      // kind: 0 data, 1 strong, 2 aggregate, 3 weak/unowned
typedef struct { const char *name; } SableProtocol;               // "Module.Protocol"
typedef struct { const SableProtocol *protocol;
                 const void *witnesses; } SableConformance;       // witnesses: null for now
typedef struct { int64_t kind;                                    // 1: a class
                 int64_t field_count; const SableField *fields;   // the superclass's first
                 int64_t conformance_count;
                 const SableConformance *conformances; } SableTypeInfo;
```

- **Field descriptors**: every stored field — the superclass's first, at
  the same offsets — with its name, its type's name as Sable writes it
  (`Zoo.Animal?`, `Sable.Collections.Deque<String>`), its offset from
  the start of the object, and what it holds. The leak report reads them.
- **Conformance records**: one per protocol the class declares — where it
  is declared, in an extension that holds for every instance, and every
  protocol those refine — by a protocol descriptor, `@"sable.protocol.M.P"`,
  which each unit makes and the linker keeps one of. A superclass's
  conformances are in the superclass's records. `witnesses` is reserved for
  what a cast to `any P` builds the existential from; the compiler leaves it
  null until such casts lower through the registry.
- **The registry** is the records themselves, found from an object's
  metadata, with nothing built as the program starts:
  `sable_conformance(metadata, protocol)` returns the record of the class's
  or a superclass's conformance, or null; `sable_conforms(object, protocol)`
  answers `x is P`. A protocol is matched by its descriptor's address or,
  across images (a Sable shared library and the program), by its name.
- **`--deny-reflection`** (`sable build`/`run`, for a file or a package; or
  `[targets.<triple>] reflection = false` in a manifest): the field descriptors carry
  no names, and `Mirror`, `TypeInfo` and the `Reflect` questions are refused
  (S3450). Printing values is not reflection and still works.

Not yet: a kind and type information for structs, unions and other
non-class types (their value witnesses are functions the compiler calls
directly, with no table the runtime can reach); the witness tables
conformance records will point at; and the profiles (`minimal`, `standard`, `full`), which need the runtime split
into parts a link can leave out. The allocator's size classes reach 1 KB;
each class has 64 KiB slabs of its own, mapped from the system aligned to
their size, which count the blocks they have out and are unmapped when that
reaches zero (but for one each class keeps): memory a program once held in
small objects goes back to the system. `SABLE_STATS` reports the slabs held,
the most held, and those given back. The compiler warns (S3460) of classes
that hold one another strongly: the type-level strong-reference graph's
cycles through two or more classes, broken by a `weak` or `unowned` field.

An existential, `any P`, is its box — an object of a class for the value's
type, `any$box$of$T`, each a subclass of one root class — and P's witness
table for that type, laid out inline after it: a function per requirement
that takes the box. Copying an existential shares the box; a witness that
changes the value (a setter, a mutating method) is given the box where the
existential keeps it and first copies it if another existential shares it.
A dynamic cast or test, `e as? Square`, `e is any Named`, asks the box's class:
to a concrete type, whether it is that type's box; to another protocol,
whether it is the box of any type of the program that conforms to it, in a
table the compiler writes out for each protocol cast to — the conformance
lookup, done without the registry above. The two answer different
questions and are kept apart for now: the registry's records hang off a
class's metadata, so it answers for objects alone, while an existential's
box is an object of the compiler's own class whatever the value's type,
struct or union as much as class, and has no records of its own. A cast of
an existential to a protocol therefore asks which box class it is, and
builds the existential from witnesses the compiler emitted for each
conforming type. Once conformance records carry their witness tables (the
`witnesses` field above) and non-class types have type information, a box
can point at its value's metadata and the cast can become a registry lookup
— a cast table for a library's types that the program does not see whole
would need that.

`FieldDescriptor` (name, type, offset) is **always** emitted, for every type.
It costs a few bytes per field and it buys: debugger formatters, `Mirror`,
structural equality/hashing derivation, and the zero-copy validator. `FullTypeInfo`
— methods, parameter names, attribute payloads, dynamic-invoke thunks — is
emitted only for `@reflectable` types.

## 6.3 Reflection API

```sable
// Always available, on any type. Read-only, cheap.
let m = Mirror(of: value)
for field in m.fields {
    print("\(field.name): \(field.typeName) = \(field.value)")
}

// Requires @reflectable
let t = TypeInfo.of(Document.self)
for method in t.methods where method.isPublic {
    print(method.signature)
}
let doc = try t.construct(args: ["title": "Untitled"])
let result = try t.invoke("render", on: doc, args: [])

// Attribute reading
for route in t.attributes(of: Route.self) { }  // Route: a struct conforming to Attribute

// Dynamic lookup by name — the plugin story
if let ty = Runtime.lookupType("App.Plugins.MyPlugin") { }
```

### The reachability model

At link time the compiler computes the reflection closure:

1. Roots: `@reflectable` types, types named in `[reflect] include`, and types
   passed to `TypeInfo.of` with a statically known argument.
2. Closure: field types, parameter and return types, generic arguments, and
   protocol conformances of everything in the set.
3. Everything outside the closure has `full == null` and is stripped.

`sable build --explain-reflection` prints the tree with per-type byte costs. A
type that got pulled in by accident is then visible and fixable, which is
exactly the problem `ILLink` warnings try and fail to solve.

**`Runtime.lookupType(String)` forces conservatism**: a package that calls it
with a non-constant string must declare `[reflect] dynamic = true`, which
disables stripping for that package's public types. The cost is explicit.

---

## 6.4 Framework layout

The .NET BCL is the model: a large, coherent, well-documented standard library
with consistent naming, rather than Rust's thin core plus a crates.io lottery.
But it is split into packages so that a binary pays only for what it uses.

```
Sable.Core          ── always linked, no dependencies
  primitives, Optional, Outcome, String, the array family,
  Comparable/Equatable/Hashable/Iterable, MemoryLayout, unsafe pointers,
  Formattable/FormatSpec (what "\(x:spec)" writes through) and
  Parsable/ParseError/NumberStyles (text back to values)

Sable.Collections   List Dictionary Set Deque OrderedDictionary
                    PriorityQueue BitSet InlineArray SmallVec
Sable.Text          Unicode tables, normalisation, formatting, parsing,
                    Regex (a compiled NFA/DFA hybrid), StringBuilder
Sable.Math          BigInt, Decimal, SIMD<T, N>, Matrix, Quaternion,
                    Random (PCG + ChaCha), statistics
Sable.IO            File Directory Path Stream MemoryMappedFile stdio
Sable.Serialize     Encoder/Decoder protocols, conformance synthesis via
                    @derive(Serializable), JSON, TOML, MessagePack, and the
                    zero-copy @inline_layout path
Sable.Concurrency   Task TaskGroup Actor Channel Mutex RwLock Atomic<T>
                    AsyncSequence AsyncStream
Sable.Time          Instant Duration Clock Calendar TimeZone
Sable.Net           Socket Tcp Udp Tls Http1 Http2 Uri
Sable.Process       spawn, pipes, environment, signals
Sable.Diagnostics   Logger (structured), tracing spans, metrics, Backtrace
Sable.Testing       @test @bench, assertions, snapshot testing, property testing
Sable.Interop       C FFI helpers, CString, CVarArg, callback shims
Sable.Reflect       the reflection surface described above
```

### Dictionaries and hashing

`[Key: Value]` is `Sable.Core`'s `Dictionary<Key: Hashable, Value>`
(`lib/Sable.Core/Dictionary.sb`), a struct written in Sable that the
compiler knows only by name: `[K: V]` binds to it (its key checked for
`Hashable` as `Dictionary<K, V>` written out would be), `["a": 1]` calls its
`init(dictionaryLiteral:)`, `d[k]` its subscript, and `for (k, v) in d` goes
through its `Sequence` conformance like any other type's. There is one hashed
map; `Sable.Collections.HashMap` is gone, and `HashSet` is a dictionary to
`Bool` underneath.

- **Layout.** An array of entries (key, value, the key's full hash) in the
  order keys were added, none where one was removed, and beside it an index:
  a power of two of `Int` slots, at most half of them used, each empty (-1),
  left by a removal (-2), or the entry's position with the top 31 bits of its
  hash above it. A probe starts where the hash's bottom bits say and moves
  one slot at a time, comparing the stored hash bits before any key —
  SwissTable's metadata filter, without its SIMD groups, which Sable has no
  primitive for. Growing drops removed entries and rebuilds the index from
  the stored hashes; no key is hashed twice.
- **Order.** Iteration is in insertion order, and the language guarantees
  it: removing a key keeps the others' order, setting an existing key keeps
  its place, and a key removed and added again goes last. It does not depend
  on hashes, so a native program and the interpreter agree whatever the seed.
- **Value semantics.** A dictionary is a value; its arrays are shared until
  one copy changes (copy on write), as an array's elements are.
- **Hashing.** `Hashable` is `fn hash(into hasher: inout Hasher)`, which
  feeds a value's parts to a `Hasher` (`lib/Sable.Core/Hasher.sb`);
  `@derive(Hashable)` writes it field by field (a union's case first, then
  its payload), and `hashValue` is a protocol extension. A `Hasher` starts
  from a seed that is random per process (`sable_hash_seed`, from the
  system's secure random source as the runtime starts), so that keys chosen
  to collide in one run collide in no other; `SABLE_DETERMINISTIC_HASHING=1`
  fixes it, natively and in the interpreter alike, and `Hasher(seed:)` takes
  one of the caller's. A word is mixed in with one 64×64→128-bit multiply
  whose halves are folded together, under the seed (foldhash's
  construction, which Rust's hashbrown uses by default); a string's bytes
  are hashed the same way in the runtime, sixteen to a multiply
  (`sable_string_hash`), which the interpreter computes identically. That
  resists flooding as long as the seed stays secret — the level of .NET's
  randomised hashing rather than SipHash's keyed-PRF guarantee, which costs
  about 40% on string-keyed maps (`map-string`, measured) and was not
  taken.

`Sable.Core` reaches the runtime through `@builtin("name")`: a bodiless
declaration, allowed only in `Sable.Core`, whose calls are the named SIL
builtin applied in place — `String.count`, `print`, `Runtime.readFile`'s
floor — so the library is Sable down to the operations only the runtime can
perform, at no cost over the compiler doing it by special case
(docs/04, "Sable.Core in Sable").

A builtin whose name has a `Self` segment is a **family**, declared once in
a protocol's extension as the default of one of its requirements: each use
is the builtin of the type Self stands for there, so `x.leadingZeroCount`
is `count_leading_zeros_I8` on an I8 and `count_leading_zeros_U64` on a
U64. The numeric protocols are built this way (`AdditiveArithmetic`,
`Numeric`, `SignedNumeric`, `BinaryInteger`, `FixedWidthInteger`,
`SignedInteger`, `UnsignedInteger`, `FloatingPoint`, `BinaryFloatingPoint`):
what every integer or float can do — its operators, bit counts, rotations,
arithmetic that reports or saturates on overflow, IEEE operations — is
declared once for every type rather than once per type, each an LLVM
intrinsic at the type's width, and the rest of their API is Sable over
those, monomorphised for each type it is used with. A family is only the
built-in types' default: a type of the program's own conforming to the
protocol meets the requirement itself. Each type's own file
(`Int.sb`, `U8.sb`, `F64.sb`, …) says only what differs: its limits and
constants.

### Source layout

Each module is a directory of `lib/` (`lib/Sable.Collections/`), its files
all declaring `module Sable.Collections`, and each holds **one public type**,
named after it: `Stack.sb`, `Deque.sb`. Helpers private to a type live in
its file; a family that is one concept shares one (`Sequence.sb` has
`Iterator`, `Sequence` and their async forms; `Channel.sb` has
`ChannelIterator`); free functions are in a file named for what they do
(`Sort.sb`, `Math.sb`); a large extension of a type for one feature is
`<Type>+<Feature>.sb` (`String+Sequence.sb`). `lib/Sable.Core/` is flat.
Declarations are internal to their module by default, so how a module is
cut into files changes nothing a program sees.

Each type is tested in Sable, by `@test` functions in
`tests/lib/<Module>/<Type>Tests.sb`, and each module's tests are run with
coverage counted in the module (`sable test --cover lib/<Module>
tests/lib/<Module>/`): `tools/stdlib-coverage.sh` holds every file to at
least 85% of its lines and 70% of its branches, or the floor recorded for
it in `tools/stdlib-coverage.thresholds` (CONTRIBUTING.md).

Outside the standard set, in the registry but team-maintained:

```
sable-ui            retained-mode UI, platform-native backends
sable-graphics      a thin Vulkan/Metal/D3D12 abstraction
sable-db            database drivers and a query builder
sable-web           an HTTP server framework
```

### Naming conventions

- Types: `UpperCamelCase`. Protocols: `UpperCamelCase`, adjectival where it
  reads well (`Comparable`, `Sendable`) and nominal where it does not
  (`Collection`, `Iterator`).
- Members: `lowerCamelCase`. Cases: `UpperCamelCase`.
- Mutating/non-mutating pairs: `sort()` / `sorted()`, `reverse()` /
  `reversed()` — Swift's rule, which is the clearest one anybody has.
- No Hungarian notation, no `I`-prefix on protocols, no `_` prefix for private.

## 6.5 Derivation

Sable has no macros (docs/01 §1.6). `@derive` covers the conformances the
standard library defines:

```sable
@derive(Equatable, Hashable, Comparable, Serializable, Debug, Copy)
struct Point { var x: I32, var y: I32 }
```

Derivation is implemented as compiler-known conformance synthesis against the
always-present `FieldDescriptor` metadata, not as source generation. It
composes with generics (`@derive(Equatable)` on `Pair<T, U>` yields a
conditional conformance `where T: Equatable, U: Equatable`).

Conformances defined outside the standard library are written by source
generators (docs/08). A package declares a `@generator("name")` function, and
any package that depends on it can write `@name` on a type to get the
generated code.

## 6.6 Error ABI

`throws(E)` lowers to:

- The return value in the normal register(s).
- The error in a dedicated register (`x21` on arm64, `r12` on x86-64 — the
  Swift convention, which is well tested).
- A flag/branch at the call site, costing a predictable-not-taken branch.

No unwinding tables, no landing pads, no `__cxa_throw`. A throw is a return.
This is why typed throws are cheap enough to use everywhere, and why Sable does
not need a separate `Result`-returning convention for performance-critical code.

Panics (`fatalError`, a failed `precondition`, `todo()`, `unimplemented()`,
`Optional.expect` — lib/Sable.Core/Assertions.sb) do not unwind either: they
print `fatal error: ` and the message, the caller's place, a backtrace with
`SABLE_BACKTRACE` set, and call `abort()`. Supervisors that need to survive a panic run the
supervised code in a separate task on a separate stack with a guard page, and
recover at the task boundary.

## 6.7 Concurrency runtime

- **Cooperative thread pool** sized to the core count, with work stealing.
- Tasks are heap-allocated with a growable stack segment chain; `async`
  functions are compiled to state machines so a suspended task costs only its
  live state, not a full stack.
- Actors own a serial executor; `@MainActor` binds to the platform main loop.
- Structured concurrency is enforced by the type system (a child task's handle
  cannot escape the `withTaskGroup` scope — this falls out of root inference).
- Priority inheritance across `await` boundaries, so a high-priority task
  waiting on a low-priority actor does not invert.
