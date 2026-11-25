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
  metadata — what metadata made at run time is cached by (#297 stage 6):
  an instance code compiled once makes for a type known only at run time
  is made then, once, and equal to the one emitted statically for the same
  arguments.
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
T as its one type argument — then T's metadata, then a pointer to T's
witness table for each of its protocols, one for `any P` and one per part
of `any A & B` (#299, Swift's layout):

```
any P  =  [ box* | metadata* | witnessTable_P* ]      (+ one table pointer per extra protocol)
             └─> heap object any$box$of$T { header, value: T }
```

Its size does not grow with the protocol: three words for `any P`, four
for `any A & B`. Copying an existential shares the box. A call through it
**opens** it: the value's address in the box (just past the header,
aligned as its metadata says: `box_project`), the entry for the
requirement loaded from the table (`witness_generic:i`), and the call
given the address, the arguments and the metadata — the same entry, in the
same convention, that unspecialised generic code calls. A requirement that
changes the value (a setter, a mutating method) is given its address after
the box is made the existential's own (`box_project_unique`,
`sable_box_unique`): when another existential shares the box, a new box of
the same class with a copy of the value, made by its value witnesses,
takes its place. A call is two dependent loads — the table, then the
entry — where the slots of the old layout were one; an existential built
where its type is known holds constant tables, so a call through it in the
same function is a direct call of the witness (Simplify's
`DirectWitnesses`), which LLVM can inline, and a witness inlined reads the
value from the box's field (`FoldBoxProjections`).

A member of an extension of the protocol, which is no requirement, has no
entry: a call of it on an existential opens the existential as a type known
only by its metadata — its value where the box holds it, its tables those
the existential holds — and calls the member compiled once, as on a type
parameter's value (SE-0352). So does a requirement whose result (or a
get-only property whose type) is `Self` or one of the protocol's associated
types: the entry writes the result to a buffer its metadata sizes
(`opaque_alloc`) — an associated type's metadata read from the table
(`witness_associated:i`) — and the value is boxed again by its type's boxer,
as the existential of `Self` or of the protocols the associated type is
constrained to (SYNTAX.md "Opening an existential"). An async requirement's
entry is an async function in the same convention, awaited as any async
call; one is not made yet for an async requirement that takes or gives a
value of `Self` or an associated type other than `self`, since a suspension
would have to keep the caller's storage. A witness is referenced from its
table, so AsyncLowering keeps it async even where it never suspends.

So does a call of generic code given existentials for its type
parameters, each of which one parameter takes as itself and nothing else of
its signature names but its result: `twice(e)` for `fn twice<T: P>(_ x:
T)`, and `pair(a, b)` for `fn pair<A: P, B: Q>(_ a: A, _ b: B)` with both,
run the callee compiled once with each value's address, metadata and
tables, so requirements that mention `Self` or an associated type
(`x.same(x)`) are callable on it; a result of one of them is boxed again by
the type's boxer. A type parameter two parameters name, or one named in a
`T?`, a `T[]` or another type made of it, in an `inout`, in a result or an
error other than as itself, or in a `where` clause, is not opened. A
specialised build compiles such callees once from the first call that needs
them, as a call that opens a Type does.

**Witness tables.** Each conformance a program needs — a type put in an
existential, a type conforming to a protocol cast to, a type unspecialised
code is given — has one witness table, `SableWitnessTable`, which T's
metadata's conformance record for P points at: T's associated types'
metadata and what each must conform to (by index and protocol, found in
the registry from that metadata), the conformer's tables of the protocols
P refines directly, in the order it names them (`bases`), and then,
inline, one entry per requirement P itself declares.

```c
typedef struct { const SableProtocol *protocol;
                 int64_t associated_type_count; const SableTypeMetadata *const *associated_types;
                 int64_t associated_conformance_count;
                 const SableAssociatedConformance *associated_conformances;
                 int64_t base_count; const SableWitnessTable *const *bases;
                 int64_t requirement_count; void *const requirements[]; } SableWitnessTable;
```

The entries are every method, operator, property getter and setter and
subscript P itself declares, in declaration order, in one calling
convention — the one unspecialised code calls them in, and a call through
an existential too: `self` (the address of its storage, for a `mutating`
one) unless the requirement is static, its parameters, the address a
result of `Self` is written to, the address an error of an associated type
is written to, and `Self`'s metadata last; a value of `Self` or of one of
its associated types (`next() -> Element?`, `index(after: Index) ->
Index`) is passed by the address of its caller's storage. A requirement
whose error is of an associated type, `throws(Failure)`, writes the error
where its caller says and throws that address: the witness of a conformer
whose `Failure` is `Never` never writes it. A requirement that convention
cannot pass yet (generic itself, an initialiser, async with a value of
`Self` or an associated type other than `self`, or `Self` inside another
type than an optional or a tuple) has an empty entry, and a call of it
through an existential is not lowered (S7000). Each witness
(`$generic$witness$T$P$i$name`) is the requirement lowered as if its body
called itself on the conforming type, so it is met as code that knows the
type meets it: the type's member — through its vtable for a class a
subclass may override — a default, a builtin. That is the one function per
requirement: the existential-convention slots, their thunks and the thunks
bridging the two conventions of before #299 are gone, in specialised builds
as in unspecialised ones.

A type that conforms to several instances of a generic protocol has a table
for each, known by the protocol's name and the instance's arguments
(`M.Handles$of$Int`). What an extension of a generic protocol adds, used on
an existential of an instance of it, is the copy made for the existential as
`Self`: code compiled once is given no tables of generic protocols.

An instance of a generic protocol, `any Producer<Animal>`, holds the table
of the conformer's own instance, `Producer<Dog>`: its entries take and give
what the existential's entries are declared to, as an upcast of a class
reference (and an optional of one) is the same bits. A table is made in
full whatever needed it first, so that every unit's copy of it is the
same. Which records a type has follows from its declarations alone (a
built-in type's from Sable.Core's extensions); which tables a unit makes
depends on what its own code boxes and casts. So in a program built in
units a record's `witnesses` is null, every unit's copy of it the same as
`linkonce_odr` requires, and each unit's constructor hands the runtime the
tables it made (`sable_register_witness_table`), which the lookup consults
after the records; a program compiled whole points its records at its
tables and registers nothing. (Swift keeps such records in a section the
linker gathers; a constructor works on every target Sable has.)

**Casts.** `e as? Square` asks the box's class whether it is that type's
box (or, for an object, a superclass's box, and then the object). `e as?
any Q` and `e is any Q` are registry lookups: the metadata of what the box
holds — an object's own class, which may be a subclass of the one boxed
(`sable_box_value_metadata`) — is asked for Q's witness table
(`sable_witness_table`, each part's for `any A & B`), and the existential is
the same box, shared, with that metadata and the tables. `animal as? any
Named` boxes the object as its static class and asks the same. An instance
of a generic protocol, `any Producer<Animal>`, which a conformer to
`Producer<Dog>` may fill by variance, is cast by trying each conforming
type of the program in turn, with its own table. `any Error` put in `any
Debug` holds the tables of `any Error`'s own conformances, whose entries
call the slots of the error object's vtable.

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
  `x as! U` from one type parameter or associated type to another (a range's
  bound made a collection's index, `lowerBound as! C.Index`) is the same
  value, seen as the other, when the two metadata are the same, and a trap
  when not.
  A requirement is called through the table's generic entry; one of a
  protocol a given one refines, through the table's bases.
- `"\(x)"` asks the runtime for the **describer** of the type
  (`sable_opaque_describe`): a function, made for each type whose metadata
  code that knows the type hands to unspecialised code and registered as
  the image starts, that writes the value out as an interpolation in code
  that knows its type does. `"\(x:?)"` asks for its **debug describer**
  (`sable_opaque_debug_describe`), made beside it, which writes the value
  as `?` in code that knows its type does; a whole program keeps the debug
  describers only when something asks for one.
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

**A library's generic code, compiled once into its archive** (#297 stage
7). Built unspecialised, the standard library's unit — and a registry or git
dependency's — compiles every generic function of its files that can be
compiled once, whether or not its own code calls it, defines each for every
unit linked with it, and records them in its interface (`.sbi`): each
function's name, and whether it writes out a value of a type it is given
(which tells a caller to register the type's describer). A unit compiled
against the library — the program's, a dependency's after the library — takes
that record as the library's decision: it calls those functions in the
archive, binding and lowering none of their bodies, and lowers again only
the generic functions the library left to be specialised
(`SilUnit.Libraries`, `CompiledGenerics`). The interface's SIL is the
library's code other units may copy: its **inlinable** generic functions —
marked `@inlinable` or `@inline(always)`, or of at most
`SilGen.InlinableSize` (120) SIL instructions and not `@inline(never)` —
with the closures, thunks and instances they refer to, and the code of the
classes they name. An optimised build copies the ones it calls into its
unit (`LibraryInterface.LinkInlinable`) before the specialisation pass,
which makes its copies of them for the unit's types, as it does of the
unit's own generic code; a larger one is called in the archive, generic,
as Swift calls a library's code that is not `@inlinable`. An optimised
single-file build is compiled whole, the library with it, and specialises
everything. The build cache keys a library's entry, a project's objects and
the record that the library checks cleanly by the generics mode, and an
interface records it, so a specialised build never links an unspecialised
archive.

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
of its indices is, and set as a mutating method of its indices and the new value: the table's entries hold its getter, and its setter for one `{ get set }`, in its
place among the protocol's requirements; a settable property's setter is an entry after its getter.

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
(`M.Pair<String, Int>`) as one made where its arguments are known. In
an extension or a function with `where T == X` the parameter is that type.

A **generic class** is compiled once as a struct is, the
reference passed as it is; any method may change self's fields. One whose
stored fields hold its type parameters' values only behind references —
arrays and slices of them, plain generic structs that do (`[Key: Int]`),
other such classes — is laid out alike whatever its arguments (`Stack`,
`Queue`, `Deque`, `OrderedDictionary`). One with no deinit may hold them by
value too, laid out as an optional's or a tuple's fields are: its fields are
where the class's metadata says (`sable_field_address`). That metadata is
made at run time from the fields' and the type arguments' metadata
(`sable_class_type`, from the class's shape) and kept for each set of
arguments — its object metadata sized for them, with a destroy and a visitor
that go through each field's value witnesses — and its **initialisers** are
compiled once with it: the allocating half makes the object from that
metadata, and is called, never made where it is called. So that an object
unspecialised code makes is of the same class as one made where its types
are known, a program built unspecialised registers each generic class's
metadata it writes as it starts (`sable_register_generic_class`), and the
runtime gives that record for those arguments: a class's metadata is its
identity to `is` and `as?`. A **deinit** is compiled once too: a program
registers, by the class's name, a function that reads the type arguments
from an object's class and calls it (`$template$deinit$…`,
`sable_register_generic_deinit`), which a class made at run time runs before
its fields go. A class
in a **hierarchy** — with a superclass, or one a program inherits from
(src/Sable.SIL/SilGen.Hierarchies.cs) — is too: its superclass is made from
its own type arguments (`sil_superclass`, an accessor the class's shape
names), and its vtable's slots are in the convention of the class that
declares the method, so that a call through the base, whatever it knows of
the types, passes what any override takes; an override whose types are
fixed (`class Names: Box<String>`) has a thunk in its slot that converts, as
a reabstraction thunk does. A method generic over its own parameters that a
subclass may override is not (below). An
**operator** a generic type declares is a static member of it. A
**subscript**'s getter and setter, of a generic struct,
union or class, are compiled once as methods are (a dictionary's `d[key]` and
`d[key] = v`). A static member of a generic type that names none of its type
parameters (`Task.sleep`, `Task.isCancelled`) is generic over its own alone,
and takes none of its type's metadata.

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
it is thrown untouched; its own `throw error` of that type writes the error
there. One that throws `any Error` — a `rethrows`
function, `rethrows(Failure)` among them, throws that — takes such an
error in a slot of its own and boxes it as `any Error` with the type's
**eraser** (`opaque_erase_error`, `sable_opaque_erase_error`): a function
made where the type is known, with its table of the conformance to
Error, and registered with the runtime as a describer is. So `map`,
`filter`, `reduce` over any sequence are one function each. An error of
one caught inside unspecialised code is taken from a slot of its own: a
`do` whose catches take it whole, and a `try?`; with another pattern, or
mixed with other errors, it keeps the function specialised.

**Closures.** A function type of a type parameter's values, `(T) -> U`,
`(Element, Element) -> Bool`, `(inout Value) -> Void`, takes the generic
convention: each value of a type held indirectly is the address of the
caller's storage, borrowed, and a result of one is written where the
caller says, after the parameters (`@thick (@opaque(T), *@opaque(U)) ->
()`). A `rethrows` function's closures throw `any Error`, so one copy
serves closures that throw and those that do not, as specialised. One that
throws an error of a type parameter, `() throws(E) -> T`, is given where the
error goes after where the result does, and throws that address. A
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
closure captures by value (a `let`, a parameter, its own `self`), and may be inside
another; a `var` it shares is in a box that is an instance of a generic class,
laid out at run time as any is. One that changes a value's `self` is
given where it is, as any closure that does not escape is; a function that
gives back a closure over its type parameters gives it in the convention of
its own, which a caller that knows the types converts. An async closure's
parameter and result go as an async function's do.

A function **stored** in a field of a generic struct or class is kept, in an
unspecialised build, in the generic convention of the type's own parameters
(`@thick (@opaque(__Element)) -> …`), whatever the instance's arguments, so
that code that knows them and code that does not agree on the field: what
reads it makes of it the function the code has there — a cast, where only
the parameters' names differ, or a reabstraction thunk — and what writes it
converts likewise (`SilGen.StoredFunctionType`). So the Lazy adapters and
`PriorityQueue` are compiled once. A specialised build keeps such a function
in the convention of the instance's types, and calls it directly; so does an
optimised unspecialised one (`SilGen.UseOptimised`), where those types' code
is copied for each instance, as the specialisation pass would leave a
conversion at every use. A payload that is a function, and an optional
function, are kept and converted likewise.

A **requirement generic over its own parameters** (`relative<C:
Collection>(to: C)`) has an entry that takes, before `Self`'s metadata, the
metadata of each of them and the tables their constraints name; its witness
is compiled once for them. A conformer whose member cannot be has an entry
that traps, and a caller that knows the conformer calls a copy instead.

**Async functions.** An async function compiled once is split as any is
(§6.7): its start function takes the metadata and tables with its
parameters and keeps them in the frame, where its resume and drop functions
find them by name each time they run. A parameter of a type parameter's type
stays where its caller holds it — the caller's frame, which outlives the
call — and the frame keeps its address. Its slots of such a type, and such
values live across an await, are buffers after the frame's fixed part, on
the task's slab with it: the start function sizes each from the metadata
as it makes the frame, all zeros, and the field holds the buffer's address.
A value borrowed from storage is borrowed again after an await rather than
kept. A task dropped part-way destroys what its frames hold through their
metadata, as a frame of known types does. The specialisation pass copies an
async function as any other where its types are known.

**Opening a Type** (#297 stage 6; SYNTAX.md "Opening a type"). A call
that gives a `Type` for a generic function's `T.Type` runs the function
compiled once, the value's metadata as `$metadata.T`. Its tables are the
registry's: `witness_table_required:M.P` asks `sable_witness_table` for
each protocol `T` must conform to, and traps with the type's and the
protocol's names (`sable_witness_table_required`, at the call) when there is
none. A result of `T` is written to a buffer its metadata sizes, all zeros
(`opaque_alloc`, `sable_opaque_alloc`), and boxed as the existential of
`T`'s protocols by the type's **boxer** (`opaque_box`,
`sable_opaque_box`): a function made where the type is known that copies
the value into the box an existential keeps one of its type in
(`any$box$of$T`, so `as? Meters` finds it), registered with the runtime as
the image starts, as describers and error erasers are; the value in the
buffer is then destroyed and the buffer freed, or, when the call throws,
only freed. An optional of `T`, or an array, is erased by a helper
compiled once for the existential (`$erase$optional$…`, `$erase$array$…`),
given `T`'s metadata, which boxes each value. The existential holds the metadata and the tables, as `as? any
Q` makes them. The registry finds only the
tables a program makes, so a call that opens a Type makes, for every type
of the program that conforms to `T`'s protocols, its table with its generic
entries, its boxer when the result is erased, and its describer when the
callee writes a value out; an instance of a generic type made at run time
has its declaration's **template** tables (src/Sable.SIL/SilGen.Templates.cs),
one per conformance for every instance, registered by the generic type's name
as the image starts (`sable_register_template`): each entry reads the type arguments from `Self`'s metadata
(`metadata_argument`), finds the tables the conformance requires of them in
the registry, and calls the witness compiled once for the generic type. They
are made for the protocols a program opens a type as or casts to, whole or
not at all: a conformance with a requirement the convention cannot pass or
whose member is not compiled once has none, and the opening traps. Where the
protocol, or one it refines, has associated types, which differ per instance,
the template has a function for each that gives its metadata from `Self`'s
(`$template$associated$…`), and the registry makes the instance a table of
its own the first time it is asked, with them asked and its bases the
instance's; one bound to an array, an optional or a tuple of type parameters
that must itself conform has no template. A value of such an instance is
boxed in a class the runtime makes for it (`sable_opaque_box`). In a program
built in units, one unit may make the instance and another open it: the unit
that opens a type as a protocol, or casts to it, makes the templates of every
generic type of the program that conforms. An entry for a requirement generic
over parameters of its own is compiled once for those too: their metadata and
tables come among the type arguments'. A witness that asks for the conformance
itself — a requirement left to the protocol's default, whose `Self` it is —
takes it to have the template being made.

**Arrays, slices and optionals** conform by their extensions, where the type
they are of does. Their metadata, made alike in every unit and as the program
runs, has no records: a module that asks the registry gives it the list
(`sable_register_structural`: the kind, the protocol, what is required of the
one argument), which a lookup of such a type consults after its records.
Their tables are templates as a generic type's are, known by the kind (`[]`,
`[:]`, `?`), and the one a program makes for a type it names is registered
for it, in a whole program as in units. Tuples, functions and enums conform
to nothing but what an enum declares.

A **static member of an `any P.Type`** is one of the type the value holds,
opened as `P`'s `Self` for the one use: a requirement through the registry's
table, a member of an extension of `P` compiled once.

A specialised build compiles generic code once too, from the first call
that opens a Type: such a call, and the code compiled once it reaches,
call the callee's `$generic` instance — made from the body in this unit,
since a specialised library archive has none — while code that knows its
types still calls the copies made for them. A function that only forwards to a requirement,
otherwise left specialised, is compiled once when a call opens a Type for
it; one that cannot be is S3164, with what in its body keeps it copied.

A **cast of an existential to a type only its metadata knows**, `x as? T` and
`x as! T` in code compiled once, asks the runtime (`existential_cast`,
`sable_existential_cast`) for what the box holds: one of that type, or of a
class it derives from, is copied out by its value witnesses. To an
existential it is asked of the type's **caster**: a function made, as a cast
written there is, where code that knows the existential hands its `T.Type`
to code compiled once, and registered as boxers are; or, with none, of the
registry. A cast to an instance of a generic protocol, `any Repo<Order>`,
tries each type of the program that conforms, and each generic type's
instance whose arguments that says (`Sql<Order>`, for `Sql<T>: Repo<T>`).

**A generic type without its type arguments** is a value too: `Stack.self` is
a `GenericType` — its name, how many type arguments it takes, and a function
that makes its metadata from theirs (src/Sable.SIL/SilGen.GenericTypes.cs) as
`Stack<T>.self` does in code compiled once, after asking whether each meets
its constraints. The type arguments of a type are its metadata's, those of
an existential of a generic protocol's instance too.

Inside such code, a type made of a type parameter as a value — `T[].self`,
`T?.self`, `(T, Int).self`, `Box<T>.self` — is its metadata made at run
time from `T`'s (`sable_array_type`, `sable_enum_type`, `sable_tuple_type`,
`sable_aggregate_type`), cached by its parts, so a generic type is made
for a type known only at run time. Its name is its parts' names, as the
metadata of the same type written out has it (`M.Box<M.Label>`, its
arguments qualified), and `==` holds the two to be one type.

A value of a type parameter's type becomes an existential — of the protocols
it is known to conform to, or `any Any` — in a buffer of its own that its
type's boxer boxes (`opaque_box`), or, of one that is an existential already,
as the box it holds; `x as? any P`, `x as? Dog` and `x is P` ask the box or
the value's dynamic type (`opaque_type`). A cast to a struct, an enum, a
number or a string, `key as! String`, compares the metadata first and copies
the value out where it is (`opaque_copy_as`), boxing it only where it may
hold another's: an existential, an optional, a union. A specialised copy
keeps the side its types take, and drops the other. Raw memory's values
(`RawMemory.load`, `take`, `initialize`, so `RawPtr`, `OnceCell`, `Mutex` and
the concurrent collections) and `Runtime.share` go through the value
witnesses. `Never` given for an error's type parameter has a table with
nothing in it, as nothing is ever called through it; `Sendable` has none, and
no function takes one. A generic type made of type parameters, `Pair<T>`,
given where its conformance is required, has the conformance's template table
("Opening a Type", below), found from the metadata made for it.

A **structural union** of type parameters, `Failure | E`, is opaque as an
optional of one is: its metadata is made at run time from its members', in
the order their names have, with a member said twice once
(`sable_union_type`), and a value goes in, and is asked which it holds, by
the member's metadata (`union_inject`, `union_case_metadata`,
`union_case_payload`). An **array, slice or optional** of type parameters
given where a conformance is required (`xs.map` of a `T[>..]`) has the
template table of the extension that conforms it ("Arrays, slices and
optionals", above); so has a structural union of errors, known by `|`, whose
entries ask which case a value holds and call the case's own. **`Task<T>`** is one class whatever its
result (src/Sable.SIL/SilGen.Tasks.cs): its body writes the result where the
task object keeps it, as its metadata lays it out.

**What is compiled for each type in both modes, and why.** These are
decisions, not gaps: a generic function that meets one is specialised for
its type arguments in the same program, so a program builds either way, and
`SABLE_GENERICS_TRACE=1` says, for each, which way it went and which of
these it met (`2` also says each instance a unit makes).

- *Value parameters, and what is laid out by them*: `SIMD<T, N>`,
  `SIMDMask`, `ArrayVec<T, N>`, `SmallVec`, `T[=N]`. Their layout — a
  register, a frame's bytes — is what they are for; code that took it from
  metadata would be slower than the array it stands in for. A search of an
  array by vectors is among them, but every type it can be given is known
  (the SIMD scalars), so the library's archive has them all (**prepared**
  instances, `SilUnitInfo.Prepared`, listed in its interface), and a program
  makes none.
- *Move-only types* (`~Copyable` parameters, `@noncopy`): a value is never
  where two owners could read it, which the convention — the caller keeps
  what it passes — is made of.
- *A type with a layout or a deinit of its own rules*: a struct with a
  deinit, `@packed`, `@align`, `@inline_layout`, a union with an `indirect`
  case.
- *Generators*: the iterator is a struct of the body's own slots, laid out
  inline, and which there are differs with the type.
- *A constraint on an instance of a generic protocol* (`FromSequence<E>`,
  `Validator<V>`, `some Sequence<Int>`), and the members of a generic
  protocol: a table's entries take the protocol's arguments as they are, so
  there is one per instance.
- *A method generic over its own parameters that a subclass may override*:
  a vtable slot is one function, and what it would be asked for is not known
  where the subclass is compiled.
- *What is written per type by the compiler*: `Mirror` and `Reflect`,
  `Error`'s description by default (`"\(self)"`: it is the type's own
  describer), and a forwarder that only calls a requirement, which is
  smaller than the call it would take.
- *A value made of type parameters written out* (`"\(xs)"` of a `T[]`), a
  `catch` that mixes a type parameter's error with others, a type test of a
  value only its metadata knows against a type made of type parameters, a
  union that mixes type parameters with known types, a local generic
  function over the type parameters around it, `@polymorphic`, and a
  requirement that takes an array or a generic type of `Self` `inout`: each
  would need a part of the runtime of its own, for what is seldom written
  and compiled once per type at no loss but that.
- *In an optimised build*, stored functions, tasks and class hierarchies: its
  types' code is copied for each instance and specialised, and one kept in
  the generic convention would be converted at every use.

Measured on the standard library as a debug build's archive: 1324 generic
functions compiled once and 119 for each type (346 and 418, with some that
have no body, before the list above was done: 99 of either are the value
generics), and a program of the core's collections — arrays, dictionaries,
searches, sorts, lazy and zipped sequences — lowers none of their code
(CompiledGenericsTests); `examples/ssg` makes no instance (six before). A specialised
build (the default) makes no describers but for a call that opens a Type
or an existential.

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

// Requires @reflectable(callable): its public members, called with boxed arguments
let doc = try Document.callable("init")!.call("Untitled")
let result = try type(of: doc).callable("render")!.call(on: doc)

// The types that ask for it and conform to a protocol — the plugin story
let plugins: Type = (any Plugin).self
for type in plugins.callableSubtypes() {
    let plugin = try type.callable("init")!.call() as! any Plugin
}

// Attribute reading
for route in t.attributes(of: Route.self) { }  // Route: a struct conforming to Attribute
```

### Calling by reflection

`@reflectable(callable)` conforms a type to `Callable`, whose three
requirements the compiler writes as it writes a derived member
(src/Sable.Symbols/DerivedBodies.Callable.cs): one describes each of the
type's public initialisers, methods and operators (`CallableMember`: name,
signature, parameter and result `Type`s), one calls the member of an index,
and one the members whose call waits — the async ones, an actor's and the
main actor's, which hop as written code does. An argument is taken out of
its box as the parameter's type, the receiver as the type's; one that is not
it is a `CallError` thrown, and what the member throws is thrown on. A
mutating method called on an `any Any` in a variable changes it.

- *Opt-in, and only public members.* A type is asked for by its author, and
  what is called by name could be called by any code, so access control
  holds without knowing who asks. A type that does not ask has no
  descriptor, no thunk and nothing registered: a program in which none does
  is byte for byte what it was. `--deny-reflection` refuses it (S3450).
- *Described, and refused with a reason* (`refusal`): a member generic over
  its own parameters, one with an `inout` or variadic parameter, of an
  extension that holds only of some instances, an abstract class's
  initialiser, one whose parameter or result no box holds (a move-only
  type, a view, a function), and one isolated to an actor whose parameters
  or result are not `Sendable`, which the boxes would hide. Each argument is
  given: a default value is bound at a call, where there is none.
- *Found by type.* `T.callableMembers()` where `T: Callable`, a generic
  type's instance among them; `type(of: x).callableMembers` whatever `x` is,
  from what each image registers as it starts (`sable_register_callable`:
  a type's metadata and the function that gives its members).
- *Listed by protocol.* `Type.callableSubtypes()` asks the conformance
  registry of each type registered (`sable_metadata_is_subtype`), so the
  types a program lists are those that asked to be kept: no linker flag or
  scan of every type, and a generic type, which is no one type, is not
  among them.

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

**`Runtime.lookupType(String)` would force conservatism** (it is not built;
a program that loads by name looks among `callableSubtypes()`):
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
and Swift's `libswiftCore`. Its declarations come from the entry too, so that
building a small program reads none of the library's source. The source stays the unit of distribution — for
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
  tables (UnicodeTables.sb, generated by tools/unicode-tables.py),
  StringBuffer and StringWriter (a string written in place), the
  array family, Comparable/Equatable/Hashable, MemoryLayout, unsafe
  pointers, Iterator/Sequence and the Collection family (Bidirectional,
  RandomAccess, Mutable, RangeReplaceable), FromSequence, and the
  sequence algorithms, eager and behind '.lazy',
  Formattable/FormatSpec (what "\(x:spec)" writes through),
  Parsable/ParseError/NumberStyles (text back to values), and
  RandomNumberGenerator with the system's generator and seeded ones
  (xoshiro256**, PCG XSL RR 128/64, ChaCha20, SplitMix64; the same stream on
  every platform), behind 'random(in:)' (Lemire's integers, evenly spaced
  floats), 'randomElement', 'shuffle', 'sample(count:)' and 'fill(bytes:)';
  portable SIMD: SIMD<T, N> and SIMDMask<N>, LLVM's vectors
  Platform (the target's constants, and at run time the operating system
  and architecture, OS version and isAtLeast, processors, page size,
  memory, host and user names, newline) and MemoryStatistics

Sable.Collections   List Dictionary Set Deque OrderedDictionary
                    PriorityQueue BitSet InlineArray SmallVec ArrayVec,
                    SortedDictionary SortedSet (B-trees, range queries),
                    LinkedList (an arena of nodes, constant-time handles,
                    order-maintained comparable positions),
                    FrozenDictionary FrozenSet, ImmutableList
                    ImmutableDictionary ImmutableSet
                    ImmutableSortedDictionary ImmutableSortedSet (below)
Sable.Text          UTF-8/16/32, ASCII and Latin-1 codecs, validated
                    (UTF-8 sixteen bytes a step) and transcoded a chunk at
                    a time (OperationStatus, Utf8Decoder, Utf16Decoder);
                    normalisation
                    (NFC, NFD, NFKC, NFKD); grapheme clusters, words and
                    sentences (UAX #29) and line breaks (UAX #14), each
                    passing the UCD's conformance tests, from tables
                    tools/unicode-tables.py generates (Unicode 16);
                    formatting, Regex (Rust's and RE2's syntax, Unicode
                    classes and case folding, compiled to an NFA: a lazy
                    DFA forwards for a match's end and backwards for its
                    start, a PikeVM for its groups — linear in the text;
                    a backtracker on request, for back references and
                    lookaround; a Pattern, so String's searches take one;
                    RegexSet),
                    TextBuilder (a UTF-8 buffer and chunks: characters and
                    integers encoded in place, edits that cut one chunk,
                    replace in one vectorised pass; at parity with .NET's
                    StringBuilder); Base64 (standard and URL-safe, padded
                    or not, streamed by Base64Encoder/Base64Decoder),
                    Base32 (and base32hex) and Hex, RFC 4648, strict, a
                    DecodeError naming the offset, encoded in place
Sable.Globalization Locale (BCP 47; current, invariant), numbers, money,
                    dates and times written and read by one, PluralRules,
                    ListFormatter, Collator (UCA, the CLDR's root and
                    tailorings, sort keys), from CLDR tables that
                    tools/locale-tables.py generates: the same everywhere.
                    No locale unless one is given (docs/09)
Sable.Math          the elementary functions (sin, exp, pow, gamma, …),
                    generic over ElementaryFunctions; Complex<T> with
                    them all (Kahan's branch cuts, signed zeros, one point
                    at infinity); integer math; the
                    vector kernels over F32[:] and F64[:] (sum, dot,
                    cosineSimilarity, softmax, …: TensorPrimitives');
                    Decimal (.NET's layout and rounding, below);
                    BigInt (an I64 inline, limbs past it; schoolbook,
                    Karatsuba and Toom-3 products, Burnikel–Ziegler
                    division, text in any radix by halves; pow, powMod,
                    gcd, isqrt, two's-complement bits, bytes),
                    statistics generic over every number type (mean,
                    median, mode, quantiles, variance, covariance,
                    correlation, histogram; compensated and two-pass;
                    RunningStatistics by Welford, mergeable),
                    and distributions over any generator (uniform, normal,
                    exponential, Bernoulli, weighted index): here rather
                    than beside the generators, as the core has no
                    functions of them
Sable.Math.Geometry Vector2, Vector3, Vector4, Matrix3x2, Matrix3x3, Matrix4x4,
                    Quaternion, Plane and Transform of F32s, Int2 to Int4,
                    Vector3D (F64); Ray, BoundingBox, BoundingSphere,
                    BoundingFrustum and their tests; Half2, Half4, UNorm8x4
                    and the like, PackedQuaternion (below)
Sable.IO            Reader Writer Seekable (and async ones), FileHandle and
                    OpenOptions (positional reads and writes, metadata,
                    setLength, sync), BufferedReader BufferedWriter
                    MemoryStream TextReader TextWriter, File (bytes, text,
                    lazy lines, copied, renamed, moved, linked, metadata,
                    permissions and times), Directory (create, list, lazy
                    entries, walk, glob, copy, remove, recursively too),
                    Glob, Temp (files and directories removed as they are
                    let go), FileWatcher (below), PathLike (a String or a
                    FilePath), FilePath (platform bytes, POSIX and Windows rules:
                    drives, UNC, verbatim; components, lexical
                    normalization, relative paths, canonical) and Path
                    over it, MappedFile (read-only, read-write and
                    copy-on-write mappings as a U8 array, flush, advise,
                    resize), Pipe with PipeReader PipeWriter (pooled
                    segments, ByteSequence views, consumed/examined,
                    backpressure), the standard streams as handles,
                    OsString and OsStr (the system's own text, whether or
                    not it is UTF-8), Environment (variables and arguments
                    as text and as OsString, set and removed under
                    'unsafe', home, temporary and known folders by XDG,
                    Apple and Windows rules)
Sable.IO.Compression Deflate Zlib Gzip Zstd Brotli (below), all at once, as they come
                    (Compressor, Decompressor) and as readers and writers
                    that compress, async ones too, with limits on what is
                    decompressed
Sable.IO.Archives   ZipArchive (random access by the central directory,
                    Zip64) ZipWriter (to anything, with descriptors) Zip
                    (create, extract, update), TarReader TarWriter (old,
                    ustar, pax and GNU headers) Tar; async writers and a
                    reader; extraction that stays in its directory, with
                    limits (below)
Sable.Net           Url (WHATWG URL parsing, as browsers do: WPT's
                    urltestdata.json passes whole; resolution, setters,
                    origin, query items), Host (domain, IPv4, IPv6,
                    opaque), PercentEncoding with the standard's encode
                    sets, application/x-www-form-urlencoded (QueryItem),
                    Idna (UTS #46 toASCII/toUnicode, tables from
                    tools/idna-tables.py) and Punycode (RFC 3492);
                    IpAddress (strict parsing, RFC 5952, scopes, IANA's
                    special-purpose registries), SocketAddress, IpNetwork;
                    TcpListener TcpStream, UdpSocket (multicast),
                    UnixListener UnixStream UnixDatagramSocket; Dns,
                    HappyEyeballs (RFC 8305), NetworkInterface (below).
                    Not yet: Tls Http1 Http2
Sable.Serialize     Serializable (encode(to:), static decode(from:)) for
                    the primitives, UUID, optionals, arrays, Set, String-keyed
                    dictionaries, Duration, UtcDateTime and JsonValue;
                    @derive(Serializable) with @serialize (§6.5), written
                    against Encoder and Decoder, protocols on serde's data
                    model (null, Bool, integers, floats, strings, bytes,
                    sequences, maps, structs, union cases) that any format
                    can conform to. JSON (RFC 8259; JSONTestSuite passes
                    whole): JsonReader, a token at a time with no tree, over
                    text, bytes, a Reader, or an AsyncReader's bytes as they
                    come (JsonReader.streaming: it stops anywhere in a token
                    and goes on), comments, trailing commas, depth, a key
                    twice and values one after another by ParseOptions,
                    strings and white space scanned 16 bytes a step; and
                    JsonWriter, compact or indented, flushed to a Writer, an
                    AsyncWriter or a ByteBuffer, its structure checked in a
                    debug build. On them JsonValue's parser and printer,
                    JsonEncoder, and JsonDecoder over a one-pass index of
                    the document, every error at its path ($.items[3].name);
                    Json.encode/decode. TOML 1.0: Toml, TomlValue,
                    TomlEncoder and TomlDecoder, and TomlDocument, which
                    edits a text and keeps its comments and layout.
                    MessagePack: extensions and timestamps. CBOR (RFC 8949):
                    tags, indefinite lengths, CborConformance (strict, the
                    deterministic encoding, length-first), and CborReader a
                    token at a time. Each bounds depth and checks
                    a declared length before making anything of it. A type
                    asks `encoder as? CborEncoder` for what one format alone
                    has. Not yet: the zero-copy @inline_layout path
Sable.Serialize.Yaml YAML 1.2: YamlParser (events, linear in the text,
                    errors by line and column) and YamlEmitter, YamlNode
                    (the core schema; aliases within an expansion budget;
                    no types from tags; a key twice is an error),
                    YamlStream (edited with its comments and formatting
                    kept, unedited byte for byte), YamlEncoder and
                    YamlDecoder, Yaml.encode/decode
Sable.Serialize.Asn1 ASN.1 (X.690): AsnReader and AsnWriter by BER, CER
                    and DER, as .NET's, for every universal type (REAL, the
                    ISO 8601 times, RELATIVE-OID and the IRIs too), tagged
                    implicitly or explicitly, and values in OCTET and BIT
                    STRINGs; sets put in order, and checked to be; a nested
                    reader shares the bytes; AsnTag, AsnBitString,
                    ObjectIdentifier (128 numbers of 128 bits), AsnError at
                    its byte
Sable.Concurrency   Channel and ThrowingChannel (unbounded, or bounded with
                    a full mode: wait — the sender's backpressure — or
                    drop oldest, newest or the write; close(error:);
                    cancellable sends and receives), Broadcast, Watch,
                    Oneshot, AsyncStream and AsyncThrowingStream (make,
                    buffering policy, onTermination), merge, debounce,
                    throttle, Mutex RwLock Condition Semaphore Barrier
                    CountdownLatch Once Lazy, AsyncMutex AsyncSemaphore,
                    Thread (spawn, scope, park, blockOn), JoinHandle,
                    ThreadLocal, TaskLocal, parallelFor parallelInvoke
                    parallelForEach, an array's and a range's parallel,
                    parallelModify parallelSort (§6.7),
                    ConcurrentDictionary (sharded)
                    ConcurrentQueue (two-lock) ConcurrentStack,
                    CancellationTokenSource/CancellationToken (handlers,
                    linked, cancel(after:), withCancellation), Promise,
                    Continuation (checked; a leak traps in debug),
                    withTimeout, race, whenAll/whenAny/whenEach,
                    Task.sleep(for:), Timer, Interval, TestClock; each, and
                    debounce, throttle and cancel(after:), on any Clock
                    (Task, TaskGroup, WaitList, AsyncSequence with map,
                    filter, compactMap, prefix, chunked, reduce,
                    first(where:), collect and zip, Atomic<T> and OnceCell
                    are Sable.Core's: an Atomic's cell is Core's raw memory)
Sable.Time          Instant Duration (checked, saturating, ISO 8601),
                    Clock: MonotonicClock SuspendingClock SystemClock;
                    Stopwatch, measure; UtcDateTime (the moment),
                    Date TimeOfDay LocalDateTime (civil), UtcOffset
                    OffsetDateTime, TimeZone (IANA, TZif, POSIX rules)
                    ZonedDateTime, Period, Weekday Month, Disambiguation;
                    .NET custom/standard patterns, RFC 3339, RFC 2822,
                    ISO 8601, RFC 9557
Sable.Process       Command (arguments, environment, directory, streams;
                    spawn, output, status), Child (piped streams, wait,
                    tryWait, kill, kill(tree:), signal), ExitStatus, Signal,
                    Signals (on, handle, raise), Process (run, exit, abort)
Sable.Hosting       ApplicationBuilder and Host (services started in order,
                    run side by side and stopped in reverse within a
                    timeout; signals; failure policies), HostedService,
                    ApplicationLifetime, recurring jobs (CronExpression,
                    intervals, overlap policies), Systemd (sd_notify)
Sable.DependencyInjection
                    @injectable and @container (a graph checked and made as
                    it is compiled); ServiceCollection (singleton, scoped,
                    transient; keyed, open generics; checked as it is built),
                    ServiceProvider, ServiceScope
Sable.Validation    Validator and Rules over key paths, FieldRules' built-in
                    and custom rules, rule sets, MessageCatalog,
                    @derive(Validatable), ValidationResult, ProblemDetails
                    (RFC 9457), validatedOptions
Sable.Crypto        HashFunction and Digest: SHA-2, SHA-3, BLAKE2b, BLAKE3,
                    Insecure.Md5 and Sha1; SHAKE; Hmac, KMAC; Hkdf, Pbkdf2,
                    Scrypt, Argon2id; PasswordHash (PHC strings); AesGcm,
                    AesCcm, ChaCha20Poly1305, XChaCha20Poly1305
                    (AuthenticatedCipher, SealedBox); AesKeyWrap;
                    Hazmat.AesCbc, AesCtr, AesCfb and AesEcb;
                    SymmetricKey, SecureRandom, constantTimeEquals,
                    secureZero; Ed25519, ECDSA (P-256, P-384, P-521) and RSA
                    (PSS, PKCS #1 v1.5) signatures, X25519 and ECDH, keys in
                    PEM, DER (SPKI, PKCS #8, PKCS #1, SEC 1) and JWK
Sable.Crypto.X509   Certificate, CertificateVerifier (RFC 5280 paths, name
                    constraints, host names, revocation lists), TrustStore
                    (the system's), CertificateBuilder, CertificateRequest
Sable.Diagnostics   Logger (structured), tracing spans, metrics, Backtrace
Sable.Testing       @test @bench; assert, assertEqual and the like; expect and
                    require, which show their expression's parts; expectThrows;
                    expectPanic and expectExit (death tests); assertSnapshot;
                    forAll, Arbitrary and Gen (property tests); Fixture and
                    FixtureLifetime, TestContext, skip; Bencher,
                    BenchStatistics, MannWhitney, BenchComparison
Sable.Interop       C's types by name (CInt, CLong, CChar, CSize, CWChar),
                    CStr, CString, withCString, withWideCString,
                    withCStrings, CFunction and cFunction, CCallback,
                    withCallback, DynamicLibrary (open, symbol, function,
                    variable), OwnedFd, OwnedHandle (Windows)
Sable.Reflect       the reflection surface described above
```

### Benchmarks

A benchmark (`@bench`, docs/05 §5.1) takes a `Bencher` and gives its `iterate`
what to measure; without one, its whole body is timed:

```sable ignore
@bench(arguments: [64, 65536], tags: ["hash"])
fn digest(_ b: Bencher, _ size: Int) {
    let bytes = U8[>..](repeating: 7, count: size)
    b.bytesPerIteration = size
    let last = b.iterate { Sha256.hash(bytes) }
    b.check("\(last.bytes[0])")
}
```

- `iterate(body)` and `iterateAsync(body)` give what the body returned last.
  What it returns is kept from the optimiser by `blackBox`, as the body is;
  a body that does the same thing to the same input many times over should
  put a `blackBox` in its own loop too, or link-time optimisation may work a
  pure function out once.
- `iterate(setup:_:batch:cleanup:)` makes each iteration a value of its own,
  given `inout`, `batch` of them at a time, untimed.
- `bytesPerIteration` and `itemsPerIteration` add a throughput;
  `operationsPerIteration` divides what is reported by what the body does.
- `check(text)` says what was computed: the same wherever it is run.
- `BenchStatistics` (Tukey's fences, Student's t), `MannWhitney` and
  `BenchComparison` are the arithmetic, of any samples; `BenchSchedule` which
  runs there are to be, without a clock.

### Tests

A test (`@test`, docs/05 §5.1) is written with `Sable.Testing`:

- `assert`, `assertEqual`, `assertEqualText` and `fail` end it, saying why.
  `expect(condition)` fails it and goes on, so that one run says all that does
  not hold; `require(condition)` fails it and ends it, and `require(optional)`
  gives the value there is. Both say what the condition was written as and
  what each part of it was, and where the sides of an equality differ:

  ```
  expectation failed: names.count == 3 (NamesTests.sb:12)
      the values differ
         left: 2
        right: 3
               ^ first difference at character 0
      names: ["Ada", "Grace"]
  ```

  That is a parameter's doing whose default is `#expression` (SYNTAX.md §18):
  the argument is worked out once, as it would be, and may throw and await.
- `expectThrows(E.self) { … }` gives the error thrown, `expectThrows(error)`
  wants that very one, `expectThrows(saying: "text")` one that says that,
  `expectThrows { … }` any, `expectNoThrow` none; each has an async form.
- A `Fixture` is what tests are run with: `setUp` and `tearDown`, which may wait
  and throw, and how long one lives — `.PerTest`, unless it says `.PerSuite`,
  `.PerCollection` or `.PerPackage`. A `@suite`'s initialiser asks for the
  fixtures it needs as parameters, a `@test` function as its own, and a fixture
  those it is made from, which must live as long. (No container is asked: a
  fixture is made by its one initialiser, and one that wants Sable.DependencyInjection
  builds a provider in `setUp`.) A suite that is a Fixture has its own around each test.

  A shared fixture is an object in memory, so the tests that share it run in one
  process, one after another, for as long as it lives: nothing is kept alive
  between processes, and nothing needs to be. Each is torn down after the last
  test that shares it, the last made first; a test that failed, threw, was
  skipped or was cancelled for its time makes no difference, and what a teardown
  throws fails the last of them. A trap cannot be unwound: it — a failed
  `assert` among them — has the fixtures torn down on another thread while the
  trapped one waits, five seconds at most, and the rest of the tests run in
  another process with fixtures of their own. A process that is killed, or past
  its time and deaf to being cancelled, tears nothing down.
- `TestContext.current` is the running test's: its name, tags and which try it
  is, the time it has left, a `cancellation` token for what takes one, `log` and
  `attach`. What a test prints is kept apart from any other's, and shown when it
  fails. (Nothing overrides the locale or the clock for a test: what depends on
  one takes a `Locale` or a `Clock`, and is given `Locale("tr")` or a `TestClock`.)
- `expectPanic("text") { … }` and `expectExit(status) { … }` run their body in
  a process of its own, the test run again as far as there, and give how it
  ended. Where none can be started — `--in-process`, the interpreter, WASI —
  they are passed by, and say so.
- `assertSnapshot(value, named:)` compares with
  `__snapshots__/<file>.<test>.<name>.snap` beside the test's file.
  SABLE_UPDATE_SNAPSHOTS=1 (`sable test --bless`) writes it; one that is not
  there is a failure.
- `forAll { (xs: Int[>..]) in … }` tries a body with a hundred arbitrary values,
  small to large, the same each run unless a seed is given (docs/05 §5.1), and
  shows the simplest it does not hold of, with the seed that makes it again. A value is made from `Choices`, numbers drawn at random
  and kept; a failing one is shrunk by its choices rather than its value (as
  Hypothesis does), so `Arbitrary` is one function, no `shrink` beside it, and
  what `map`, `flatMap` and `filter` make shrinks too. `@derive(Arbitrary)`
  makes a struct of its fields, an enum's case and a union's of its payload,
  coming to an end when it holds its own kind. The built-in types, optionals,
  arrays, dictionaries, sets, ranges and `Outcome` are Arbitrary; `Gen` is for
  anything else (`Gen.number(in: 0...9).array(count: 1...5)`). A body that
  traps is shrunk by running the test again, a process for each case.
  `Choices(bytes:)` makes a fuzz target's values of its input, each choice
  of as many bytes as it needs: a `@fuzz` function may take any `Arbitrary`
  values (docs/05 §5.1).

### Dictionaries and hashing

`[Key: Value]` is `Sable.Core`'s `Dictionary<Key: Hashable, Value>`
(`lib/Sable.Core/Dictionary.sb`), a struct written in Sable that the
compiler knows only by name: `[K: V]` binds to it (its key checked for
`Hashable` as `Dictionary<K, V>` written out would be), `["a": 1]` calls its
`init(dictionaryLiteral:)`, `d[k]` its subscript, and `for (k, v) in d` goes
through its `Sequence` conformance like any other type's. There is one hashed
map; `Sable.Collections.HashMap` is gone, and `Set` (a value, with union,
intersection and the subset tests, which use the hashes it keeps) is a
dictionary to nothing underneath.
`InlineArray<T, N>` is the built-in `T[N]` by that name, and `SmallVec<T, N>`
keeps `N` elements inline in a `T[..N]` before it moves to the heap.

- **Layout.** An array of entries (key, value, the key's full hash) in the
  order keys were added, none where one was removed, and beside it an index:
  a power of two of `I32` slots, at most half of them used, each empty (-1),
  left by a removal (-2), or the entry's position with bits of its hash above
  it, as many as the position leaves of 31: four bytes a slot, so that the
  index of a large dictionary is in cache twice as often. A probe starts where the hash's bottom bits say and moves
  one slot at a time, comparing the stored hash bits before any key —
  SwissTable's metadata filter, without its SIMD groups: at most half full,
  a probe looks at a slot and a half on average. Growing drops removed entries and rebuilds the index from
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
(`math_sin_F64`, a call of libm's `sin` that LLVM may neither fold nor
rewrite, so a constant operand gives the bits the interpreter's call of the
same libm does, at every optimisation level), and its free
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

### Frozen and persistent collections

Two kinds of collection that do not change, in `Sable.Collections` (#26).
Both are values. A list that is made once and read is neither's: that is an
array, `T[>..]`, which is .NET's `ImmutableArray` and, while it is made, its
builder.

**Frozen.** `FrozenDictionary` and `FrozenSet` are made once, from a sequence,
taking longer over it than `[Key: Value]` does, and are then only looked up.
Their order is the order their keys were given. A key finds its place by a
strategy chosen as the table is made (`FrozenTable.sb`):

| Keys | Found |
|---|---|
| at most four | one after another |
| integers, and `Char`s, in one run | by subtracting the first |
| integers close together (ten slots for each key, at most) | in an array from the least to the greatest |
| integers spread out | in an open-addressed table, hashed by one multiplication |
| strings whose lengths all differ | in an array by length |
| strings some eight bytes of tell apart, counted from the start or the end | in such a table, hashed by those bytes and the length; a length no key has is not looked for |
| strings the first eight and the last eight tell apart | the same, by both |
| any other strings | the same, by all of their bytes |
| anything else | in such a table, by their own `hash(into:)` under a seed |

The multiplier, or the seed, is the one that moves the fewest keys from their
own slots, of as many as 64 tried, so that a lookup compares one key. The
hashes are the same in every run, which lets a table be made while compiling
and means that its keys, though not what is looked up, must be trusted.
`FrozenDictionary(ignoringAsciiCase:)` folds ASCII's letters eight bytes at a
time; `FrozenHashingDictionary` and `FrozenHashingSet` take any `Hashing`.
A `Substring` is looked up by its own bytes. Which strategies there can be is
known by the key's type where that is known: the tests of `Key.self` are
constants, and code compiled once for every type asks them of the metadata.

A **`const`** of one is a table in the program's read-only memory, with
nothing to do as it starts: the initialisers are `const init`, the table is
made in the compiler's interpreter, and its arrays are written as constant
arrays are (`array_literal`). A constant with more than 256 elements in it is
kept in a global, stored once as the program starts, and any other is written
wherever it is read. A `static let` is made as the program starts, as ever.

**Persistent.** `ImmutableList`, `ImmutableDictionary`, `ImmutableSet`,
`ImmutableSortedDictionary` and `ImmutableSortedSet` share with the copies
made of them all but what is changed: each keeps a tree, and a change copies
the few nodes on the way to it.

- **Nodes are structs of arrays**, not objects, and so are shared, changed in
  place and counted as any array is: an array nothing else holds is changed
  where it is, and one something does is copied first. So a list, or a
  dictionary, that is held once is built and changed in place by its
  `mutating` methods, with no builder beside it (a `var` is one); and one that
  is shared, or crosses to another task, has the path copied. They are
  Sendable when what they hold is, and nothing about them is unsafe. A lookup
  goes down without counting a reference: the element a lent array's element
  is of is read in place across a call (`TryBorrowedElement`), as what is
  lent cannot change.
- **`ImmutableList`** is a relaxed radix balanced tree of 32 ways
  (`ListTreeNode.sb`): an index is five bits a level where the nodes before
  are full, and looked for among the node's sizes from there where they are
  not. Nodes but those at the right edge are kept half full, as a B-tree's
  are, so that cutting, joining, inserting and removing anywhere are O(log n)
  and leave it balanced. The last 32 elements at most are beside the tree, so
  that appending is an array's.
- **`ImmutableDictionary`** and **`ImmutableSet`** are a compressed
  hash-array mapped trie (CHAMP, `HashTrieNode.sb`), five bits of the hash a
  level, the hashes kept. Its shape is the keys' alone: equality compares
  nodes in step and passes over those the two share, and `union`, `merge`,
  `intersection`, `subtracting` and `changes(from:)` take time by what is not
  shared. Keys whose whole hashes are equal are in a node of their own, and
  looked through. The order is by hash: the same for equal dictionaries, but
  not from one run to the next.
- **`ImmutableSortedDictionary`** and **`ImmutableSortedSet`** are such a list
  in order of the keys, searched by the last element of each node: a range of
  keys is cut out of it, sharing the nodes.

### SIMD

`SIMD<Scalar, N>` (`lib/Sable.Core/SIMD.sb`) is N lanes of an integer of
up to 64 bits or a float (F16 and BF16 included; `SIMDScalar` says which),
N being 2, 3, 4, 8, 16, 32 or 64 (a `where` the type checks, S3421). The
compiler knows it by name, as it knows `Dictionary`: it lowers to SIL's
`@simd(T, N)` and to LLVM's `<N x T>`, a value held in vector registers —
16 bytes of them on every target Sable has (SSE2, NEON;
`Platform.vectorBytes`, and `T.preferredVectorWidth` lanes of each type —
WebAssembly is compiled without simd128, so there LLVM splits a vector into
its lanes);
a wider vector is split across several. `SIMDMask<N>` is N Bools,
`<N x i1>`, what comparisons give and `select` chooses by.

Its members are the SIMD builtins (`simd_…`, `src/Sable.SIL/Simd.cs`),
whose signatures follow the vectors they are given and are checked where
each is used. A lane-wise one is the scalar builtin of its name in every
lane — `simd_add` on `@simd(I32, 4)` is `add_I32` four times — and means
what it does: an integer's `+`, `-` and `*` trap if any lane overflows
(`&+`, `&-`, `&*` and `saturatingAdd` do not), a division traps on a zero
divisor in any lane, a shift on a count outside the width, as the scalar
operators do; a float's follow IEEE 754. Natively each is the vector
instruction or intrinsic, the overflow of a checked one reduced to one
trap; the interpreter computes each lane with the scalar builtin, so the
two agree by construction. The rest move lanes (`swizzled`, `concat`,
`lowHalf`, .NET 11's `zip` and `unzip`), load and store them from arrays
(one unaligned vector load or store, the index checked as an element's),
give a mask as bits (`mask.bits`, x86's movemask, with `firstTrue`,
`lastTrue` and `trueCount` over them),
look bytes up in a table of them (`table.lookup(indices)`, NEON's tbl;
where `Platform.hasByteLookup` is false, a lane at a time),
see their bits as other lanes (`reinterpreting`),
convert them as `unchecked as` does (never trapping: wrapped, saturated,
rounded), and reduce them: an integer's sum wraps, and a float's is added
pairwise — lane i with lane i + N/2, down to one — in that order natively
and in the interpreter alike, so that a sum is the same number either way.

`Sable.Math`'s kernels (`VectorKernels.sb`) are `TensorPrimitives`' static functions, as .NET's are,
written once over `SIMD<T, N>` and a scalar tail — `sum`, `dot`, `norm`,
`cosineSimilarity`, `min`, `max`, `softmax` and the elementwise `add`,
`subtract`, `multiply` and `divide` into a destination — and given 64
bytes a step for F32 and F64 (16 and 8 lanes: four registers, four sums
in flight). A store into an array reaches it inline when the array is its
buffer's only reference, as an element's write does, and a loop of stores
makes it so once, before the loop (`HoistUniqueness`). Measured on an Apple
M-series machine (arm64, NEON): a dot product, a sum and a multiply over
4096 F32s, 50,000 times, take 0.05 s against 0.41 s as plain loops
(`benchmarks/programs/kernels`); the six kernels of
`benchmarks/library/tensor-primitives` take 0.06 s of processor
time, start-up included, against 87 ms for .NET 11 with
System.Numerics.Tensors 9.0's TensorPrimitives, steady state. `softmax` works
its exponentials out a vector at a time too (`VectorExponential.sb`), within
two units in the last place rather than as libm's. F16 and BF16 kernels are
still to come.

**The library's own hot paths** (#229) are loops over `SIMD` too, each with
its scalar loop for what is left — the last step taken over the last
elements again where that is as good — and for saying where bad input is,
and each held to that loop by tests of every length from 0 to 130, from
several starts, at every position:

- arrays of integers and floats: `contains`, `firstIndex(of:)`,
  `firstIndex(ofAny:)`, `lastIndex(of:)`, `count(of:)`
  (`Array+VectorSearch.sb`), `reverse`, and the integers' `sum`, `min` and
  `max` (`Array+Aggregates.sb`); `==`, `<`, `commonPrefixLength`,
  `starts(with:)` and `ends(with:)` of any elements that are equal when
  their bits are, which the runtime compares as bytes (`array_mismatch`: an
  element's type is known as it is compiled, or by its metadata);
- `Hex`, `Base64` and Base64Url both ways, ASCII case mapping and
  comparison, UTF-8 to and from UTF-16;
- in the runtime's C: substring search, UTF-8 validation, counting a
  string's scalars, and a byte's search and count.

Sable code is compiled for the processor's baseline (SSE2, NEON), and a
function marked `@vectorised` for AVX2 as well
(`src/Sable.CodeGen/LlvmEmitter.Vectorised.cs`): its name is then a function
that calls the one this processor can run, which the runtime has asked it
once (`sable_processor_known`). Array searches, sums and extremes are, a bit
set's count and `softmax`. SSE2 has no byte shuffle, and the runtime's C is
dispatched too where that is what the work is: Base64 and Hex in blocks (`runtime/codecs.c`: SSSE3
or AVX2, or NEON's table lookups) and the check that UTF-8 is well-formed
(SSSE3), each asked for as the program runs. So are the kernels of
`TensorPrimitives` (`runtime/vectors.c`), SHA-512 and a byte's count, which
have forms for AVX2 that give the same bits; `SABLE_PORTABLE=1` says the
processor has no more than the baseline. UTF-8 and UTF-16 are put in each
other there too. What those leave, and the interpreter, WebAssembly and an
older processor, is the Sable code's, which comes to the same.

A string made a byte at a time is made where it will be, by `StringBuffer`
(.NET's `string.Create`) and `StringWriter`, which appends to one: case
mapping, normalization, replacing, percent-encoding and URLs, `TextBuilder`.
Both are written through checked stores, and unsafe only to make and to
finish. What a text needs of Unicode is two-stage tables in string literals
(`tools/unicode-tables.py`), read eight digits at a time
(`string_utf8_load_U64`). Against .NET 11 on an Apple
M1 Max (`tools/bench/compare.py`, processor time, the median of 7 runs on a
busy machine, Sable's over .NET's; "before" is the same without these):

| benchmark | before | now |
|:--|--:|--:|
| array-search | 6.02 | 0.99 |
| array-aggregates | 5.27 | 0.54 |
| byte-search | 1.05 | 1.06 |
| hex | 2.78 | 0.72 |
| base64 | 4.25 | 1.22 |
| utf16 | 2.76 | 1.32 |
| utf8-validate (as it was) | — | 0.30 |

### Compression

`Sable.IO.Compression` is written in Sable, not bound to zlib. A codec works
in memory of its own, at addresses, which nothing checks; its callers' bytes
are copied in and out of it a run at a time, while they are in the cache
(`NativeMemory.copy`, to and from a `U8[:]`). What keeps every access in that
memory is the code around it: margins at both ends of a buffer, every length
and distance checked against them. The interpreter, which checks each access,
runs the codecs in `CompressionTests`.

- **Inflate** decodes by tables of eleven bits of a literal's or length's
  code and eight of a distance's, with subtables for longer codes, from a
  64-bit buffer refilled without a branch, up to three literals at a time,
  matches copied sixteen bytes at a time. Near the end of its input or of its
  room it decodes a symbol at a time, and puts back what it had not all of.
- **Deflate** has levels 0 to 9: nothing; one look at the last place with the
  same four bytes; chains of such places, the first long match taken (2 to
  4), or a better one awaited for a byte (5 to 7) or two (8); and at 9 the
  parse of least bits, by what the symbols took in the pass before, three
  times. Blocks end where their symbols change. Its output is not zlib's,
  byte for byte.
- **Zstd** decodes a block at a time: its literals' four Huffman streams at
  once, five bytes of each a step, through one table of eleven bits; then its
  sequences, each read and done. It takes dictionaries (raw bytes, and those
  `zstd --train` makes), skippable frames, and windows up to a limit of 128
  MiB unless told otherwise; memory grows as a frame's bytes come, not by what
  its header says. The encoder has zstd's levels, 1 to 22 and the fast ones
  below 0, and their parameters: hash tables (1 to 4), rows of the last places
  with a hash and eight more bits of it beside each (5 to 13), a tree of
  places and the parse of least bits over a whole block (14 to 22). It does
  not split blocks by their content, nor write several at once.
- **Brotli** decodes by tables of eight bits, the bits in variables across a
  whole run of commands. The static dictionary, its transforms and its context
  tables are RFC 7932's, made into a source file by
  `tools/brotli-dictionary.py`, packed, and unpacked when first needed. The
  encoder has qualities 0 to 11 over the zstd encoder's match finders (at 10
  and 11 its parse of least bits, by zstd's costs rather than brotli's), and
  codes of its own: a literal's chosen by the bytes before it, contexts alike
  sharing one (from 5), by the best of up to four modes; a distance's by the
  copy's length. It does not split meta-blocks into block types, and uses two
  of the dictionary's transforms.
- A decoder says how much it consumed, exactly, so what follows a stream can
  be read; a gzip decoder reads every member. `DecompressionLimits` bound
  the length and the ratio.
- CRC-32 is the processor's where it has the instructions
  (`runtime/checksums.c`: ARM's, or PCLMULQDQ), eight bytes by tables else.

Against .NET 11's, which are zlib-ng, zstd 1.5.7 and brotli, all C, on an
Apple M1 Max that was busy (the best of three runs; MB/s, and the ratio):

| | Sable packs | .NET packs | Sable unpacks | .NET unpacks |
|:--|--:|--:|--:|--:|
| dickens, gzip 6 | 58, 2.631 | 69, 2.616 | 619 | 370 |
| mozilla, gzip 6 | 90, 2.720 | 85, 2.638 | 541 | 452 |
| dickens, zstd 1 | 277, 2.381 | 349, 2.392 | 827 | 1136 |
| dickens, zstd 3 | 180, 2.771 | 186, 2.773 | 722 | 864 |
| mozilla, zstd 3 | 311, 2.772 | 295, 2.786 | 840 | 1132 |
| dickens, brotli 5 | 45, 3.086 | 44, 3.014 | 391 | 438 |
| mozilla, brotli 5 | 56, 3.029 | 53, 3.109 | 294 | 415 |

Of the rest: zstd's levels 5 to 12 are at two thirds of its speed for the same
ratios, 14 to 19 at about its own, within 1.5 % of its ratio; gzip 9 is 4.6 %
smaller than .NET's and a third as fast; brotli 11 is 2.6 % (dickens) and
6.5 % (xml) larger than the reference's, and slower. Dictionaries trained are
as good as `zstd --train`'s.

### Archives

`Sable.IO.Archives` reads and writes zip and tar.

- **Extraction stays in its directory.** A name with "..", a root, a drive or
  a zero in it fails, with "/" between its parts or a backslash; on Windows,
  and where `portableNames` asks, so do devices' names and those Windows
  changes. Each directory on the way to a file is looked at, not through
  links, before it is entered, and a file is made anew (`createNew`) or
  replaces what was there, never written through. A symbolic link is made
  only if its target, worked out by its text, is inside; a hard link only of
  a file that is. Set-id bits are dropped. Limits bound the entries, each
  one's bytes and all of them; a program that swaps directories meanwhile is
  not defended against.
- **Zip** takes what the central directory says, and refuses an entry whose
  own header says another name or method, entries that overlap (the bombs
  that are a thousand times their size are made so), and bytes that are more,
  fewer or others than said. Names are UTF-8 where they say so, or are so on
  Unix, else code page 437. A time is the extended field's, in UTC, or
  MS-DOS's, as if it were.
- **Tar** refuses sparse files, and a header's records of more than a
  megabyte. What ustar has no room for is written as pax's records, or as GNU
  tar's, as the format asks. Nothing is said of owners as files are made.
- Not there: encryption, archives in parts, other methods of zip's, and
  zlib-wrapped or brotli tars known by their bytes.

### Geometry

`Sable.Math.Geometry` is what System.Numerics has of vectors, matrices,
quaternions and planes, and the bounds and rays a game engine asks with them.
Its conventions are stated once, here, and each is asserted by
`tests/lib/Sable.Math.Geometry/ConventionTests.sb`:

| | |
|---|---|
| Handedness | right-handed: X × Y is Z |
| Up, forward | +Y, -Z (+X is right) |
| Vectors | rows: a point is moved as `v * m` |
| Storage | by rows: `m11 m12 m13 m14 m21 …` |
| Translation | the last row, `m41 m42 m43`: bytes 48 to 60 |
| Composition | `local * parent`, left to right, of quaternions too |
| Angles | radians, counter-clockwise with the axis pointing at one |
| Euler angles | yaw (y), pitch (x), roll (z) of the body, in that order |
| Quaternions | x, y, z, then the scalar w; `q` and `-q` turn alike |
| Depth | 0...1, reversed: the near plane is 1, the far plane 0 |
| Winding | counter-clockwise seen from a plane's front |
| Planes | `normal · p + d` is 0; the front is where it is above 0 |
| Textures, pixels | from the top left, y down |

A matrix of row vectors by rows is, byte for byte, the matrix of column vectors
by columns that does the same, which is what GLSL, HLSL and SPIR-V read by
default: it is uploaded as it is. .NET's Matrix4x4 is the same bytes.

- **Where .NET differs.** .NET's projections run depth forward; here they take
  `depth: .Forward` for that. .NET's `Quaternion *` is Hamilton's product,
  which turns by the right first; `a * b` here is its `Concatenate(a, b)`. A
  vector too short to have a direction normalises to zero, and a quaternion to
  the identity, where .NET's are NaN. `inverted()` is an optional, where .NET
  gives a matrix of NaNs and false; so is `decomposed()`, whose mirror is in
  the scale's x where .NET's is in the greatest. `slerp` and `nlerp` hold the
  amount to 0...1. `Plane.transformed(by:)` normalises. `viewport` is .NET's
  left-handed one, the one that keeps depths as they are. There is no
  left-handed camera.
  For all else the two are near each other, not the same bits: .NET fuses and
  estimates as its processor can. `DotNetTests.sb` holds them to a relative
  2e-5 on results that .NET gave (`tools/conformance/geometry/reference.cs`).
- **The same bits everywhere.** What is made of `+`, `-`, `*`, `/` and the
  square root is the same on every processor, at every level of optimisation
  and in the interpreter: nothing is fused, estimated or reassociated
  (`DeterminismTests.sb` has the bits). So is `slerp`, which has a sine and
  an arc cosine of its own for that. The exceptions are what takes the
  platform's sine, cosine or tangent: rotations and quaternions of an angle,
  a quaternion's `angle`, skews, and the perspective of a field of view.
  A simulation that must agree across machines makes those once, or not.
- **Equality** is exact and IEEE's (`-0 == 0`, a NaN equals nothing), hashes
  agree with it, and what is near is `nearEqual(_:tolerance:)`: relative above
  1 and absolute below.
- **Layout.** Plain structs of their components, aligned to 4 as C's: 8, 12,
  16, 24, 36 and 64 bytes. `@inline_layout` is not for them (it makes a counted
  block of a value); a struct is already its bytes. They are not aligned to 16, so that
  an array of Vector3 is a tight vertex buffer and a field costs no padding;
  loads of four lanes need no alignment on either processor. std140 and std430
  align a `vec3` and a `mat3`'s columns to 16, which is the buffer's padding
  to lay out: a `Vector4` or a `Matrix4x4`, or an `@align(16)` struct around
  the field. `withComponents` views an array of them as `F32`s and `withBytes`
  as bytes, with no copy; `simd` and `init(_: SIMD)` go to and from a register.
- **Scalars, and F64.** The types are of `F32`, not generic: generics cost
  nothing specialised, but a build compiled once for every type
  (`SABLE_GENERICS=unspecialised`) would call an addition through a witness,
  and every signature and message would say `Vector3<F32>`. What an engine
  wants in `F64` is positions in a large world, which is `Vector3D`, and
  `relative(to:)` makes them `F32`s from a camera; rotations, scales and the
  matrices then made are `F32`.
- **Speed.** The arithmetic is written out by component, which LLVM makes the
  vector instructions of (as fast as `SIMD<F32, 4>` written by hand, and
  quicker in the interpreter); only `slerp` takes lanes itself. Slices are
  moved by `transformPositions(_:into:)` and its kin. Nothing allocates but
  `corners` and `toArray()`, which are arrays: `corner(_:)` is not. See
  `benchmarks/library/{matrix-multiply,matrix-invert,transform-points,quaternion-blend,frustum-cull,vector-particles}`.
- **Others' conformances.** Serializable is in Sable.Serialize and Arbitrary in
  Sable.Testing, so that a program of vectors has neither module.

### Decimal

`Sable.Math.Decimal` is exact base-10 arithmetic for money and quantities,
and it takes .NET's `decimal` layout rather than IEEE 754-2008's
`decimal128`: a 96-bit coefficient, a scale of 0 to 28 and a sign, in one
`U128` laid out as `decimal.GetBits` gives it (`bits`, `fromBits`). Why:

- **The data it meets.** Base-10 numbers come from accounts, prices and
  databases, and there the layout is .NET's — `System.Decimal`, and
  `rust_decimal`'s, which copied it. Reading one is a copy, and every
  value of one is a value of the other; `decimal128`'s 34 digits and its
  exponents to ±6144 are a scientific type's, and F64 and BigInt are there
  for science.
- **Every value a number.** `decimal128` has NaNs (quiet and signalling),
  infinities, a signed zero and many encodings of one value (cohorts);
  each is a case for comparison, hashing and every caller. A Decimal has
  none of them, so it is `Comparable` and `Hashable` without asterisks and
  overflow traps as an integer's does. Its zero has no sign.
- **Cost.** A coefficient is a U128, so a sum or product at one scale is
  an integer instruction or two; 192 bits are used only for a product, or
  a scale raised more than nine places, that needs them, and a division
  is U64 steps wherever the coefficient fits 64 bits. BID's decimal128
  needs 113-bit coefficients unpacked and a 34-digit product's 226 bits
  at every multiplication. Against .NET's `decimal` on the same work
  (`benchmarks/library/decimal`: a million invoice lines
  multiplied, rounded to the cent, summed and divided three ways) it is
  1.26 times .NET's time — most of the difference the 128-bit divisions of
  long quotients, which compiler-rt does in software.

Rounding is .NET's too: what does not fit 28 places or 96 bits is rounded
to nearest with ties to even, quotients with no end take every place that
fits, and `rounded(scale:_:)` takes any `RoundingRule` (to nearest with ties
to even by default, as `Math.Round`). A value keeps its scale — 1.20 and
1.2 are equal and hash equal, but write themselves differently — and
`quantized(scale:_:)` and `normalized()` are the General Decimal Arithmetic
specification's `quantize` and `reduce`. Parsing reads digits, a point and
an exponent and rounds what does not fit; formatting writes every place of
the scale, plainly, and takes a format spec as a float does (Sable.Core's
`FormatSpec.formatDecimal`). Its tests run cases of the specification's
examples and decTest files, at a decimal's precision.

### Clocks and durations

`Clock` (`Sable.Time`) is Swift's: an associated `Instant: ClockInstant`,
`now()`, `sleep(until:)`, which a cancellation ends, and `sleep(for:)`, with
a default. Code that waits or measures takes one, generic over it
(`fn retry<C: Clock>(clock: C)`), and everything in the library that does
has an overload that takes a clock beside the one that takes none, which is
the same code with `MonotonicClock()`: a struct with no fields, specialised
away. The clocks are the systems', which differ in whether they count while
the machine sleeps:

| Clock | macOS | Linux | Windows | WASI |
|---|---|---|---|---|
| `MonotonicClock` | `CLOCK_MONOTONIC`: counts | `CLOCK_MONOTONIC`: does not | `QueryPerformanceCounter`: counts | `CLOCK_MONOTONIC`: not said |
| `SuspendingClock` | `CLOCK_UPTIME_RAW` | `CLOCK_MONOTONIC` | `QueryUnbiasedInterruptTime`, to the tick | `CLOCK_MONOTONIC` |
| `SystemClock` | `CLOCK_REALTIME` | `CLOCK_REALTIME` | `GetSystemTimePreciseAsFileTime` | `CLOCK_REALTIME` |

Interpreted programs have one clock for the first two, .NET's `Stopwatch`.
The runtime's timers are on the monotonic clock; the other two clocks sleep
on it and look again.

`TestClock` (`Sable.Concurrency`) is advanced by hand. Its determinism on a
pool of threads rests on `Task.settle()` (`Sable.Core`): the runtime counts
the tasks queued or running (`active`, tasks.c), and a task that settles
parks in a list of its own until that count comes to nothing with the main
thread not running either, which is when nothing can go on. `advance` wakes
one sleeper, settles, and wakes the next. Tasks waiting for a real sleep, a
socket or the blocking pool are not waited for (`waitForSleepers` is for
those).

A `Duration` is an Int of nanoseconds. Its operators trap on overflow, the
`checked` and `saturating` forms do not; by an `F64` the product is made
exactly in 128 bits and rounded once, half to even. Its text
(`1h 2m 3.5s`) is exact and reads back as the same duration; ISO 8601's is
read with weeks and days of exactly 24 hours, and years and months, which
have no fixed length, are refused.

### Dates, times and time zones

`Sable.Time` follows jiff's design. A moment is a `UtcDateTime` (seconds
and nanoseconds since 1970 in UTC; jiff's Timestamp). The civil types,
`Date`, `TimeOfDay` and `LocalDateTime`, are what a calendar and a wall
clock show, with no zone; `OffsetDateTime` is a moment at a fixed offset,
what RFC 3339 and RFC 2822 write; `ZonedDateTime` is a moment in a
`TimeZone`, and follows the zone's rules when it moves. A `Duration` is
exact; a `Period` (years, months, days) is by the calendar, and a month
added to the 31st of January is clamped to the end of February. A local
time that a zone's clocks skip or show twice is made a moment by an
explicit `Disambiguation` (compatible, earlier, later; or
`zonedStrictly`, which throws), and `TimeZone.offsets(for:)` says which
it is (`AmbiguousOffset`).

**`DateTime` is renamed `UtcDateTime`.** Beside four other date-and-time
types, a bare `DateTime` reads as the general one, which in .NET it is
(and its Kind decides what it means); the name now says it is the UTC
moment, which is all it ever was. No alias is kept.

**The time zone database.** `TimeZone.find` reads the system's TZif files
(RFC 8536, versions 1 to 4, the footer's POSIX rule for the time after
the last change): `TZDIR`, then `/usr/share/zoneinfo` and the other places
Unix systems keep them; `TimeZone.system()` is `TZ`'s zone, then
`/etc/localtime`'s (by the id its link names), then `/etc/timezone`'s.
Windows has no TZif database. Its own zones, in the registry, have
Windows' names and at most one rule a year, not the IANA history; the
IANA data it does carry is inside ICU (`icu.dll`, Windows 10 1903 on),
reachable only through ICU's calendar API, one transition at a time —
and that is a runtime layer of its own that could not be tested here.
Embedding a database in the library would put a few hundred kilobytes
in every program that uses a zone, and go stale with the compiler. So on
Windows `find` reads `TZDIR`, then a `zoneinfo` directory beside the
executable (a program that wants IANA zones ships one, as Python's
`tzdata` and Go's `time/tzdata` do), and without either has UTC and fixed
offsets only; `system()` is `TZ`'s zone, else UTC. ICU is where to go
when that is not enough.

**Patterns.** `format(pattern:)` is .NET's custom and standard date and
time format strings, in the invariant culture, and
`tests/lib/Sable.Time/PatternTableTests.sb` holds it to what .NET 11's
DateTimeOffset.ToString writes for each specifier (generated by
`tools/time/dotnet-patterns.cs`); fractions go to nine digits, .NET's
seven. `parse(_:pattern:)` reads what the pattern writes, as ParseExact.
Every type is `Parsable` as ISO 8601 (calendar, ordinal and week dates,
basic and extended, reduced times with a fraction on the last part,
24:00), and `ZonedDateTime` as RFC 9557 (`...+01:00[Europe/Prague]`).

### The file system

`Sable.IO`'s `File`, `Directory` and `Temp` go through the runtime's `fs_`
calls (`runtime/fs.c`, over `platform_fs_posix.c` and `platform_fs_win32.c`).
Times are `Sable.Time`'s `UtcDateTime`: `Sable.IO` imports `Sable.Time`, which
imports nothing and reads its zones through `Sable.Core`'s `Runtime`.

- **Removing a tree** goes by what it has opened, never by a path looked up
  again. On POSIX each directory is opened in its parent's descriptor, with
  `O_NOFOLLOW`, and its entries unlinked in its own; the way up is `..`,
  checked to be the directory come from, so few descriptors are held at any
  depth. On Windows each is opened as a reparse point and deleted by its
  handle, and a directory is held, not shared to be deleted, while what is in
  it goes. A link or junction is removed and not followed, whatever another
  program swaps in meanwhile; what goes or comes meanwhile is looked at
  again, so many times. A path that is empty or a root, that ends in `.` or
  `..` (or what Windows reads as one, `...`), has a NUL or is itself a link
  is refused, by the library and the runtime both. WASI's descriptors have
  no `..`: there it is `.Unsupported`.
- **Copying** clones (`fclonefileat`; `copy_file_range` where the file system
  can), else has the system copy (`fcopyfile`, `copy_file_range`,
  `CopyFileExW`), permissions too. A file there is written over in place; one
  copied to itself is refused before it is emptied.
- **Walking** reads a directory at a time and takes each entry's kind from
  the directory, so nothing is asked of the system but the names. Following
  links, each directory's canonical path is known, and one that is being
  gone through already is not gone into again.
- **Watching** (`FileWatcher`, `runtime/watch.c`) is FSEvents on macOS, which
  is loaded when first used, inotify on Linux, its descriptor an I/O source
  of the reactor's (§6.7), and `ReadDirectoryChangesW` on Windows, heard by
  the thread pool. FSEvents says only where something happened, so the paths
  there are remembered to say what. A file is watched by the directory it is
  in, so that one that takes its place is watched too. What the system drops,
  or 16384 events not taken, is one `.Rescan`. A task waiting does not keep a program
  going once main has returned.
- **The interpreter** makes the same calls on POSIX, through the C library;
  on Windows it makes .NET's, whose recursive removal follows no reparse
  point but is by path.

### Processes and signals

`Sable.Process` starts programs through the runtime's `process_spawn`
(`runtime/process.c`, over `platform_process_posix.c` and
`platform_process_win32.c`; under WASI it throws `Unsupported`), and
`Process.run`, the blocking call, is the same start with its pipes pumped in
place.

- **Starting.** The program is found by the parent, on its own PATH, and run
  by its path: no shell, and nothing looked up again in the child's
  environment or directory. Linux forks and execs, the child making only
  async-signal-safe calls, its failure (no such directory, not a program)
  sent back through a pipe the exec closes; macOS uses `posix_spawn`, whose
  errors come back as they are; Windows `CreateProcessW`, its arguments
  quoted as `CommandLineToArgvW` reads them. A batch file is refused: cmd
  would read its arguments as commands, whatever the quoting.
- **What a child has.** Its three streams and no other descriptor
  (`POSIX_SPAWN_CLOEXEC_DEFAULT`, `close_range`, a handle list), everything
  the runtime opens being closed on exec besides; no signal blocked, none
  handled, SIGPIPE as a program expects it.
- **Streams.** This program's end of a pipe is nonblocking and an I/O source
  as a socket is, so a task reading or writing one suspends (§6.7); Windows'
  are overlapped named pipes whose events the thread pool hears. Both of a
  child's outputs are read at once by `output`, so neither fills. A write to
  a child that has stopped reading throws, and raises no SIGPIPE here.
- **Ending.** A thread per child waits for it without reaping it, then reaps
  it under the child's lock, the lock a signal is sent under: a signal never
  goes to an identifier that may be another process's. A child is reaped
  whether or not it is waited for. `kill(tree: true)` ends the job a child
  was put in before it ran on Windows; elsewhere it stops each process, then
  ends its children and it, which does not reach one whose parent has ended.
- **Signals.** The handler notes the signal and writes to a pipe; a thread
  reads it and wakes the tasks listening. What the signal did before is put
  back when the last of them goes. A task waiting for a signal does not keep
  a program going once main has returned, as one waiting for a socket does.
- **Cancellation.** `wait` throws `Cancelled`, and the child runs on:
  `withTimeout` limits a wait. `output` and `status` kill theirs.

### Dependency injection

`Sable.DependencyInjection` (#81) is Microsoft.Extensions.DependencyInjection
in two layers over the same declarations: `@container`, whose graph is checked
and whose code is written as the program is compiled (the way of Dagger, Jab
and Pure.DI), and `ServiceCollection`, registrations made as it runs.

```sable
@injectable(.Scoped, as: OrderRepository.self)
final class SqlOrderRepository: OrderRepository {
    init(database: Database) { … }
}

@container
struct AppServices {
    @include(Payments.Services.self)
    @singleton fn clock() -> any Clock { return SystemClock() }
    @singleton async fn database(config: Config) throws -> Database { … }
    @keyed("hot") @singleton fn hotCache() -> any Cache { … }
}

let provider = try await AppServices.build()
provider.withScope { scope in
    scope.resolve(OrderService.self).run()
}
```

- **`@injectable`** (a generator, `lib/Sable.DependencyInjection/generators/`)
  writes a class's, a struct's or an actor's factory from its initialiser —
  the one marked `@inject`, of several: `Injectable.make`, called with what a
  `ServiceScope` gives for each parameter, and `dependencies`, what that is.
  An async initialiser's is `AsyncInjectable`'s. With a lifetime, the type is
  also registered: as itself or `as:` what it conforms to, under `key:`. A
  generic type is registered for every type it is asked for as. Nothing is
  called through reflection.
- **Parameters.** `T`, `Lazy<T>`, `() -> T`, `T[]` and `T[>..]` (every
  registration), `T?`, `@keyed("k")`, and a default value for what is not
  registered. Anything else is an error where the parameter is (S3232).
- **`@container`** (the same) is a Sendable struct or class. It has the
  package's `@injectable` types, the fragments it includes, and its own
  functions marked `@singleton`, `@scoped` or `@transient`. Written beside it
  are a provider and a scope, `AppServicesProvider` and `AppServicesScope`, in
  which each service is a function that calls initialisers and functions by
  name. Each is a `Provides<Service>` for what it gives — the provider, what
  needs no scope — and `resolve` is a `ServiceContainer`'s `where Self:
  Provides<Service>`, so asking for anything else does not compile. Keyed
  services are got by `find(_:key:)`, and those with several registrations by
  `resolveAll`.
- **Checked at compile time**, each error with the way to it and where it
  starts (`OrderService → OrderRepository → Database: nothing provides
  Database`): what is missing, a scoped service in a singleton, a service that
  needs itself with no `Lazy` or factory on the way, several registrations
  where one is asked for, a singleton that is not Sendable, and a throwing or
  async maker of anything but a singleton.
- **Effects.** A singleton whose making throws or waits is made by `build`,
  after what it is made from; `build` is `async` and `throws` just as they
  are. So `resolve` does neither. Any other singleton is made the first time
  it is wanted.
- **Fragments.** A package with registered `@injectable` types gets a public
  `Services`, in the module of theirs fewest names deep, so nothing else there
  has that name, a container neither (S3232). `@include(M.Services.self)`
  has a container build them, their graph checked with its own; an error there
  is at the container. A type the package keeps to itself is made by a
  function of the fragment, when it is registered as what is public, of what
  is.
- **`ServiceCollection`** is a value. `add` takes a container, a fragment or
  any `ServiceModule`; `addSingleton`, `addScoped` and `addTransient` a type,
  a closure or a value, `open:` a generic; `tryAdd`, `replace…` and `remove`
  change what was added, as a test does. `build` runs the same checks on what
  the registrations say they are made from, and `ServiceError.Invalid` has
  all that is wrong; `buildAsync` also makes what is waited for. A closure's
  own dependencies are known only `from:` what it says, else as it runs, so
  `resolve` throws a `ServiceError` here.
- **Keys.** A service is known by its `Type` and, for a keyed one, a string.
  It is kept as an `any Any` and given as the type asked for, so one that only
  the running program knows is a service like any other: `resolve(type)`.
- **Open generics.** `addScoped(open: Repository.self, SqlRepository.self)`
  takes two `GenericType`s (§6.2). Asked for `any Repository<Order>`, the
  provider reads the type arguments from its metadata, makes
  `SqlRepository<Order>`'s of the same, and opens it as `Injectable`. The
  cast back to `any Repository<Order>` has the tables of every generic type
  of the program that conforms where its arguments are known, so an instance
  no code names is one all the same. `@container` closes them as it is
  compiled, for the uses there are.
- **Sharing.** A singleton's service is Sendable, so a provider is; a scope is
  not, and what is scoped or transient need not be. Two threads that want one
  singleton wait for one of them to make it.
- **Disposal.** A scope disposes what it made (`Disposable`,
  `AsyncDisposable`), the last first, and then lets go of what it held, as its
  closure ends; the provider its singletons. A container's `withScope` waits
  where it must be awaited; a `ServiceProvider`'s is `withAsyncScope`. What is
  transient is its scope's to dispose; asked of the provider, the provider's,
  as in .NET. A singleton's `Lazy` and factories do not keep their provider.

Over the fifty-service graph of `benchmarks/library/dependency-container`, the
container takes 0.5 to 0.7 of .NET's time, and `dependency-injection`'s
collection 2.3 times it: .NET compiles a graph's code as it runs.

### Validation

`Sable.Validation` is FluentValidation over key paths (SYNTAX.md §16), with
DataAnnotations' attributes as `@derive(Validatable)` (§6.5), which makes the
same rules.

- **A validator says its rules**, `fn rules(_ r: inout Rules<T>)`, and
  `Rules<T>` runs them. `validate` on a validator makes them anew; kept in a
  `Rules` they are made once. A nested validator's are made as the first
  value is checked by them, so a tree's may use itself.
- **Optional fields.** `r.field(\.nick)` of a `String?` gives rules of a
  `String`: where the field is none, only the rules that ask whether it is
  (`notNull`, `null`, `notEmpty`, `empty`, `equal`, `notEqual`) are run.
- **Waiting.** A rule, a condition or a group's condition may be an async
  closure. `validate` waits; `check` does not, and throws `AsyncRuleError`
  where a rule that applies would have to. Rules that cannot wait are run
  without suspending either way.
- **As FluentValidation has it**: `when` after several checks is of all of
  them (`for: .LastOnly` of one); rule sets are run only where asked for, and
  a nested validator is asked for the same ones; a warning is a failure
  (`isValid`), and `hasErrors` says whether there is worse; placeholders have
  its names. A string's length is its scalars, not UTF-16 units.
- **The parsers are the library's**: `url` is `Url`, `uuid` `UUID`, `decimal`
  and `precisionScale` `Decimal`, `matches` `Regex`, an address literal in an
  email address `IpAddress` and its domain `Idna`. There is no mail module to
  ask about email addresses (#227), so `EmailSyntax` has the HTML standard's
  grammar (the default), RFC 5321's with RFC 6531, and FluentValidation's.
- **Messages.** A `MessageCatalog` is asked first, by code and locale; the
  English ones are last. No translations come with the library.
- **What is not here**: binding to a web framework's requests (there is
  none; `problemDetails()` is what one answers with) and registration in a
  container (#81), which holds an `any Validator<T>` and asks
  `Rules(erased:)`.

### Hosting

`Sable.Hosting` is .NET's generic host over what the library has, with
swift-service-lifecycle's ordering: `ApplicationBuilder` (`services`,
`configuration`, `logging`, `environment`, `options`) builds a `Host`, whose
`run` lasts as long as the program does.

- **Services.** `services` is the list of services to run, each made as the
  host is built: by a typed closure from a `HostContext`, or, for a type
  that is `@injectable`, by the builder's `container`
  (`Sable.DependencyInjection`, below), which has the host's environment,
  configuration, logging, lifetime and clock too. The host disposes the
  container once its services have stopped.
- **Seams.** `configuration` is any `HostConfiguration` — a key's text, and
  `reload` — with the environment's variables for the one there is: #80's
  library is to conform to it. `logging` is the settings (`HostLogging`)
  each task makes its own `Logger` from, as a Logger is mutable and not
  Sendable; providers are #83's. All the host knows of time is `HostClock`
  — now, and sleep until — which tests replace, and #61's Clock is to.
- **Starting and stopping.** A service's `start` is awaited before the next
  service's begins, and its `run` is started as it ends. Stopping goes the
  other way: the last service's token is cancelled, its `run` waited for,
  its `stop` run, and only then the one before told. Its token is a request;
  its task is cancelled only when the timeout runs out, so that work under
  way may finish, its waits and sleeps with it (`HostContext.sleep` is one
  the token ends).
- **The timeout.** A child task cannot be let go: its parent waits for it
  (§6.7). So each step of a service is a detached task whose end is a
  promise the host waits for, and which the deadline completes in its
  place; on every other path the host waits for them all, as a group would.
  A service given up on runs on, and a program's end waits for its tasks:
  `runToExit` ends the process all the same, and is how a daemon's main
  ends.
- **Signals.** SIGTERM and SIGINT stop the host, SIGHUP reloads it (or stops
  it, by option; on Windows, where it is the console closing, always). A
  SIGINT while stopping ends the program with status 130 — by the process's
  exit, since what it would otherwise wait for is what is stuck.
- **Failure.** A service whose `run` throws stops the host, which then
  throws it, or is logged and left (`ServiceFailurePolicy`); one that
  returns has ended, and stops nothing.
- **Cron.** Five fields, as Vixie cron reads them, and Vixie's rule where
  clocks change: a job whose minute and hour are both fixed runs once a
  day, as soon as a gap that skipped it is over and the first time in a
  fold; any other follows the clock as it goes. It is computed from the
  zone's transitions, between which local time and elapsed time go together,
  not by trying times. Missed times are not made up for, and a clock set
  back does not hold a job up. A run that falls due during another is
  skipped, queued or run beside it (`OverlapPolicy`).
- **Service managers.** `Systemd.notify` sends a datagram to NOTIFY_SOCKET
  (`process_notify`; a path, or an abstract name on Linux; not vsock), and
  `useSystemd` sends READY, RELOADING, STOPPING and the watchdog's. launchd
  needs nothing but the foreground and SIGTERM. A Windows service is not
  built: its control dispatcher takes over a thread for the service's life
  and its stop request comes to a handler there, which is a runtime layer
  that could not be tested here.

### Cryptography

`Sable.Crypto`'s algorithms are written in Sable, and the same code runs on
every platform and in the interpreter; nothing of the operating system's is
linked. The alternative was CommonCrypto, CNG and OpenSSL: three libraries
whose algorithms differ (none has BLAKE3; SHA-3 and Argon2 depend on the
version), the last of which a Linux machine need not have, and none of which
the interpreter could call. What only they had is speed, which is the
processor's instructions, and those are in `runtime/crypto.c`, for SHA-1,
SHA-256, SHA-512 and Keccak: ARMv8's SHA instructions and ARMv8.2's, x86's
SHA extensions, and for what those do not have AVX-512 or else AVX2 and BMI2
(SHA-512's schedule is worked out two blocks at a time while the first one's
rounds wait on each other; Keccak's lanes are in AVX-512's registers, or in
the ordinary ones with BMI's). ARM's Keccak instructions are used on Apple's
processors only, as Neoverse's do one at a time and the portable code is
twice as fast there. Which the processor has is asked once (sysctl,
getauxval, IsProcessorFeaturePresent, cpuid); a kernel is asked for whole
blocks (`crypto_blocks`) and says whether it did them, and where it did not
the portable loop does. For tests, `SABLE_CRYPTO_PORTABLE=1` says the
processor has none, `SABLE_CRYPTO_NARROW=1` that it has no AVX-512, and
`SABLE_CRYPTO_NARROW=2` that the ciphers have no AVX2 either.
Of the rest, MD5, BLAKE2b, BLAKE3, Argon2id and scrypt are portable only.

The ciphers' kernels (`runtime/ciphers.c`) are asked for whole messages
(`crypto_cipher`), from one slice to another or in place. AES is by AES-NI or
ARMv8's instructions, eight registers side by side, of one block, or with
VAES of two or four; GCM's GHASH is worked in by PCLMULQDQ, VPCLMULQDQ or
PMULL as they come out, eight registers' products added up before they are
reduced. ChaCha20 is by whatever vectors there are (SSE2, AVX2, AVX-512,
NEON) in 4, 8 or 16 blocks, Poly1305 in vectors of limbs of 26 bits, or for
short messages in three words. `cipher_blocks.h`, `cipher_chacha.h` and
`cipher_poly.h` are the loops, each included once for every width. The
portable code is bitsliced AES of four blocks (`BitslicedAes`), GHASH by
multiplications with room for their carries (`PortableGhash`), and Poly1305
by limbs: no table is indexed by a secret anywhere.

- **State** — a hash's, an HMAC's, a key's — is raw memory of its own
  (`SecretMemory`), zeroed by its owner's deinit, where no copy of it is
  made unasked; a hasher is a value that copies it before changing one that
  is shared (the builtin `ref_is_unique`). `finalize()` works on a copy and
  leaves the hasher as it was. Registers and stack slots are not wiped, and
  a secret given out as an array, or come from a String, is the caller's.
- **Constant time.** No algorithm branches on, or indexes by, a secret,
  but Argon2id in its second part and scrypt, whose memory accesses depend
  on the password by design. `constantTimeEquals`, `Digest`'s and
  `SymmetricKey`'s `==` pass every step through `blackBox`, so that LLVM
  cannot find the loop to be one it may leave early.
- **`secureZero`** is a call of the runtime's whose memset is kept by an
  assembly barrier, in the link's optimiser too.
- **Authenticated ciphers** open in one pass: the plaintext is written as its
  tag is worked out, and zeroed, in place too, when the tag is wrong, before
  the error is thrown; a tag of another length is refused unread. Every
  failure is the one `AuthenticationFailed`. A random nonce is counted in the
  cipher's state, which its copies share, and refused when n bits of nonce
  have had 2^((n - 32) / 2); the lengths the ciphers' counters end at are
  errors; that a nonce of the caller's is new, and how many bytes a key has
  enciphered in all, are not known and only documented. Nonces of GCM are of
  12 bytes unless the cipher is made for others. CBC, CTR, CFB and ECB are
  under `Hazmat`, where `InvalidPadding` is an oracle for whoever does not
  authenticate first; the padding itself is checked without branches.
- **Public keys** are the library's own mathematics too (issue #65 proposed
  the operating system's), so that a key, a signature and an error are the
  same everywhere, in the interpreter and on WASI as well. What that gives up
  is the vendors' assembly: benchmarks/library's `ecdsa-*`, `ecdh-*`,
  `x25519` and `rsa-*` measure it against .NET, which calls the system's.
  There is no `BigInt` in it: numbers are 64-bit limbs at an address in
  `SecretMemory` (`Wide`), so that they are zeroed, and 25519's field has five
  of 51 bits, the P curves' 4, 6 and 9 with a reduction of their own, and RSA
  and the group orders any number, by Montgomery's multiplication
  (`MontgomeryModulus`).
- **Nothing secret is a branch, an index or a divisor there.** A choice is
  made with a mask that has been through `Native.barrier`, a builtin
  (`value_barrier`) that is an empty assembly statement giving back its
  operand, as LLVM would otherwise know the mask for a bit and may branch on
  it. A scalar's windows look at every entry of their table; the P curves'
  sums are the complete ones of Renes, Costello and Batina and Edwards's
  curve's are complete, so no point is a special case; inverses are Pornin's
  binary algorithm, of a fixed number of steps; RSA is by the Chinese
  remainder theorem on a number blinded by a random one, checked before it is
  given out. `sharedSecret` refuses X25519 points of small order, and a P
  curve's key is checked to be on its curve when it is made. What ends in
  `Public` — the sums that check signatures — takes a time that depends on
  its numbers, which are not secret. Making an RSA key tells which candidates
  were not prime. Reading and writing keys as text (Base64, JSON) is not in
  constant time.
- **The compiled code** was read for arm64 and x86-64 (LLVM 21, `-O2`): the
  fields' functions and the points' sums have no conditional branch and no
  division, and those of `multiplyBase`, `power`, `invert` and the ladder are
  on loop counters, limb counts, allocations and reference counts. It is to be
  looked at again when the barrier or the fields change: `objdump -d` on a
  program that signs shows it.
- **ECDSA's nonces** are RFC 6979's with 32 random bytes added (§3.6): neither
  a bad random source nor a glitch in the hashing gives the key away.
  `deterministic: true` is the RFC's.
- **Key formats.** `PublicKey` and `PrivateKey` are keys of any algorithm, and
  `AsymmetricPublicKey`'s and `AsymmetricPrivateKey`'s conformers those of
  one. DER is read by its own rules alone. Encrypted PKCS #8 is not read, nor
  curves by their parameters, nor RSA keys of more than two primes or without
  them.
- **Randomness** is one source, `random_bytes` of the platform layer, for
  `SecureRandom`, `SystemRandomNumberGenerator` and UUIDs: arc4random_buf on
  Apple's systems, getrandom on Linux (again on EINTR), BCryptGenRandom, or
  WASI's random_get. When it fails the program traps.
- **Verifying** a code (`isValid` of `Hmac` and the KMACs) takes the bytes,
  what was given to `update` so far, or a reader's bytes to its end, where
  the task may suspend; `.NET`'s `...AndReset` is a copy kept of the value.
- **Passwords.** Argon2id fills its lanes one after another. Verifying reads
  a PHC string strictly — one spelling of each number, the parameters in
  their order, a salt of at least 8 bytes and a hash of at least 16 — and
  refuses one that asks for more memory or work than the caller's limits.

SHA-1, SHA-256, SHA-512, SHA3-256, MD5, HMAC, PBKDF2, AES-GCM,
ChaCha20-Poly1305, AES-CBC and the random source are timed against .NET by
the benchmarks of those names. Signatures are not here yet.

**Sockets.** `Sable.Net`'s sockets are nonblocking and handles of the
runtime's (`runtime/net.c`), read, written, accepted from and connected over
the reactor; a task that must wait suspends. What each does where systems, or
other libraries, differ:

- *Nothing listens but where it is told.* `bind` takes a `SocketAddress`, and
  there is none by default; `.v4Any` and `.v6Any` are every interface, and
  are written. An IPv6 socket takes IPv6 alone on every system unless it is
  bound `dualStack`.
- *A port in use is in use.* POSIX's listeners have SO_REUSEADDR, which there
  is about TIME_WAIT alone; Windows' have SO_EXCLUSIVEADDRUSE, SO_REUSEADDR
  there letting any program take the port too. `sharePort` (SO_REUSEPORT) is
  asked for, and Unsupported on Windows.
- *Closing* a socket another task reads, writes or accepts from wakes that
  task, which throws Disconnected. A call on a socket and its close never
  overlap: a close waits for the calls that are on it (none waits for
  anything), since the descriptor's number is the next socket's at once. The
  port is free when `close` returns.
- *Cancelling* a read, a receive or an accept loses nothing and leaves the
  socket as it was. A write that throws, cancelled or not, may have sent some
  of its bytes; a connect that does leaves no socket.
- *Out of descriptors*, accept waits and asks again, up to a tenth of a
  second apart, rather than throw or spin: no readiness says there are some.
  It asks whether there are before it accepts again, since macOS drops the
  connection an accept has no descriptor for.
  A connection given up before it was accepted is passed over.
- *Reading.* Several tasks may wait on one socket the same way (accepting
  from one listener, say); a readiness wakes them all.
- *Addresses.* `IpAddress.parse` refuses what programs read differently
  (`0177.0.0.1`, `127.1`, `0x7f.1`), and `Dns.resolve` does not hand such
  text to a resolver that would read it so. A v4-mapped IPv6 address is
  classified as the IPv4 one it goes to, and is in the networks that one is
  in; NAT64's is global only if its IPv4 one is.
- *Names* are looked up by getaddrinfo, on threads no task runs on, at most
  32 at once; one whose task was cancelled or ran out of time is left to end
  by itself, and the program's end does not wait for it.
- *SIGPIPE* is never raised and every descriptor is closed on exec.

`IoError` says why, in cases the systems' codes are sorted into.

### X.509

`Sable.Crypto.X509` reads certificates with `Sable.Serialize.Asn1` by DER's
rules and verifies paths as RFC 5280 §6 has it, with this library's
mathematics; only which roots are trusted is the system's (`TrustStore.system`).

- **The path** is searched for depth first, trusted issuers before
  intermediates and those whose key identifier is the one named before those,
  with backtracking; a subject with its key is in a path once. What it takes
  is limited: a hundred signatures, ten thousand candidates, 250,000
  comparisons of names.
- **Asked of every certificate:** its time, in whole seconds (the caller's
  clock: `at:`), no critical extension that is not known, a key and a
  signature the library has (RSA of 2048 bits or more, SHA-2). Of one that
  issued another: basic constraints, the path's length, the key's usage, the
  purpose, where it says one, and the names it permits and excludes, of
  domains, addresses, mailboxes, URIs and directories; a name of another kind
  that is constrained is refused. Policies are not processed: any is taken,
  and a critical `policyConstraints` is an extension that is not known.
- **Profiles.** `.Compatible`, the default, is that. `.Rfc5280` also asks for
  what RFC 5280 asks of who issues certificates, and `.WebPki` for the
  CA/Browser Forum's. The public suffix list is in none of them.
- **Hosts** are matched by RFC 9525: alternative names alone, "*." for one
  label before two or more. A name is ASCII: Sable.Net's `Idna` makes one.
  Sable.Net is not imported, so that what makes connections can use this.
- **Revocation** is by the lists a verifier is given; there is no OCSP, and
  nothing is fetched.
- **The system's roots.** macOS: the Security framework's anchors and the TLS
  trust settings of the user and the administrator; Windows: the ROOT store,
  for servers, without the Disallowed one. Both libraries are loaded when
  asked for (runtime/trust.c), so no program links with them. Elsewhere, and in
  the interpreter, the distribution's bundle is read. SSL_CERT_FILE and
  SSL_CERT_DIR, where they are set, are read instead on every system.

### Calling C

`Sable.Interop` (#74) is the library's half of §24 of the syntax: the compiler
passes integers, floats, pointers and structs as C does, and the rest is here.

A binding, whole — `sable bindgen zlib.h` writes the declarations, and more:

```sable
module Zlib

import Sable.Interop

public union ZlibError: Error {
    Failed(code: Int)

    public fn description() -> String {
        return match self {
            case .Failed(let code) => "zlib failed with \(code)"
        }
    }
}

// What zlib.h declares. 'unsafe': it is given pointers.
@extern("c", "compressBound")
fn bound(_ length: CUnsignedLong) -> CUnsignedLong

@extern("c", "compress")
unsafe fn deflate(_ into: RawPtr<U8>, _ intoLength: RawPtr<CUnsignedLong>, _ from: RawPtr<U8>, _ length: CUnsignedLong) -> CInt

@extern("c", "uncompress")
unsafe fn inflate(_ into: RawPtr<U8>, _ intoLength: RawPtr<CUnsignedLong>, _ from: RawPtr<U8>, _ length: CUnsignedLong) -> CInt

@extern("c", "zlibVersion")
fn version() -> CStr

/// The bytes as zlib packs them.
public fn compressed(_ bytes: U8[:]) throws(ZlibError) -> U8[>..] {
    return try filled(Int(bound(CUnsignedLong(bytes.count)))) { (into: RawPtr<U8>, length: RawPtr<CUnsignedLong>) -> CInt in
        return bytes.withPointer { (from: RawPtr<U8>) -> CInt in
            unsafe {
                return deflate(into, length, from, CUnsignedLong(bytes.count))
            }
        }
    }
}

/// The bytes zlib packed, 'length' of them at most.
public fn decompressed(_ bytes: U8[:], upTo length: Int) throws(ZlibError) -> U8[>..] {
    return try filled(length) { (into: RawPtr<U8>, length: RawPtr<CUnsignedLong>) -> CInt in
        return bytes.withPointer { (from: RawPtr<U8>) -> CInt in
            unsafe {
                return inflate(into, length, from, CUnsignedLong(bytes.count))
            }
        }
    }
}

// Room for a C function to fill, which says how much of it it filled.
fn filled(_ room: Int, _ fill: (RawPtr<U8>, RawPtr<CUnsignedLong>) -> CInt) throws(ZlibError) -> U8[>..] {
    var bytes = U8[>..](repeating: 0, count: room)
    var length = CUnsignedLong(room)
    let code = bytes.withMutablePointer { (into: RawPtr<U8>) -> CInt in
        return Memory.withPointer(to: &length) { (place: RawPtr<CUnsignedLong>) -> CInt in fill(into, place) }
    }
    if code != 0 {
        throw ZlibError.Failed(code: Int(code))
    }
    bytes.removeLast(room - Int(length))
    return bytes
}

fn main() {
    let text = "the same words, and the same words again, and the same words".utf8Bytes()
    let packed = try! compressed(text)
    print("zlib \(version().string): \(text.count) bytes in \(packed.count)")
    print(String.fromUtf8Lossy(try! decompressed(packed, upTo: text.count)))
}
```

The package's build script prints `sable:link-lib=z` (docs/05 §5.6); or there is
no library at the link, and `DynamicLibrary.open("libz.so.1")` looks the
functions up. `InteropTests` builds and runs the example as it stands here.

What holds it together:

- **Pointers are lent.** `withCString`, `withPointer`, `withMutablePointer` and
  `Memory.withPointer` give a closure where something is, for as long as it
  runs; what C keeps is a `CString`, in memory of its own, or the program's
  `RawPtr`. Making a pointer is safe and using it is `unsafe`, and giving one
  to C is using it (S3225).
- **A function value is C's function and back.** `cFunction(f)` is the address
  of an entry of C's convention over `f`, which captures nothing (S3226).
  `CFunction.function()` is a closure over an address, its code compiled for
  the type: calling it costs a C call through a pointer. `CSignature`, which
  bounds both, is met by the function types C has and by nothing else.
- **A closure goes with a context.** One entry serves every closure of a type,
  and finds which by the context: an index and a generation in the runtime's
  table (`runtime/foreign.c`), never an address. So C calling a closure that
  was let go of — its place another's by then, or not — stops the program with
  a message, and runs nothing. `withCallback` lends one for a call, with
  anything captured, to this thread alone, which is checked; `CCallback` holds
  one whose captures are Sendable (S3236), callable from any thread until it
  goes. A call in progress as it goes keeps the closure until it returns.
- **A thread the runtime did not make** is taken up the first time it enters
  Sable, by an exported function or a callback: it gets an allocator's state
  and a task of its own (so that it is not the main task's, and what it spawns
  has a parent), kept for the next such thread once it ends. What it can reach
  was shared before C was given the function — a `CCallback`'s captures, and
  the globals, whose counts are atomic from the first time anything is given
  to C to be called on any thread (docs/02 §2.7).
- **A library stays open while anything of it is held**: a `DynamicLibrary`,
  a function value (which holds it in its context) or a `LibraryVariable`.
- **Owners close once.** `OwnedFd` and `OwnedHandle` are `@noncopy`.

`OsString` (Sable.IO) is the system's own text: POSIX's bytes, or Windows'
UTF-16 as WTF-8, which the runtime converts both ways without loss
(`runtime/wide.h`). Paths, arguments and variables are given and taken as it,
and `Command` passes them on as they are.

Not here: C functions with `va_list`; unions, bit-fields and arrays inside
structs passed by value; blocks; `long double`. The interpreter calls no C
(S7000 at compile time, a trap when run).

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
@derive(Equatable, Hashable, Comparable, Serializable, Debug, Arbitrary, Copy)
struct Point { var x: I32, var y: I32 }
```

Derivation is implemented as compiler-known conformance synthesis against the
always-present `FieldDescriptor` metadata, not as source generation. It
composes with generics (`@derive(Equatable)` on `Pair<T, U>` yields a
conditional conformance `where T: Equatable, U: Equatable`).

`Serializable` writes a struct as an object of its fields, an enum as its
case's name, and a union's case tagged externally (`{"Circle": {"radius":
1}}`, a case that carries nothing as its bare name) unless the type says
otherwise; a payload of one unlabelled value is that value, of labelled
values an object, of several unlabelled ones an array. `@serialize` tunes
it, every option checked where it is written:

```sable
@derive(Serializable)
@serialize(renameAll: .SnakeCase)   // or .CamelCase, .PascalCase, .ScreamingSnakeCase, .KebabCase
struct Account {
    var userName: String            // "user_name"
    @serialize(rename: "id")
    var accountNumber: Int
    @serialize(skip)                // never written; its initial value when read
    var session: String = ""
    @serialize(defaulted)           // its initial value when the key is missing
    var retries: Int = 3
    @serialize(flatten)             // its members among this struct's own
    var contact: Contact
    @serialize(flatten)             // a map: whatever keys are left over
    var extra: Dictionary<String, Int>
}

@derive(Serializable)
@serialize(tag: "type")             // internally: {"type": "Click", "x": 1, "y": 2}
union Event {
    Click(x: Int, y: Int)
    Idle
}
// @serialize(tag: "t", content: "c"): adjacently, {"t": "Text", "c": "hi"}

@derive(Serializable)
@serialize(untagged)                // the payload alone; read as the first case it decodes as
union Id {
    Number(Int)                     // 3
    Name(String)                    // "x"
    Unknown                         // null: one case at most carries nothing
}
```

A missing optional reads as none; unknown keys are ignored. What is
flattened is anything written as a struct or a map — a derived struct, an
internally or adjacently tagged union, a dictionary, a type parameter's value,
which is asked as it is encoded — and a field of another type, or one that
would write a key twice, is an error where it is declared (S3071).

A conformance, derived or not, is generic over the format:
`encode<E: Encoder>(to encoder: E)` and `static decode<D: Decoder>(from
decoder: D)`. An `Encoder` is told a value's parts in order (`beginStruct`,
`encodeField`, …, `endStruct`), with counts for formats that write them first;
a `Decoder` is a value that can be asked for its parts, as decoders, more than
once, which is what lets an untagged union try its cases. A format conforms
to both and serves every type. In JSON a struct and a map are objects, bytes a
base64 string, a `Duration` its whole nanoseconds and a `DateTime` an ISO 8601
string in UTC.

`Validatable` (Sable.Validation) is made from a struct's attributes: each
attribute of a field that is none of the language's, nor a source
generator's, nor the program's own, is a rule, a method of `FieldRules` by
its name called with its arguments, in order, on the rules of that field.

```sable
@derive(Validatable)
struct Signup {
    @notEmpty @email
    var email: String           // r.field(\.email).notEmpty().email()
    @range(18...) @message("You must be an adult.")
    var age: Int                // r.field(\.age).range(18...).message("…")
    @equal(\.email)
    var again: String
}
```

The arguments are bound where they are written, so an error in one is
reported there, and a program's own rule, an extension's method, is an
attribute as the library's are. A name that is no rule of a field of that
type is S3092.

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
  socket, a child's pipe, its end or a signal suspends, and no worker blocks. Work that must block runs on a
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
- Waits on state are wait lists (Sable.Core's `WaitList`): a task parks in
  one and is woken, with every other or alone (the first), by whatever
  changes the state, then looks again. Parked inside an actor's method, it lets the actor go only once it
  is in the list, so no wake is missed. A park may be cancellable — the
  task's cancellation takes it out of the list and wakes it, and a task
  cancelled already does not park — which is how a channel's blocked send
  or receive gives up; and it may be made against a ticket, the list's count
  of wakes, taken before the look at the state: a wake since then ends the
  park at once, so state guarded by a lock rather than an actor (an
  AsyncStream's, which any thread yields to without waiting) misses none
  either. Both are decided as the task suspends, under the lock a wake and
  a cancellation take.
- Priorities are inherited by children and order the run queues, by band.
  A task waiting for a lower-priority one escalates it, what it waits for
  and its children, so it does not invert; one waiting for an actor
  escalates the task holding it.

### Threads, locals and parallel loops

`Sable.Concurrency`'s, over runtime/parallel.c.

- **Threads.** `Thread.spawn(name:stackSize:priority:)` starts one of the
  system's, and gives a `JoinHandle<T, E>`: `join()` and `join(atMost:)`
  block, `await value()` suspends, `isFinished` and `detach()`; what the body
  throws, the join throws, and a trap stops the program as on any thread. A
  thread ends by returning, and a program waits for every one it started,
  joined or not, as it does for its tasks (so a detached one runs to its end;
  .NET's foreground threads). What its closure captures is Sendable, and
  shared as a task's is. `Thread.current` (`id`, `name`: the main thread's is
  `main`, a worker's `sable-worker-N`), `sleep`, `yield`, `park` and `unpark`
  (one token a thread), `availableParallelism`: the processors the process is
  bound to and, on Linux, its control groups' quota rounded up, which is also
  how many workers the pool has. A stack is at least 256 KiB; a priority is a
  hint (a quality-of-service class on Apple's systems, a nice value on Linux).
- **A thread and tasks.** A thread has a task of its own to be running, as one
  C made has (§6.4, "Calling C"): the tasks it spawns are that one's children,
  which no one waits for or cancels once the thread ends. `Thread.blockOn`
  runs an async body as a task and blocks until it has finished; it traps on a
  worker, and on the main thread but where the program's own task runs, whose
  loop it then runs meanwhile. A worker that blocks in a join keeps a core
  from other tasks: async code awaits `value()`. With `SABLE_WORKERS=0` tasks
  run on the main thread alone, so a thread that blocks on one while the main
  thread blocks in a join waits for ever.
- **Scopes.** `Thread.scope { s in s.spawn { … } }` ends once every thread
  spawned on `s` has, whether its body returns or throws, so a thread may use a
  `var` of the function around it where it is. The compiler holds that `var`
  to be the one thread's alone (`ScopeChecker`, S3236): it is the calling
  function's own, the thread is spawned in the scope's own closure and not in
  a loop, nothing else in the scope mentions it, and no other closure of the
  function does. It refuses more than Rust does: threads that only
  read a `var` share it by a `let`, and the scope uses it again only once the
  scope has ended, not once the thread is joined.
- **`ThreadLocal<T>`** is a place in a table each thread's state has: a get is
  the thread's state, from the register the system keeps it in, two loads and
  a compare. Values are made when first asked for, and destroyed as the thread
  ends, the last made first, before a join returns; as the thread C made ends;
  and, for the main thread and the pool's, as the program does, once no task
  runs. `withValue` takes a closure that cannot wait, so that no value is held
  across an `await`, after which a task may be on another thread; `value` is a
  copy. There is no `@threadLocal static var`: a `static let` of a
  `ThreadLocal` is one.
- **`TaskLocal<T>`** is bound by `withValue` for as long as its body runs, by a
  node put before the running task's others, which its children begin with:
  a task is given its parent's list as a pointer, which is all it costs, and
  nothing where the list is empty. The nodes are counted, and last as long as
  a child that has them. A detached task and a thread begin with none; a
  worker in a parallel loop has those of the task that began it.
- **Loops.** `parallelFor`, `parallelForChunks`, `parallelInvoke`, and
  `parallel`'s operations are run by the thread that begins them and by the
  workers that come: a loop is put where a worker that finds no task finds it
  (`sable_thread_loop`), and what is left of it handed out a chunk at a time —
  a fixed part of what is left, so a few large ones and then smaller — until
  none is. Nothing is queued, so a loop no worker has time for, or one in a
  loop, is done by its own thread, which waits at the end only for workers in
  a chunk: none waits for one that waits for it. A worker runs its chunks as a
  child of the task the loop was begun in, so `Task.checkCancellation()` in a
  throwing body ends the loop with the task. The first error ends it: no more
  chunks are handed out. `degree` is how many threads at most, `grain` the
  least a chunk has. A thread whose last loops were each over in some
  microseconds wakes no worker for the next until that has gone on for ten.
  With no pool (`SABLE_WORKERS=0`, WASI, the interpreter) a loop is run in
  order by its thread.
- **`parallel`** on an array or a range of `Int` gives `map`, `filter`,
  `compactMap`, `flatMap`, `forEach`, `reduce`, `sum`, `min`, `max`,
  `count(where:)`, `first`, `contains`, `allSatisfy`, `sorted`: each an
  operation of its own, whose result is not parallel. Results are in order;
  `unordered` gives them as they are made. Chunks are the same each time and
  combined in order, so a sum of floats is the same for a degree.
  `array.parallelModify`, `parallelModifyChunks(of:)` and `parallelSort` change
  the elements where they are, with no copy of an array that is no one else's;
  a chunk is a class whose every access is checked, and which is empty once
  its body returns. `parallelForEach` is the loop of an async body, with so
  many children of a task group at once.
- **The interpreter** has one thread: a spawned one runs to its end as it is
  spawned, and a wait that another thread would have to end is a trap.
