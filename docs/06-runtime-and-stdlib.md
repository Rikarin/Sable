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

Not built: every build links the whole runtime, and until the runtime is
split there is no setting for it (`[targets.<triple>] runtime` is an error,
docs/05 §5.2.10). `minimal` plus `--deny-reflection` is meant to be the
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
its type's metadata, `SableTypeMetadata`, which arrays and the runtime's own
objects leave null. Every other type has one too — a struct, an enum, a
union, an optional, a tuple, a function, an array, a slice, an existential,
a scalar, a string, a metatype — written by the compiler for the types a
program asks about (`T.self`, a `Type`, the SIL instruction
`metadata $T`), the classes it makes, the types its existentials box, and
what those reach. Both formats only grow, at the end of each record.

```c
typedef struct { const char *name; const char *type_name; int64_t offset;
                 int64_t kind;                                    // 0 data, 1 strong, 2 aggregate, 3 weak/unowned
                 const SableTypeMetadata *type; } SableField;     // null unless SABLE_TYPE_FIELD_TYPES
typedef struct { const char *name; } SableProtocol;               // "Module.Protocol"
typedef struct { const SableProtocol *protocol;
                 const SableWitnessTable *witnesses; } SableConformance;
typedef struct {                                                  // a value's copy, move and destruction in memory
    void (*initialize_with_copy)(void *dest, void *src, const SableTypeMetadata *);
    void (*initialize_with_take)(void *dest, void *src, const SableTypeMetadata *);
    void (*assign_with_copy)(void *dest, void *src, const SableTypeMetadata *);
    void (*assign_with_take)(void *dest, void *src, const SableTypeMetadata *);
    void (*destroy)(void *value, const SableTypeMetadata *);
    void (*visit)(void *value, const SableTypeMetadata *);       // each strong reference, to sable_heap_visit
} SableValueWitnesses;
typedef struct SableTypeMetadata {
    int64_t kind;                                                 // SABLE_KIND_*: 1 class, 2 struct, 3 enum, 4 union, ...
    int64_t field_count; const SableField *fields;                // a class's always; others' with SABLE_TYPE_FIELDS
    int64_t conformance_count; const SableConformance *conformances;
    const char *name;                                             // 'Zoo.Animal?', 'Sable.Collections.Deque<String>'
    int64_t size, alignment, stride, flags;                       // SABLE_TYPE_TRIVIAL, _NONCOPYABLE, _HAS_DEINIT, ...
    const SableValueWitnesses *witnesses;
    int64_t argument_count;
    const SableTypeMetadata *const *arguments;                    // type arguments; an optional's, array's, tuple's parts
    const SableMetadata *object;                                  // a class's object metadata: vtable, superclass
} SableTypeMetadata;
```

- **Layout**: size, alignment and stride as the value is stored —
  natively LLVM's own numbers for the type the code uses, in the
  interpreter `TypeLayout`'s (src/Sable.SIL/TypeLayout.cs), which the code
  generator lays values out by; a test holds the two to agree for every kind.
  A class's are a reference's; its instance's are its object metadata's.
- **Value witnesses**: copy, take (a move: every Sable value moves by its
  bits), assign with copy or take, destroy, and visit, each on memory and
  given the metadata — what generic code compiled once for every type will
  call (#297). A trivial type shares the runtime's `sable_bits_witnesses`
  and a single counted reference (a class, a string, an array, an optional
  reference) `sable_reference_witnesses`; any other type has a table of its
  own, `@"sable.vwt.T"`, whose copy, destroy and visit wrap the value witness
  functions compiled code already calls directly, with the runtime's take
  and assignments. A move-only type's copy witnesses are null.
- **Field descriptors**: a struct's or class's stored fields — a class's
  superclass's first, at the same offsets from the start of the object — a
  tuple's elements, an enum's or union's cases with where their payload is;
  each with its name, its type's name as Sable writes it, its offset, what it
  holds, and its type's metadata. A class's are always there, without the
  field types: the leak report names fields by them. Everything else's, and
  the field types, are emitted only in a program that reads them
  (`Type.fieldCount` and the like; `SilModule.FieldDescriptors`),
  the switch unspecialised generic code will also turn on where it reads a
  generic type's field offsets. A program that never asks pays for none.
- **Type arguments**: a generic type's instance lists its arguments'
  metadata — what metadata made at run time will be cached by (#297 stage
  6); until then each instance a program uses is emitted statically.
- **Linkage**: every record is `linkonce_odr` (in a COMDAT on Windows) when
  the program is built in units, as a box's class is, so the linker keeps
  one of each and its address is the type's identity — `Type`'s `==` is a
  pointer comparison. A Sable shared library and the program that loads it
  each have their own records, so `sable_metadata_equal` falls back to the
  kind, the size and the name, and `sable_metadata_hash` hashes the name
  (FNV-1a), which the interpreter computes alike.
- **Conformance records**: one per protocol the type declares — where it is
  declared, in an extension that holds for every instance, and every
  protocol those refine — by a protocol descriptor, `@"sable.protocol.M.P"`,
  which each unit makes and the linker keeps one of. A superclass's
  conformances are in the superclass's records.
- **The registry** is the records themselves, found from a value's
  metadata, with nothing built as the program starts (but the tables of a program built in units, below):
  `sable_type_conformance(type, protocol)` returns the record of the type's
  (or a superclass's) conformance, or null, and `sable_conformance` the same
  from an object's metadata; `sable_conforms(object, protocol)` answers
  `x is P`. A protocol is matched by its descriptor's address or, across
  images (a Sable shared library and the program), by its name. Compiled
  code asks it for `animal is Named` (and `is A & B`, each part) on any
  object: the builtin `class_conforms:M.Named`, a call of `sable_conforms`
  with the descriptor.
- **`--deny-reflection`** (`sable build`/`run`, for a file or a package; or
  `[targets.<triple>] reflection = false` in a manifest): the field descriptors carry
  no names, and `Mirror`, `TypeInfo` and the `Reflect` questions are refused
  (S3450). Printing values is not reflection and still works. A type's
  metadata keeps its name: a trap message has it anyway.

`Type` (lib/Sable.Core/Type.sb) is a type as a value of the language, the
existential metatype (SYNTAX.md "Metatypes"): a pointer at the type's
metadata. `T.self` is one — `metadata $T`, a constant, as each instance of
generic code is compiled for its types — and every `T.Type` lowers to the
same pointer, so it converts to a `Type` as it is. `type(of: x)` reads a
value's dynamic type: an object's own class from its header (its object
metadata's `type`, the builtin `object_metadata`), what an existential's box
holds (`sable_box_value_metadata`, which an `any Error`'s box answers too,
its class having the error's type as its argument), and any other value's
static type, a constant. A `Type` has its metadata's `kind`, `name`, `size`,
`alignment`, `stride`, fields and type arguments, and its value witnesses
applied to a value (`copy`, `move`, `assign`, `destroyCopy`), as code that
knows a type only by its metadata applies them; it is Equatable, Hashable
and Formattable (its name). `t.isInstance(x)` and `t.isSubtype(of: u)` test
against a type known only at run time (`sable_metadata_is_subtype`): the
same type, a superclass by the chain `superclass_of` walks, or each protocol
an existential's metadata names — read from its name, `any M.A & M.B`, as
protocols are known by name across images — by `sable_type_conformance`.
The interpreter models the same records (src/Sable.SIL/TypeMetadata.cs), so
a program interpreted answers as the native one does.

Not yet: the profiles (`minimal`, `standard`, `full`), which need the runtime split
into parts a link can leave out. The allocator's size classes reach 1 KB;
each class has 64 KiB slabs of its own, mapped from the system aligned to
their size, which count the blocks they have out and are unmapped when that
reaches zero (but for one each class keeps): memory a program once held in
small objects goes back to the system. `SABLE_STATS` reports the slabs held,
the most held, and those given back. The compiler warns (S3460) of classes
that hold one another strongly: the type-level strong-reference graph's
cycles through two or more classes, broken by a `weak` or `unowned` field.

An existential, `any P`, is its box — an object of a class for the value's
type, `any$box$of$T`, each a subclass of one root class, whose metadata has
T as its one type argument — and P's witnesses for that type, laid out
inline after it: a function per requirement that takes the box. Copying an
existential shares the box; a witness that changes the value (a setter, a
mutating method) is given the box where the existential keeps it and first
copies it if another existential shares it.

**Witness tables.** The same functions are the conformance's witness
table, `SableWitnessTable`, which T's metadata's conformance record for P
points at: P's requirements in the order of its existential's slots, then
T's associated types' metadata and what each must conform to (by index and
protocol, found in the registry from that metadata).

```c
typedef struct { const SableProtocol *protocol;
                 int64_t requirement_count; void *const *requirements;
                 int64_t associated_type_count; const SableTypeMetadata *const *associated_types;
                 int64_t associated_conformance_count;
                 const SableAssociatedConformance *associated_conformances; } SableWitnessTable;
```

A table is made for each conformance an existential needs: a type put in
an existential of P, and every type conforming to a protocol cast to — the
program's non-generic conformers and each type boxed anywhere, whichever the
program lowers first. Its witnesses are kept while its conformer can be made,
boxed or asked about, and are dropped with it. An existential built where the
type is known copies them into its slots, so a call through it costs what it
did: there is no load from the table, and the inline slots are the table's
contents cached in the value. Which records a type has follows from its
declarations alone (a built-in type's from Sable.Core's extensions); which
tables a unit makes depends on what its own code boxes and casts. So in a
program built in units a record's `witnesses` is null, every unit's copy of
it the same as `linkonce_odr` requires, and each unit's constructor hands
the runtime the tables it made (`sable_register_witness_table`), which the
lookup consults after the records; a program compiled whole points its
records at its tables and registers nothing. (Swift keeps such records in a
section the linker gathers; a constructor works on every target Sable has.)

**Casts.** `e as? Square` asks the box's class whether it is that type's
box (or, for an object, a superclass's box, and then the object). `e as?
any Q` and `e is any Q` are registry lookups: the metadata of what the box
holds — an object's own class, which may be a subclass of the one boxed
(`sable_box_value_metadata`) — is asked for Q's witness table
(`sable_witness_table`, each part's for `any A & B`), and the existential is
the same box, shared, with the table's witnesses. `animal as? any Named`
boxes the object as its static class and asks the same. An instance of a
generic protocol, `any Producer<Animal>`, whose slots a conformer to
`Producer<Dog>` may fill by variance, is still cast by trying each conforming
type of the program in turn, from witnesses the compiler picks for each.

A table's first entries are what an existential can call — requirements
that mention `Self` have no slot — each taking the existential's box. Then
come its **generic entries** (`generic_requirements`): every method,
operator and property getter the protocol itself declares, in declaration
order, in the convention unspecialised code calls them in — `self` (the
address of its storage, for a `mutating` one) unless the requirement is
static, its parameters, the address a result of `Self` is written to, the
address an error of an associated type is written to, and `Self`'s
metadata last; a value of `Self` or of one of its associated types
(`next() -> Element?`, `index(after: Index) -> Index`) is passed by the
address of its caller's storage. A requirement whose error is of an
associated type, `throws(Failure)`, writes the error where its caller says
and throws that address: the witness of a conformer whose `Failure` is
`Never` never writes it. A requirement that convention cannot pass yet
(generic itself, async, an initialiser, or `Self` inside another type than
an optional or a tuple) has an empty entry. Each witness is the requirement lowered
as if its body called itself on the conforming type, so it is met as
specialised code meets it: the type's member, a default, a builtin. Last
come its **bases** (`bases`): the conformer's tables of the protocols this
one refines directly, in the order it names them, so that unspecialised
code given a `Greeter`'s table reaches its `Named` one with a load, not a
lookup. A program built unspecialised fills every part of every table it
makes, so that every unit's copy of a table is the same.

**Unspecialised generic code** (#297 stage 4). With `-generics=unspecialised`
(or `SABLE_GENERICS=unspecialised`), a generic function whose body uses its
type parameters only in the ways below is compiled once for every type, as
Swift compiles generic code. A value of a type parameter's type is
**opaque** (SIL's `@opaque(T)`): its size is known only from the metadata,
so it is held indirectly, and nothing is allocated on the heap for it.

- A parameter of a type parameter's type is the address of the caller's
  storage, borrowed for the call; a result of one is written to an address
  the caller passes, before the hidden parameters (`$*@opaque(T)`). After
  its own parameters the function takes each type parameter's metadata and
  then a witness table for each protocol one is constrained to, in that
  order.
- Inside, each value of one is a buffer of the metadata's size in the
  function's frame: one per value, made where the function starts, so that
  a loop reuses it and the frame does not grow as it runs. A local is such
  a buffer, empty (all zeros) until stored. Copying one calls the type's
  `initialize_with_copy` witness, assigning `assign_with_take`, destroying
  `destroy`; a move is a copy of its bits, as every type's take is.
- `T.self` is the metadata, `T.self == Int.self` asks `metadata_equal`,
  `type(of: x)` the metadata or an object's own class (`sable_opaque_type`).
  A requirement is called through the table's generic entry; one of a
  protocol a given one refines, through the table's bases.
- `"\(x)"` asks the runtime for the **describer** of the type
  (`sable_opaque_describe`): a function, made for each type whose metadata
  code that knows the type hands to unspecialised code and registered as
  the image starts, that writes the value out as an interpolation in code
  that knows its type does.
- Where the types are known, a call passes each argument of a type
  parameter's type by address, copying nothing: a local, or a stored field
  of one, where it is, when nothing the call evaluates or passes can change
  it (no inout or out parameter, no mutating receiver, its arguments only
  literals and variables); anything else the call borrows, in a slot of its
  frame that holds it unowned (`alloc_stack [unmanaged]`, stored without a
  retain and never destroyed). It passes a slot for the result and loads it
  after, and passes the constant metadata and the table made for the type —
  by the whole program, or by the unit, whose copy is the one the linker
  keeps — referred to as the constant it is, not looked up. Where the table
  is not one the module made (a subclass inheriting a conformance), the
  registry's answer is found once where the function starts and kept for
  the next call (`sable_witness_table_cached`), as the metadata of an array
  of a type parameter's values is (`sable_array_type`).
- The SIL inliner leaves unspecialised code alone: its opaque values are
  sized by its own metadata parameters. But an optimised build (`-O2`,
  `-O3`, `-Os`) first specialises it where the types are known (#297
  stage 5, `Specialise` in src/Sable.SIL/Specialise.cs): a call passing
  constant metadata and tables calls a copy of the function made for those
  types (`M.pick$specialised$Int`), with each opaque type the type it is,
  its views of a value the value, its metadata constants, a requirement
  called through a table the conformance's witness (copied for its `Self`
  too), a value witness the type's own copy or destroy, a describer or an
  error eraser the copy of it for the type, and a result written where the
  caller said returned. Calls inside a copy are specialised as it is made;
  a closure the caller reabstracted to the generic convention is passed as
  it was made. The copies are ordinary code, which the inliner and the
  passes after it treat as any other; what cannot be copied stays generic
  and is called as before, and the generic functions nothing calls any more
  are dropped. `-Os` copies only small functions and calls in loops;
  `SABLE_SPECIALISE=0` turns the pass off, and `SABLE_SPECIALISE_TRACE=1`
  says why a copy was not made.

The interpreter runs the same SIL: an opaque value is held as a value of the
type it is, and copied and destroyed as the metadata the running function
was given says.

A protocol extension's member is generic over `Self`: it takes `Self`'s
metadata and the protocol's table, and `self` where its caller holds it.
A `some P` parameter is an anonymous type parameter, after the named ones
(`@opaque(some1)`, `$metadata.some1`): `contains(_ pattern: some Pattern)`
takes the pattern where its caller holds it, its type's metadata and its
table for Pattern, as `contains<P: Pattern>(_ pattern: P)` would. (One
with arguments, `some Sequence<Int>`, is still specialised.) An `inout`
parameter of a type parameter's type, `using generator: inout G`, is the
address of the caller's storage, seen as `*@opaque(G)`, and a mutating
requirement changes the value there. A protocol's subscript requirement,
`self[position]` on a Collection, is read through the table as a method
of its indices is: the table's generic entries hold its getter, in its
place among the protocol's requirements (a setter is not one yet).

An array of a type parameter's values (`T[]`, `T[>..]`, `T[>..<]`) is a
buffer of them, as any array is: its object metadata — the element's size,
and its copy and destroy — is made at run time from the element's type
metadata (`sable_array_metadata`), once per element type, and the
runtime's array code copies and destroys such elements through the
element's value witnesses. Such an array can be made, indexed, walked with
`for`, appended to, popped, and passed to and returned from code that knows
the type, which sees it as the array it is. Its type's own metadata, `[T]`,
is made the same way (`sable_array_type`), so that an array of one can be a
type argument: **polymorphic recursion** — `nest<T>` calling `nest([x])` —
is one function, where a specialised build makes it the one instance over
existentials (its arrays then hold `any Any`).

A slice of them (`T[:]`) is a view of such a buffer, laid out as any
slice is — the buffer, the start, the count — its elements where the
element's stride says. It is passed, sliced, walked, read and written
through (copying its run first when the buffer is shared, from the
array's run-time metadata), and its type's metadata, `[T:]`, is made at
run time beside the array's (`sable_slice_type`), with value witnesses
that count its buffer. So the slice extensions' members — `sort`,
`reverse`, `swapAt`, `contains`, `ArrayIterator`, the Collection
conformance — are compiled once for every element type, and a mutating
one called on an array is given the array's own slice, seen as its own.

An optional or a tuple of a type parameter's values (`T?`, `(T, Int)`,
`(T?, U)`, `T??`) is held indirectly too, as SIL's optional or tuple of an
`@opaque(T)`: its metadata is made at run time from its parts' — once per
set of parts, kept for the program's life and at each call that asks
(`sable_enum_type`, `sable_tuple_type`) — and lays the value out as
`TypeLayout` lays out the same type where its parts are known, so that code
that knows the type passes its own `Int?` or `(String, Bool)` where
unspecialised code takes one, and takes one back, as it is. So an optional
of an object or a string is the reference, null for none; one of a Bool or
a Char is the scalar, none one of its spare values (which the metadata of
those types says with the flags `SABLE_TYPE_NULLABLE`, `_BOOL` and
`_CHAR`); any other is a tag and the payload after it; a tuple's elements
follow one another, each aligned. Its value witnesses copy, destroy and
visit the active case's payload or each element; the runtime says what case
a value is (`sable_enum_tag`), where its payload or an element is
(`sable_enum_payload`, `sable_tuple_element`), and writes a case
(`sable_enum_inject`), so `none`, wrapping, `if let`, `match`, `??`, `!`,
`== none`, building a tuple, taking it apart and `let (a, b) =` all work on
one. Such a type is a type argument as an array of them is (`T?` passed as
another function's `U`), and a requirement may take or give one of Self
(`overflowingAdd(_:) -> (Self, Bool)`), written where the caller says.

A member of a generic struct or union (`Stack<T>.push`, `Outcome<T,
E>.unwrap`), of an optional's extension or of an array's is generic over the
type's type parameters, then its own: it takes their metadata and the tables
their constraints and its extension's `where T: P` ask for, as a generic
function does, and `self` as any value of a type parameter's — a struct or
union that holds one of them by value is held indirectly, laid out at run
time (`sable_aggregate_type`, from a shape the compiler writes: the kind,
the fields' or cases' names, the name, and the type arguments' count) as
`TypeLayout` lays it out. Its fields are where the metadata says
(`sable_field_address`); a struct that holds them only through a reference
(`T[]`) has the layout it has whatever `T` is, and is passed as it is. Such a
type is a type argument and a type's name as Sable writes it
(`M.Pair<String, Int>`) as one made where its arguments are known. A type
with a deinit, packed or aligned, move-only, a union with a boxed case, a
class with a field of `T` by value, and an extension with `where T == X` are still specialised.

A **generic class** whose stored fields hold its type parameters' values
only behind references — arrays and slices of them, plain generic structs
that do (`[Key: Int]`), other such classes — and which has no superclass is
laid out alike whatever its arguments, so its methods and getters are
compiled once as a struct's are, the reference passed as it is
(`Stack`, `Queue`, `Deque`, `OrderedDictionary`); any method may change
self's fields. One that a subclass may override is still specialised, as
its vtable holds an instance for each class's arguments; so is an
initialiser, which makes the object and so needs its class's metadata for
the arguments, not made at run time yet. A **subscript**'s getter and
setter, of a generic struct, union or class, are compiled once as methods
are (a dictionary's `d[key]` and `d[key] = v`).

**Associated types.** An associated type read through a type parameter —
`S.Element`, `C.Index`, `S.Iter`, `S.Iter.Element`, and inside a protocol
extension its `Self`'s, named bare (`Element`) — is opaque too
(`@opaque(S.Element)`), first read as the function's same-type requirements
and its protocols' read it (`S.Iter.Element` is `S.Element`; after `where
S.Element == Int` it is an `Int`). Its metadata is found where the function
starts, one step at a time from the type parameter it is read through: the
step's associated type in the table of the conformance that declares it —
the table the function is given (through its bases), else the registry's
for the step before's metadata. SIL keeps how on the function,
`associated("S.Element", "S", "Sable.Core.Sequence", 0, "$witnesses.S.Sequence")`,
which the code generator and the interpreter read; the body asks for the
metadata with `opaque_metadata:S.Element`. What an associated type is known
to conform to — its protocol's constraints (`Iter: Iterator`), a `where
S.Element: Equatable` — is found in the registry; code that knows the
type makes those tables when it makes the conformer's (every table of a
conformance's associated types' conformances, and a `where` clause's),
and a table's associated types' tables are kept with it. An array is a
conformer as any type is (`Int[]` as `C: Collection`): its tables are
made from its conforming extensions.

An error of a type parameter's or an associated type's type,
`throws(S.Failure)`, is written to an address the caller passes after
where a result goes, and the function throws that address. A caller that
knows the type passes a slot of it and takes the error from there; one
whose error is `Never` passes a slot never written; unspecialised code
that throws the same passes its own caller's place, and so throws on what
it is thrown untouched. One that throws `any Error` — a `rethrows`
function, `rethrows(Failure)` among them, throws that — takes such an
error in a slot of its own and boxes it as `any Error` with the type's
**eraser** (`opaque_erase_error`, `sable_opaque_erase_error`): a function
made where the type is known, with its table of the conformance to
Error, and registered with the runtime as a describer is. So `map`,
`filter`, `reduce` over any sequence are one function each. An error of
one caught inside unspecialised code (`do`/`catch`, `try?`) keeps the
function specialised.

**Closures.** A function type of a type parameter's values, `(T) -> U`,
`(Element, Element) -> Bool`, `(inout Value) -> Void`, takes the generic
convention: each value of a type held indirectly is the address of the
caller's storage, borrowed, and a result of one is written where the
caller says, after the parameters (`@thick (@opaque(T), *@opaque(U)) ->
()`). A `rethrows` function's closures throw `any Error`, so one copy
serves closures that throw and those that do not, as specialised. A
closure made where the types are known, `{ $0 * 2 }` as an `(Int) -> Int`,
is given to such code through a **reabstraction thunk** (`$reabstract$…`)
that converts: it loads each argument as the type it is, calls the
closure, kept in its context, and stores the result where it was told. A
closure made in unspecialised code, `firstIndex(where: { $0 == element })`,
is compiled with it: its code is an unspecialised function of its own
(`…$closure0$generic`), given the metadata and tables the function it is
in was given, and called by a thunk — the closure's code — that reads them
from its context, after what it captures. A value of a type held
indirectly that it captures is kept in an array of one in the context,
which sizes it at run time and destroys it through its witnesses, and is
copied into a slot of its own where the closure's code starts. Such a
closure captures by value (a `let`, a parameter); one that shares a `var`
of a type parameter's type, or captures `self`, or is inside another,
keeps its function specialised, as does a function that returns a closure
over its type parameters.

**Existential witnesses.** A slot of an existential's witness table whose
witness is generic code compiled once — a method of a generic type boxed as
`any P` (`Tag<Int>`), a protocol extension's default for the boxed type, an
optional's member — calls that code, with the boxed type's metadata and
tables, rather than an instance made for the slot alone. The thunk opens
the box and makes the call as code that knows the type makes it (its
arguments stored where unspecialised code takes them, its result taken out
of a slot); it is lowered once everything pending has been, since the call
may ask for the very table the thunk is in. Which way a slot goes depends
on its witness alone, so every unit's copy of a table is the same.

What unspecialised code cannot serve yet — an optional chain through a
`T?`, a structural union of `T`, a method of a generic class
a subclass may override or whose fields hold `T` by value, a field of a
function type in a generic type, a property's setter, a class's initialiser or a struct's that holds `T` by value, `"\(x:?)"`, an async function or a
generator — is still specialised for its type arguments in
the same program, so a program builds either way; `SABLE_GENERICS_TRACE=1`
says, for each generic function, which way it went and why. A specialised
build (the default) makes neither the generic entries nor the describers.

Field descriptors were meant to be emitted always, for every type; as built
they are a class's always and every other type's only in a program that
reads them (above), so that hello world stays its size. Reflection
(`Mirror`, `Reflect`, `TypeInfo`, below) is not read from them yet: it is
SIL the compiler writes for each type reflected on, which runs the same in
the interpreter. What it shares with the metadata is the type's name as
Sable writes it (one function, the debugger's too) and `--deny-reflection`.
`FullTypeInfo` — methods, parameter names, attribute payloads, dynamic-invoke
thunks — is emitted only for `@reflectable` types.

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

1. Roots: `@reflectable` types (or `extension T: Reflectable {}`, for a
   type of another package), and types passed to `TypeInfo.of` with a statically known argument.
2. Closure: field types, parameter and return types, generic arguments, and
   protocol conformances of everything in the set.
3. Everything outside the closure has `full == null` and is stripped.

`sable build --explain-reflection` prints the tree with per-type byte costs. A
type that got pulled in by accident is then visible and fixable, which is
exactly the problem `ILLink` warnings try and fail to solve.

**`Runtime.lookupType(String)` would force conservatism** (it is not built):
a call with a non-constant string would have to keep every public type of
the package reflectable. How a package says so is to be designed with it;
the manifest has no `[reflect]` table, since what is reflected on is said
in code.

---

### Shipping the runtime and the library

The runtime and the standard library are shipped as source, carried inside
the compiler, and compiled once per target and profile into archives —
`libsable_runtime.a` and `libsable_std.a` (`.lib` on Windows) — kept in the
build cache, `$SABLE_HOME/cache/<key>/` (docs/05 §5.4), which a program links
against instead of compiling the library again: the model of Rust's `.rlib`
and Swift's `libswiftCore`. The source stays the unit of distribution — for
go-to-definition, debug information (an archive's debug information carries
each library file's text), `sable test --doc`, a program bringing its own
copy of a library file, and `#if os(...)`/`#if DEBUG`, which decide what the
library holds for a target and profile. The format is tied to the exact
compiler that made it, through its identity in every key; nothing is
promised across compilers, and there are no binary-only packages. A release
carries the host's archives prebuilt (`sable cache warm --into`); another
target's are built on first use. An optimised single-file build still
compiles the library with the program, whole, so that the SIL optimiser sees
its bodies; a package's release build (ThinLTO) links the archive and lets
LLVM inline across it.

A registry or git dependency is shipped the same way: as source, pinned by
the lock file's checksum, and built once per target and profile into an
archive and interface of its own in the same cache, keyed besides by its
version and the entries of what it depends on — so that every project
using it at that version links the one archive. A workspace's own packages
are compiled into its `target/`.

## 6.4 Framework layout

The .NET BCL is the model: a large, coherent, well-documented standard library
with consistent naming, rather than Rust's thin core plus a crates.io lottery.
But it is split into packages so that a binary pays only for what it uses.

```
Sable.Core          ── always linked, no dependencies
  primitives, Optional, Outcome, String (String.Index, searching through
  Pattern, case) and Substring, a zero-copy piece of one, sharing
  StringProtocol's members (docs/03 §3.10), Char with Unicode's character properties and case
  tables (UnicodeTables.sb, generated by tools/unicode-tables.py), the
  array family, Comparable/Equatable/Hashable, MemoryLayout, unsafe
  pointers, Iterator/Sequence and the Collection family (Bidirectional,
  RandomAccess, Mutable, RangeReplaceable), FromSequence, and the
  sequence algorithms, eager and behind '.lazy',
  Formattable/FormatSpec (what "\(x:spec)" writes through),
  Parsable/ParseError/NumberStyles (text back to values), and
  RandomNumberGenerator with the system's generator and a seeded one
  (xoshiro256**), behind 'random(in:)', 'randomElement' and 'shuffle'

Sable.Collections   List Dictionary Set Deque OrderedDictionary
                    PriorityQueue BitSet InlineArray SmallVec
Sable.Text          UTF-8/16/32, ASCII and Latin-1 codecs, normalisation,
                    formatting, Regex (a compiled NFA/DFA hybrid),
                    TextBuilder
Sable.Math          the elementary functions (sin, exp, pow, gamma, …),
                    generic over ElementaryFunctions; integer math;
                    BigInt, Decimal, SIMD<T, N>, Matrix, Quaternion,
                    statistics
Sable.IO            File Directory Path Stream MemoryMappedFile stdio
Sable.Serialize     Encoder/Decoder protocols, conformance synthesis via
                    @derive(Serializable), JSON, TOML, MessagePack, and the
                    zero-copy @inline_layout path
Sable.Concurrency   Actor Channel Mutex RwLock Atomic<T> AsyncStream
                    (Task, TaskGroup and AsyncSequence are Sable.Core's)
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
map; `Sable.Collections.HashMap` is gone, and `Set` (a value, with union,
intersection and the subset tests) is a dictionary to `Bool` underneath.
`InlineArray<T, N>` is the built-in `T[N]` by that name, and `SmallVec<T, N>`
keeps `N` elements inline in a `T[..N]` before it moves to the heap.

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
declaration, allowed only in the standard library (`Sable.Core` and the
other `Sable.*` modules), whose calls are the named SIL builtin applied in
place — `String.count`, `print`, `Runtime.readFile`'s floor — so the
library is Sable down to the operations only the runtime can perform, at no
cost over the compiler doing it by special case (docs/04, "Sable.Core in
Sable"). Outside the core, `Sable.Math` declares the elementary functions
this way: its `ElementaryFunctions` protocol's static requirements
(`static fn sin(_ x: Self) -> Self`, …) are libm's for F64 and F32
(`math_sin_F64`, through LLVM's intrinsic where it has one), and its free
functions are generic over it, written once — `sin(x)` is `T.sin(x)`,
monomorphised to the call to libm. Floats have no `x.sin()` method.

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
`Iterator`, `Sequence` and their async forms, `Collection.sb` the
collection protocols; `Channel.sb` has
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
`SABLE_BACKTRACE` set (each frame by its line in a `-g` build), and call `abort()` — or
run the program's panic hook in place of the report (`Panic.setHook`, Sable.Diagnostics).
An error that leaves `main` is reported the same way, as `uncaught error: `, and ends the
program with status 1. Supervisors that need to survive a panic run the
supervised code in a separate task on a separate stack with a guard page, and
recover at the task boundary.

## 6.7 Concurrency runtime

- **Cooperative thread pool** sized to the core count, with work stealing:
  a Chase–Lev deque per worker and priority band, a global injection
  queue, workers parked on a condition each, and one heap of timers that
  whichever worker looks for work serves. `SABLE_WORKERS` sets the size;
  0 runs every task on the main thread, in a deterministic order. (See
  docs/04, "Async, as implemented".)
- An I/O reactor — kqueue, epoll, or an I/O completion port — which the
  parked worker waits in until the earliest timer: a task waiting for a
  socket suspends, and no worker blocks. Work that must block runs on a
  blocking pool (`Task.blocking`). (See docs/04, "Async, as implemented".)
- Tasks are heap-allocated with a growable stack segment chain; `async`
  functions are compiled to state machines so a suspended task costs only its
  live state, not a full stack. (As built: frames on a bump-allocated slab of
  chunks per task; each frame also has a drop function, so a task that can
  never go on is destroyed as the program ends rather than leaked.)
- Actors own a serial executor: a lock-free inbox and a queue its holder
  keeps. A call from outside the actor hops onto it — taking it at once if
  it is free, waiting its turn otherwise — and back after; a call inside
  it, or to a `nonisolated` member, does not hop. Actors are reentrant at
  their suspension points: a task suspended inside an actor's method lets
  the actor go, so other calls run meanwhile, and takes it again before it
  goes on — so what the method read before an `await` may have changed
  after it. `@MainActor` code runs on the main thread, which runs the
  program's entry. Values crossing into or out of an actor are Sendable,
  and counted atomically from then on (docs/02 §2.7).
- Structured concurrency: a task spawned from another is its child — it is
  cancelled with its parent and keeps it from finishing until it has — and a
  group's children, and an `async let`'s, are awaited before their scope
  ends, so none outlives it. `Task.detached` is the one escape. (Planned: the
  type system enforcing it too — a child task's handle cannot escape the
  `withTaskGroup` scope, which falls out of root inference.)
- Cancellation is a flag per task, set down through its children, which runs
  the task's cancellation handlers and wakes a sleep it is in; the task stops
  where it asks.
- Priorities are inherited by children and order the run queues, by band.
  A task waiting for a lower-priority one escalates it, what it waits for
  and its children, so it does not invert; one waiting for an actor
  escalates the task holding it.
