// The Sable runtime: objects — the header every counted object starts with, and what is counted.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Objects -----------------------------------------------------------

// What a type is to the runtime beyond what counting it needs (docs/06
// §6.2): its kind, its stored fields and the protocols it conforms to. The
// compiler emits one per class; the runtime's own objects have none. The
// layout is shared with code that casts to a protocol (docs/06 §6.2,
// "Conformance records"), so it only grows, at the end.
enum {
    SABLE_KIND_CLASS = 1,
};

// A stored field: where it is in an instance, and what it holds. The names
// are null in a program built with --deny-reflection.
enum {
    SABLE_FIELD_DATA = 0,      // nothing the runtime follows
    SABLE_FIELD_STRONG = 1,    // a strong reference to an object, or null
    SABLE_FIELD_AGGREGATE = 2, // a value holding references: a struct, a union, a closure, ...
    SABLE_FIELD_WEAK = 3,      // a weak or unowned reference: it keeps nothing alive
};

typedef struct SableField {
    const char *name;
    const char *type_name;
    int64_t offset; // from the start of the object, its header's bytes among them
    int64_t kind;   // SABLE_FIELD_*
} SableField;

// A protocol, identified by its descriptor's address, or — across images,
// each of which has its own copy — by its name.
typedef struct SableProtocol {
    const char *name;
} SableProtocol;

// That a type conforms to a protocol: the record a dynamic cast looks up.
// 'witnesses' is what code casting to 'any P' builds the existential from;
// null where the compiler has not emitted one.
typedef struct SableConformance {
    const SableProtocol *protocol;
    const void *witnesses;
} SableConformance;

typedef struct SableTypeInfo {
    int64_t kind;
    int64_t field_count;
    const SableField *fields;
    int64_t conformance_count;
    const SableConformance *conformances;
} SableTypeInfo;

// What a counted object is (docs/06 §6.2): how to destroy what it holds,
// and, for an array, how its elements are copied and destroyed; how large
// and how aligned an instance is; what it keeps alive; and, for a class,
// what it is (SableTypeInfo). The compiler emits one per class and per
// array element type; a class's vtable follows.
typedef struct SableMetadata {
    void (*destroy)(void *object); // destroys the contents; not the memory
    const char *name;
    int64_t element_size;
    void (*copy_element)(void *destination, const void *source); // null: copy the bits; a class's superclass
    void (*destroy_element)(void *element);                       // null: nothing to do
    int64_t size;      // an instance's bytes, the header's among them; 0 when it varies (a string)
    int64_t alignment;
    // Calls sable_heap_visit with each strong reference an object holds —
    // for an array, an element holds. Null: none.
    void (*visit)(void *object);
    const SableTypeInfo *info; // null for an array and the runtime's own objects
} SableMetadata;

// ---- Conformances -----------------------------------------------------------

static bool same_protocol(const SableProtocol *a, const SableProtocol *b) {
    return a == b || (a != NULL && b != NULL && a->name != NULL && b->name != NULL && strcmp(a->name, b->name) == 0);
}

// The record of the type's conformance to the protocol — its own, or one it
// inherits from a superclass — or null when it does not conform. This is the
// conformance registry: each class's metadata carries its records, so a
// lookup walks the class and its superclasses, and no table is built as the
// program starts.
const SableConformance *sable_conformance(const SableMetadata *type, const SableProtocol *protocol) {
    while (type != NULL && type->info != NULL && type->info->kind == SABLE_KIND_CLASS) {
        for (int64_t i = 0; i < type->info->conformance_count; i++) {
            if (same_protocol(type->info->conformances[i].protocol, protocol)) {
                return &type->info->conformances[i];
            }
        }

        // A class keeps its superclass where an array keeps its element copier.
        type = (const SableMetadata *)(const void *)type->copy_element;
    }

    return NULL;
}

// Whether the object's dynamic type conforms to the protocol: 'x is P' on a class reference.
bool sable_conforms(const void *object, const SableProtocol *protocol) {
    return object != NULL && sable_conformance(*(const SableMetadata *const *)object, protocol) != NULL;
}

// The field of the object's type that holds 'target' at 'object', or null:
// what names the edges of a reported reference cycle. Its superclasses'
// fields are the class's own, at the same offsets.
static const SableField *field_holding(const SableMetadata *type, const void *object, const void *target) {
    if (type == NULL || type->info == NULL) {
        return NULL;
    }

    for (int64_t i = 0; i < type->info->field_count; i++) {
        const SableField *field = &type->info->fields[i];
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

    // The unowned count, above the flags.
    SABLE_UNOWNED_SHIFT = 9,
    SABLE_UNOWNED_ONE = 1u << SABLE_UNOWNED_SHIFT,
};

static uint32_t unowned_count(const SableObject *object) {
    return object->flags >> SABLE_UNOWNED_SHIFT;
}

_Noreturn void sable_trap(const char *message);
_Noreturn static void trap_here(const char *message);
static void sable_weak_object_died(SableObject *object);

static int64_t live_objects;
static void object_born(SableObject *object);
static void object_gone(SableObject *object);
static void report_leaks(void);
static bool tracking;

// What ARC did: the counts the benchmarks track, and what the ARC passes are
// measured by. Retains and releases count only those that touch a count —
// not null, not immortal.
static int64_t allocations, retains, releases;

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
            (long long)allocations, (long long)retains, (long long)releases);
        fprintf(stderr, "sable: slabs %lld held, %lld at most, %lld given back\n",
            (long long)slabs_held, (long long)slabs_most, (long long)slabs_returned);
    }
}
