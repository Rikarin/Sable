// The Sable runtime: objects — the header every counted object starts with, and what is counted.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Objects -----------------------------------------------------------

// What a type is to the runtime (docs/06 §6.2): its kind, its layout, how
// its values are copied and destroyed, its fields, the protocols it
// conforms to, its type arguments and its name. The compiler emits one per
// type a program asks about, makes, or boxes in an existential — a class's
// is its object metadata's 'type' (SableMetadata below) — once per program:
// a unit's copy is linkonce_odr in a COMDAT, so its address is the type's
// identity. The format only grows, at the end.
enum {
    SABLE_KIND_CLASS = 1,
    SABLE_KIND_STRUCT = 2,
    SABLE_KIND_ENUM = 3,
    SABLE_KIND_UNION = 4,
    SABLE_KIND_OPTIONAL = 5,
    SABLE_KIND_TUPLE = 6,
    SABLE_KIND_FUNCTION = 7,
    SABLE_KIND_ARRAY = 8,
    SABLE_KIND_SLICE = 9,
    SABLE_KIND_EXISTENTIAL = 10,
    SABLE_KIND_METATYPE = 11,
    SABLE_KIND_SCALAR = 12,
    SABLE_KIND_STRING = 13,
    SABLE_KIND_REFERENCE = 14,
    SABLE_KIND_OPAQUE = 15,
};

enum {
    SABLE_TYPE_TRIVIAL = 1,      // copied by copying its bits; destroying it does nothing
    SABLE_TYPE_NONCOPYABLE = 2,  // move-only: the copy witnesses are null
    SABLE_TYPE_HAS_DEINIT = 4,   // destroying one runs a deinit
    SABLE_TYPE_FIELDS = 8,       // the metadata carries its field descriptors
    SABLE_TYPE_FIELD_TYPES = 16, // and each names its field's type's metadata
};

// A stored field: where it is in an instance, and what it holds. The names
// are null in a program built with --deny-reflection. An enum's are its
// cases: each case's name, and its payload's type and offset.
enum {
    SABLE_FIELD_DATA = 0,      // nothing the runtime follows
    SABLE_FIELD_STRONG = 1,    // a strong reference to an object, or null
    SABLE_FIELD_AGGREGATE = 2, // a value holding references: a struct, a union, a closure, ...
    SABLE_FIELD_WEAK = 3,      // a weak or unowned reference: it keeps nothing alive
};

struct SableTypeMetadata;

typedef struct SableField {
    const char *name;
    const char *type_name;
    int64_t offset;                       // from the start of the value — of an object, its header's bytes among them
    int64_t kind;                         // SABLE_FIELD_*
    const struct SableTypeMetadata *type; // null unless SABLE_TYPE_FIELD_TYPES, and for a case with no payload
} SableField;

// A protocol, identified by its descriptor's address, or — across images,
// each of which has its own copy — by its name.
typedef struct SableProtocol {
    const char *name;
} SableProtocol;

// What an associated type must conform to: its index, and the protocol.
typedef struct SableAssociatedConformance {
    int64_t associated_type;
    const SableProtocol *protocol;
} SableAssociatedConformance;

// A conformance's witness table: the protocol's requirements in the order
// of its existential's slots, each a function given the box an existential
// keeps the value in, and then its associated types' metadata and what each
// must conform to (found in this registry from that metadata).
typedef struct SableWitnessTable {
    const SableProtocol *protocol;
    int64_t requirement_count;
    void *const *requirements;
    int64_t associated_type_count;
    const struct SableTypeMetadata *const *associated_types;
    int64_t associated_conformance_count;
    const SableAssociatedConformance *associated_conformances;
} SableWitnessTable;

// That a type conforms to a protocol: the record a dynamic cast looks up,
// with the witness table an existential of the protocol is built from — or
// null, where the program builds none.
typedef struct SableConformance {
    const SableProtocol *protocol;
    const SableWitnessTable *witnesses;
} SableConformance;

// How a value of a type is copied, moved and destroyed in memory, given its
// metadata (which generic code knows a type by). The take witnesses move the
// bits: every Sable value may be moved by copying them. A move-only type's
// copy witnesses are null.
typedef struct SableValueWitnesses {
    void (*initialize_with_copy)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*initialize_with_take)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*assign_with_copy)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*assign_with_take)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*destroy)(void *value, const struct SableTypeMetadata *type);
    // Hands each strong reference the value holds to sable_heap_visit.
    void (*visit)(void *value, const struct SableTypeMetadata *type);
} SableValueWitnesses;

struct SableMetadata;

typedef struct SableTypeMetadata {
    int64_t kind; // SABLE_KIND_*
    int64_t field_count;
    const SableField *fields; // a class's (its superclass's first) always; another type's with SABLE_TYPE_FIELDS
    int64_t conformance_count;
    const SableConformance *conformances; // a class's own: its superclass's are the superclass's
    const char *name;                     // as Sable writes it: 'Zoo.Animal?', 'Sable.Collections.Deque<String>'
    int64_t size;
    int64_t alignment;
    int64_t stride;
    int64_t flags; // SABLE_TYPE_*
    const SableValueWitnesses *witnesses;
    // A generic type's arguments; an optional's wrapped type, an array's
    // element, a tuple's elements, a function's parameters and result.
    int64_t argument_count;
    const struct SableTypeMetadata *const *arguments;
    const struct SableMetadata *object; // a class's object metadata: its vtable, its superclass's; else null
} SableTypeMetadata;

// What a counted object is (docs/06 §6.2): how to destroy what it holds,
// and, for an array, how its elements are copied and destroyed; how large
// and how aligned an instance is; what it keeps alive; and, for a class,
// its type's metadata. The compiler emits one per class and per array
// element type; a class's vtable follows.
typedef struct SableMetadata {
    void (*destroy)(void *object); // destroys the contents; not the memory
    const char *name;
    int64_t element_size;
    void (*copy_element)(void *destination, const void *source); // null: copy the bits; a class's superclass
    void (*destroy_element)(void *element);                       // null: nothing to do
    int64_t size;                                                 // an instance's bytes, the header's among them; 0 when it varies (a string)
    int64_t alignment;
    // Calls sable_heap_visit with each strong reference an object holds —
    // for an array, an element holds. Null: none.
    void (*visit)(void *object);
    const SableTypeMetadata *type; // a class's; null for an array and the runtime's own objects
} SableMetadata;

// ---- Conformances -----------------------------------------------------------

static bool same_protocol(const SableProtocol *a, const SableProtocol *b) {
    return a == b || (a != NULL && b != NULL && a->name != NULL && b->name != NULL && strcmp(a->name, b->name) == 0);
}

// The superclass's metadata, for a class's; null for any other type.
static const SableTypeMetadata *superclass_of(const SableTypeMetadata *type) {
    if (type->kind != SABLE_KIND_CLASS || type->object == NULL) {
        return NULL;
    }

    // A class keeps its superclass where an array keeps its element copier.
    const SableMetadata *super = (const SableMetadata *)(const void *)type->object->copy_element;
    return super == NULL ? NULL : super->type;
}

// The record of the type's conformance to the protocol — its own, or for a
// class one it inherits from a superclass — or null when it does not
// conform. This is the conformance registry: each type's metadata carries
// its records, so a lookup walks the type and its superclasses, and no
// table is built as the program starts.
const SableConformance *sable_type_conformance(const SableTypeMetadata *type, const SableProtocol *protocol) {
    for (; type != NULL; type = superclass_of(type)) {
        for (int64_t i = 0; i < type->conformance_count; i++) {
            if (same_protocol(type->conformances[i].protocol, protocol)) {
                return &type->conformances[i];
            }
        }
    }

    return NULL;
}

// The same, for a class by its object metadata.
const SableConformance *sable_conformance(const SableMetadata *type, const SableProtocol *protocol) {
    return type == NULL ? NULL : sable_type_conformance(type->type, protocol);
}

// The witness table of the type's conformance to the protocol, or null:
// what 'e as? any Q' builds the existential from.
const SableWitnessTable *sable_witness_table(const SableTypeMetadata *type, const SableProtocol *protocol) {
    const SableConformance *conformance = sable_type_conformance(type, protocol);
    return conformance == NULL ? NULL : conformance->witnesses;
}

// Whether the object's dynamic type conforms to the protocol: 'x is P' on a class reference.
bool sable_conforms(const void *object, const SableProtocol *protocol) {
    return object != NULL && sable_conformance(*(const SableMetadata *const *)object, protocol) != NULL;
}

// The field of the object's type that holds 'target' at 'object', or null:
// what names the edges of a reported reference cycle. Its superclasses'
// fields are the class's own, at the same offsets.
static const SableField *field_holding(const SableMetadata *type, const void *object, const void *target) {
    if (type == NULL || type->type == NULL) {
        return NULL;
    }

    for (int64_t i = 0; i < type->type->field_count; i++) {
        const SableField *field = &type->type->fields[i];
        if (field->kind == SABLE_FIELD_STRONG && *(const void *const *)((const char *)object + field->offset) == target) {
            return field;
        }
    }

    return NULL;
}

// The header of docs/06 §6.2: metadata, the strong count, and a word of
// flags in its low byte with the unowned count above them.
typedef struct SableObject {
    const SableMetadata *type;
    uint32_t strong;
    uint32_t flags;
} SableObject;

enum {
    // Never counted, never freed: string literals live in read-only memory.
    SABLE_IMMORTAL = 1u,

    // The last strong reference has gone and the deinit has run; unowned
    // references still hold the memory.
    SABLE_DEINITED = 2u,

    // Weak references to it share an entry in the side table.
    SABLE_HAS_SIDE = 4u,

    // Made in a frame it does not outlive (docs/04 §3.7): counted as any
    // object is, its contents destroyed at the last release, never freed.
    SABLE_STACK = 8u,

    // An inline block that may hold a relative reference: one has been set
    // in it, or it came from bytes or another block. A block without one
    // changes size without walking itself for references to measure again.
    SABLE_LINKED = 16u,

    // Bits 5 to 8: the size class the object's memory came from (see
    // Memory, below); zero for memory from malloc.
    SABLE_CLASS_SHIFT = 5,
    SABLE_CLASS_MASK = 15u << SABLE_CLASS_SHIFT,

    // More than one thread may reach it: its counts are atomic (docs/02
    // §2.7). Set as a value crosses from one task to another — into a
    // task's closure, through an actor's call, as a task's result — on the
    // object and on everything it reaches (sable_share). An object only one
    // task can reach is counted with plain loads and stores; one task runs
    // on one thread at a time, and a task moving to another thread is
    // handed over with a release and an acquire.
    SABLE_SHARED = 1u << 9,

    // Its initialiser threw: it was never made, so no deinit of its runs
    // when it goes, though the fields set so far are destroyed as ever.
    // Compiled code's class destroy functions test this bit (1024).
    SABLE_UNMADE = 1u << 10,

    // The unowned count, above the flags.
    SABLE_UNOWNED_SHIFT = 11,
    SABLE_UNOWNED_ONE = 1u << SABLE_UNOWNED_SHIFT,
};

// The flags, read while another thread may change the unowned count.
static inline uint32_t object_flags(const SableObject *object) {
    return __atomic_load_n(&object->flags, __ATOMIC_RELAXED);
}

// The flags, changed: atomically, as another thread may change them too.
static inline void flags_set(SableObject *object, uint32_t bits) {
    __atomic_fetch_or(&object->flags, bits, __ATOMIC_RELAXED);
}

static inline void flags_clear(SableObject *object, uint32_t bits) {
    __atomic_fetch_and(&object->flags, ~bits, __ATOMIC_RELAXED);
}

// The strong count, as a check of uniqueness reads it: the releases other
// threads made before it are seen.
static inline uint32_t strong_count(const SableObject *object) {
    return __atomic_load_n(&object->strong, __ATOMIC_ACQUIRE);
}


_Noreturn void sable_trap(const char *message);
_Noreturn static void trap_here(const char *message);
static void sable_weak_object_died(SableObject *object);

static void object_born(SableObject *object);
static void object_gone(SableObject *object);
static void report_leaks(void);
static void report_frames(void);
static void report_tasks(void);
static void forget_immortal_reach(void);
static void trap_hook_forget(void);
static bool tracking;

// What ARC did: the counts the benchmarks track, and what the ARC passes are
// measured by. Retains and releases count only those that touch a count —
// not null, not immortal — and only under SABLE_STATS (threads.c). The two
// are the program's to count too: compiled code retains and releases
// inline, calling here only for an object's last release
// (sable_release_last), and keeps these counts as the runtime does.
// Allocations and the objects alive are counted by each thread (threads.c).
static int64_t thread_sum(size_t offset) {
    int64_t sum = 0;
    spin_lock(&threads_lock);
    for (SableThread *thread = all_threads; thread != NULL; thread = thread->next) {
        sum += __atomic_load_n((int64_t *)((char *)thread + offset), __ATOMIC_RELAXED);
    }

    spin_unlock(&threads_lock);
    return sum;
}

static int64_t live_object_count(void) {
    return thread_sum(offsetof(SableThread, live_objects));
}

// What a library does as the process exits — destroy its globals — given by
// its start-up (LlvmEmitter's LibraryLifetime). Run here, before the report,
// rather than as destructors of their own: the order of one image's
// destructors is the linker's, and the report must come after them.
static void (*exit_hooks[8])(void);
static int exit_hook_count;

void sable_runtime_at_exit(void (*hook)(void)) {
    if (exit_hook_count < (int)(sizeof exit_hooks / sizeof exit_hooks[0])) {
        exit_hooks[exit_hook_count++] = hook;
    }
}

// The small blocks' slabs (memory.c): how many are held now and were at
// most, and how many were given back to the system.
static int64_t slabs_held, slabs_most, slabs_returned;

// With SABLE_CHECK_LEAKS set, a program that ends with objects still alive
// says how many: tests run every program this way. With SABLE_STATS set, it
// says what ARC did. Run as the program exits (platform.h).
SABLE_AT_EXIT(sable_report) {
    while (exit_hook_count > 0) {
        exit_hooks[--exit_hook_count]();
    }

    trap_hook_forget();
    forget_immortal_reach();

    int64_t live_objects = live_object_count();
    if (live_objects != 0 && getenv("SABLE_CHECK_LEAKS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: %lld object(s) leaked\n", (long long)live_objects);
        if (tracking) {
            report_leaks();
        }
    }

    if (getenv("SABLE_STATS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: allocations %lld, retains %lld, releases %lld\n",
            (long long)thread_sum(offsetof(SableThread, allocations)), (long long)sable_retains, (long long)sable_releases);
        fprintf(stderr, "sable: slabs %lld held, %lld at most, %lld given back\n",
            (long long)__atomic_load_n(&slabs_held, __ATOMIC_RELAXED), (long long)__atomic_load_n(&slabs_most, __ATOMIC_RELAXED),
            (long long)__atomic_load_n(&slabs_returned, __ATOMIC_RELAXED));
        report_tasks();
        report_frames();
        fprintf(stderr, "sable: peak memory %lld KiB\n", (long long)(peak_memory() / 1024));
    }
}
