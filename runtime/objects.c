// The Sable runtime: objects — the header every counted object starts with, and what is counted.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Objects -----------------------------------------------------------

// What a counted object is (docs/06 §6.2): how to destroy what it holds,
// and, for an array, how its elements are copied and destroyed; how large
// and how aligned an instance is; and what it keeps alive. The compiler
// emits one per class and per array element type; a class's vtable follows.
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
} SableMetadata;

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
// not null, not immortal. The two are the program's to count too: compiled
// code retains and releases inline, calling here only for an object's last
// release (sable_release_last), and keeps these counts as the runtime does.
static int64_t allocations;
int64_t sable_retains, sable_releases;

// With SABLE_CHECK_LEAKS set, a program that ends with objects still alive
// says how many: tests run every program this way. With SABLE_STATS set, it
// says what ARC did. Run as the program exits (platform.h).
SABLE_AT_EXIT(sable_report) {
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
            (long long)allocations, (long long)sable_retains, (long long)sable_releases);
    }
}
