// The Sable runtime: the small amount of native code a compiled program
// links against (docs/06 §6.1).
//
// Writing a line, stopping the way the language says a program stops, and the
// heap: counted objects, strings and copy-on-write arrays. Every counted
// object starts with the header of docs/06 §6.2. Counts are not atomic yet:
// there is one thread until the concurrency runtime arrives.

// glibc declares dladdr (for a trap's backtrace) only for GNU sources.
#define _GNU_SOURCE

#include <errno.h>
#include <math.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- Objects -----------------------------------------------------------

// What a counted object is: how to destroy what it holds, and, for an
// array, how its elements are copied and destroyed. The compiler emits one
// per class and per array element type.
typedef struct SableMetadata {
    void (*destroy)(void *object); // destroys the contents; not the memory
    const char *name;
    int64_t element_size;
    void (*copy_element)(void *destination, const void *source); // null: copy the bits
    void (*destroy_element)(void *element);                       // null: nothing to do
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

    // Bits 5 to 7: the size class the object's memory came from (see
    // Memory, below); zero for memory from malloc.
    SABLE_CLASS_SHIFT = 5,
    SABLE_CLASS_MASK = 7u << SABLE_CLASS_SHIFT,

    SABLE_UNOWNED_ONE = 1u << 8,
};

static uint32_t unowned_count(const SableObject *object) {
    return object->flags >> 8;
}

_Noreturn void sable_trap(const char *message);
static void sable_weak_object_died(SableObject *object);

static int64_t live_objects;

// What ARC did: the counts the benchmarks track, and what the ARC passes are
// measured by. Retains and releases count only those that touch a count —
// not null, not immortal.
static int64_t allocations, retains, releases;

// With SABLE_CHECK_LEAKS set, a program that ends with objects still alive
// says how many: tests run every program this way. With SABLE_STATS set, it
// says what ARC did.
__attribute__((destructor)) static void sable_report(void) {
    if (live_objects != 0 && getenv("SABLE_CHECK_LEAKS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: %lld object(s) leaked\n", (long long)live_objects);
    }

    if (getenv("SABLE_STATS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: allocations %lld, retains %lld, releases %lld\n",
            (long long)allocations, (long long)retains, (long long)releases);
    }
}

// ---- Memory ------------------------------------------------------------

// Small blocks — most objects, and the elements of most arrays — come from
// size classes kept here rather than from malloc: a free list per class,
// refilled by carving slabs in order. Programs that make and drop many
// small strings and arrays otherwise spend most of their time in malloc and
// free, where a .NET program bumps a pointer (docs/04 §4.1 names a
// size-class allocator; this is its first, smallest form). One task runs at
// a time (see Concurrency), so the lists need no locks. A freed block goes
// back on its class's list and stays there: the memory is the program's
// until it ends, as a garbage-collected heap's is.
//
// An object records its class in its flags, so freeing one needs no lookup;
// an array's elements are freed with their size, which the array knows.

enum { SMALL_CLASSES = 8, SMALL_LARGEST = 256, SLAB_SIZE = 256 * 1024 };

static const int64_t small_sizes[SMALL_CLASSES] = { 0, 32, 48, 64, 96, 128, 192, 256 };

// The class for a size, by sixteenths: the smallest that holds it.
static const uint8_t small_class_of[SMALL_LARGEST / 16 + 1] = { 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7 };

static void *small_free[SMALL_CLASSES];
static char *slab_next, *slab_end;

_Noreturn void sable_trap(const char *message);

// The class for 'size' bytes, or zero: too large, or nothing at all.
static inline unsigned small_class(int64_t size) {
    return size > 0 && size <= SMALL_LARGEST ? small_class_of[(size + 15) >> 4] : 0;
}

static void *small_alloc(unsigned class) {
    void *block = small_free[class];
    if (block != NULL) {
        small_free[class] = *(void **)block;
        return block;
    }

    // What is left of the slab, too little for this class, is let go.
    int64_t size = small_sizes[class];
    if (slab_end - slab_next < size) {
        slab_next = malloc(SLAB_SIZE);
        if (slab_next == NULL) {
            sable_trap("out of memory");
        }

        slab_end = slab_next + SLAB_SIZE;
    }

    block = slab_next;
    slab_next += size;
    return block;
}

static inline void small_release(void *block, unsigned class) {
    *(void **)block = small_free[class];
    small_free[class] = block;
}

// The large buffer freed last, kept rather than given back to the system: a
// program that builds a large array, drops it and builds another — as a
// loop over batches does — would otherwise have the system map and zero the
// same memory afresh each time, which costs more than filling it. A
// collected heap keeps its memory the same way. Its size is what its last
// owner asked for: the block may be larger, never smaller.
enum { LARGE_BUFFER = 1 << 20 };
static void *large_spare;
static int64_t large_spare_size;

// The spare, if it holds 'size' bytes without wasting more than as much again.
static void *take_spare(int64_t size) {
    if (large_spare == NULL || large_spare_size < size || large_spare_size > 2 * size) {
        return NULL;
    }

    void *buffer = large_spare;
    large_spare = NULL;
    return buffer;
}

// A large buffer let go: kept as the spare if it is the larger, freed otherwise.
static void keep_spare(void *buffer, int64_t size) {
    if (large_spare != NULL && large_spare_size >= size) {
        free(buffer);
        return;
    }

    free(large_spare);
    large_spare = buffer;
    large_spare_size = size;
}

// An array's elements: 'size' bytes, uninitialised.
static void *buffer_alloc(int64_t size) {
    unsigned class = small_class(size);
    if (class != 0) {
        return small_alloc(class);
    }

    void *buffer = size >= LARGE_BUFFER ? take_spare(size) : NULL;
    if (buffer == NULL) {
        buffer = malloc((size_t)(size > 0 ? size : 1));
    }

    if (buffer == NULL) {
        sable_trap("out of memory");
    }

    return buffer;
}

static void buffer_free(void *buffer, int64_t size) {
    if (buffer == NULL) {
        return;
    }

    unsigned class = small_class(size);
    if (class != 0) {
        small_release(buffer, class);
    } else if (size >= LARGE_BUFFER) {
        keep_spare(buffer, size);
    } else {
        free(buffer);
    }
}

// Elements grown from 'old' bytes to 'size', kept as they were.
static void *buffer_resize(void *buffer, int64_t old, int64_t size) {
    unsigned from = buffer == NULL ? 0 : small_class(old), to = small_class(size);
    if (buffer != NULL && from == 0 && to == 0) {
        // Into the spare, when it fits: its pages are already the program's.
        void *spare = size >= LARGE_BUFFER ? take_spare(size) : NULL;
        if (spare != NULL) {
            memcpy(spare, buffer, (size_t)(old < size ? old : size));
            buffer_free(buffer, old);
            return spare;
        }

        void *grown = realloc(buffer, (size_t)size);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        return grown;
    }

    if (buffer != NULL && from == to) {
        return buffer;
    }

    void *grown = buffer_alloc(size);
    if (buffer != NULL) {
        memcpy(grown, buffer, (size_t)(old < size ? old : size));
        buffer_free(buffer, old);
    }

    return grown;
}

// An object's memory, given back: to its class, or to malloc.
static void object_free(SableObject *object) {
    unsigned class = (object->flags & SABLE_CLASS_MASK) >> SABLE_CLASS_SHIFT;
    if (class != 0) {
        small_release(object, class);
    } else {
        free(object);
    }
}

// A new object, its count one; the rest zero when 'zero' is set, and
// otherwise whatever was there, for the caller to write.
static void *alloc_object(const SableMetadata *type, int64_t size, bool zero) {
    unsigned class = small_class(size);
    SableObject *object;
    if (class != 0) {
        object = small_alloc(class);
        if (zero) {
            memset(object, 0, (size_t)size);
        }

        object->flags = class << SABLE_CLASS_SHIFT;
    } else {
        object = zero ? calloc(1, (size_t)size) : malloc((size_t)size);
        if (object == NULL) {
            sable_trap("out of memory");
        }

        object->flags = 0;
    }

    object->type = type;
    object->strong = 1;
    live_objects++;
    allocations++;
    return object;
}

// A new object, its count one and the rest of it zero: every field empty.
void *sable_alloc(const SableMetadata *type, int64_t size) {
    return alloc_object(type, size, true);
}

// An object in the caller's frame: the header, and the rest zero.
void *sable_stack_object(void *memory, const SableMetadata *type, int64_t size) {
    memset(memory, 0, (size_t)size);
    SableObject *object = memory;
    object->type = type;
    object->strong = 1;
    object->flags = SABLE_STACK;
    live_objects++;
    return object;
}

void sable_retain(void *pointer) {
    SableObject *object = pointer;
    if (object != NULL && !(object->flags & SABLE_IMMORTAL)) {
        object->strong++;
        retains++;
    }
}

void sable_release(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object->flags & SABLE_IMMORTAL)) {
        return;
    }

    releases++;
    if (--object->strong > 0) {
        return;
    }

    // The deinit, and the destruction of the fields, see the object alive.
    object->strong = 1;
    if (object->type->destroy != NULL) {
        object->type->destroy(object);
    }

    if (object->strong != 1) {
        sable_trap("an object outlived its deinit");
    }

    object->strong = 0;
    object->flags |= SABLE_DEINITED;
    if (object->flags & SABLE_HAS_SIDE) {
        sable_weak_object_died(object);
    }

    // Unowned references hold the memory, not the object, until they go.
    if (unowned_count(object) > 0) {
        return;
    }

    live_objects--;
    if (!(object->flags & SABLE_STACK)) {
        object_free(object);
    }
}

// ---- Exclusivity -----------------------------------------------------------

// The accesses in progress to storage inside objects (docs/02 §2.3): an
// inout or ref argument's, for the length of its call. One that overlaps
// an access in progress, either of them a change, is a trap. Kept in
// checked builds only; the compiler proves what it can without them.
typedef struct SableAccess {
    void *storage;
    int32_t modify;
} SableAccess;

static SableAccess *accesses;
static int64_t access_count, access_capacity;

void sable_access_begin(void *storage, int32_t modify) {
    for (int64_t i = 0; i < access_count; i++) {
        if (accesses[i].storage == storage && (modify || accesses[i].modify)) {
            sable_trap("overlapping accesses to the same storage");
        }
    }

    if (access_count == access_capacity) {
        access_capacity = access_capacity < 16 ? 16 : access_capacity * 2;
        accesses = realloc(accesses, (size_t)access_capacity * sizeof(SableAccess));
        if (accesses == NULL) {
            sable_trap("out of memory");
        }
    }

    accesses[access_count++] = (SableAccess){ storage, modify };
}

void sable_access_end(void *storage) {
    for (int64_t i = access_count - 1; i >= 0; i--) {
        if (accesses[i].storage == storage) {
            accesses[i] = accesses[--access_count];
            return;
        }
    }
}

// ---- Classes ---------------------------------------------------------------

// 'x is Dog': whether the metadata is the class's, or a subclass's. A class's
// metadata keeps its superclass's where an array's keeps its element copier.
bool sable_class_is(const SableMetadata *type, const SableMetadata *target) {
    for (; type != NULL; type = (const SableMetadata *)(const void *)type->copy_element) {
        if (type == target) {
            return true;
        }
    }

    return false;
}

// ---- Regions -------------------------------------------------------------

// A region (docs/02 §2.1): chunks values are bumped into, and the destroys of
// those that hold references, run last first when the region goes.
typedef struct SableChunk {
    struct SableChunk *next;
    size_t used, size;
    _Alignas(16) char bytes[];
} SableChunk;

typedef struct SableCleanup {
    void *value;
    void (*destroy)(void *value);
} SableCleanup;

// Where a 'region frame = pool' block began: what it gives back as it ends.
typedef struct SableRegionMark {
    SableChunk *chunk;
    size_t used;
    int64_t cleanup_count;
} SableRegionMark;

typedef struct SableRegion {
    SableObject header;
    SableChunk *chunk;
    SableCleanup *cleanups;
    int64_t cleanup_count, cleanup_capacity;
    SableRegionMark *marks;
    int64_t mark_count, mark_capacity;
} SableRegion;

static void sable_region_destroy(void *pointer) {
    SableRegion *region = pointer;
    for (int64_t i = region->cleanup_count - 1; i >= 0; i--) {
        region->cleanups[i].destroy(region->cleanups[i].value);
    }

    free(region->cleanups);
    free(region->marks);
    for (SableChunk *chunk = region->chunk; chunk != NULL;) {
        SableChunk *next = chunk->next;
        free(chunk);
        chunk = next;
    }
}

static const SableMetadata region_metadata = { sable_region_destroy, "Region", 0, NULL, NULL };

void *sable_region_create(void) {
    return sable_alloc(&region_metadata, (int64_t)sizeof(SableRegion));
}

// Room for a value of 'size' bytes, aligned to 16; with a destroy, it is
// destroyed when the region goes. The memory is zero: empty, until stored.
void *sable_region_alloc(void *pointer, int64_t size, void (*destroy)(void *value)) {
    SableRegion *region = pointer;
    size_t rounded = ((size_t)size + 15) & ~(size_t)15;
    SableChunk *chunk = region->chunk;
    if (chunk == NULL || chunk->used + rounded > chunk->size) {
        size_t capacity = chunk == NULL ? 4096 : chunk->size * 2;
        if (capacity < rounded) {
            capacity = rounded;
        }

        SableChunk *fresh = calloc(1, sizeof(SableChunk) + capacity);
        if (fresh == NULL) {
            sable_trap("out of memory");
        }

        fresh->size = capacity;
        fresh->next = chunk;
        region->chunk = chunk = fresh;
    }

    void *value = chunk->bytes + chunk->used;
    chunk->used += rounded;

    if (destroy != NULL) {
        if (region->cleanup_count == region->cleanup_capacity) {
            region->cleanup_capacity = region->cleanup_capacity == 0 ? 16 : region->cleanup_capacity * 2;
            region->cleanups = realloc(region->cleanups, (size_t)region->cleanup_capacity * sizeof(SableCleanup));
        }

        region->cleanups[region->cleanup_count++] = (SableCleanup){ value, destroy };
    }

    return value;
}

// Destroys what was allocated since cleanup 'count', last first, and frees
// the chunks begun since 'chunk', bumping back to 'used' in it.
static void region_back_to(SableRegion *region, SableChunk *chunk, size_t used, int64_t count) {
    for (int64_t i = region->cleanup_count - 1; i >= count; i--) {
        region->cleanups[i].destroy(region->cleanups[i].value);
    }

    region->cleanup_count = count;
    while (region->chunk != chunk) {
        SableChunk *next = region->chunk->next;
        free(region->chunk);
        region->chunk = next;
    }

    if (chunk != NULL) {
        chunk->used = used;
        memset(chunk->bytes + used, 0, chunk->size - used);
    }
}

// 'arena.reset()': everything in it destroyed; the newest chunk kept, empty,
// for what comes next — a frame's worth of churn in one store.
void sable_region_reset(void *pointer) {
    SableRegion *region = pointer;
    SableChunk *newest = region->chunk;
    region_back_to(region, newest, 0, 0);
    if (newest != NULL) {
        for (SableChunk *older = newest->next; older != NULL;) {
            SableChunk *next = older->next;
            free(older);
            older = next;
        }

        newest->next = NULL;
    }

    region->mark_count = 0;
}

void sable_region_push(void *pointer) {
    SableRegion *region = pointer;
    if (region->mark_count == region->mark_capacity) {
        region->mark_capacity = region->mark_capacity < 4 ? 4 : region->mark_capacity * 2;
        region->marks = realloc(region->marks, (size_t)region->mark_capacity * sizeof(SableRegionMark));
        if (region->marks == NULL) {
            sable_trap("out of memory");
        }
    }

    region->marks[region->mark_count++] = (SableRegionMark){
        region->chunk, region->chunk == NULL ? 0 : region->chunk->used, region->cleanup_count,
    };
}

void sable_region_pop(void *pointer) {
    SableRegion *region = pointer;
    if (region->mark_count == 0) {
        return;
    }

    SableRegionMark mark = region->marks[--region->mark_count];
    region_back_to(region, mark.chunk, mark.used, mark.cleanup_count);
}

// ---- Unowned references --------------------------------------------------

void sable_unowned_retain(void *pointer) {
    SableObject *object = pointer;
    if (object != NULL && !(object->flags & SABLE_IMMORTAL)) {
        object->flags += SABLE_UNOWNED_ONE;
    }
}

void sable_unowned_release(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object->flags & SABLE_IMMORTAL)) {
        return;
    }

    object->flags -= SABLE_UNOWNED_ONE;
    if (unowned_count(object) == 0 && (object->flags & SABLE_DEINITED)) {
        live_objects--;
        object_free(object);
    }
}

// Reading an unowned reference: the object, retained — or a trap, if it is gone.
void sable_unowned_load(void *pointer) {
    SableObject *object = pointer;
    if (object->flags & SABLE_DEINITED) {
        sable_trap("read an unowned reference to an object that is gone");
    }

    sable_retain(object);
}

// ---- Weak references -------------------------------------------------------

// A weak reference points to its object's entry, not the object: the entry
// outlives the object, and says it is gone. Entries are found by object in an
// open-addressed table, which only objects with weak references are in.
typedef struct SableWeakEntry {
    SableObject *object;
    int64_t count;
} SableWeakEntry;

static SableWeakEntry **side_table;
static int64_t side_capacity, side_count;

#define SIDE_TOMBSTONE ((SableWeakEntry *)1)

static size_t side_slot(const SableObject *object) {
    uintptr_t key = (uintptr_t)object;
    key ^= key >> 33;
    key *= 0xff51afd7ed558ccdULL;
    key ^= key >> 33;
    return (size_t)(key & (uintptr_t)(side_capacity - 1));
}

static SableWeakEntry **side_find(const SableObject *object) {
    for (size_t i = side_slot(object);; i = (i + 1) & (size_t)(side_capacity - 1)) {
        SableWeakEntry *entry = side_table[i];
        if (entry == NULL) {
            return NULL;
        }

        if (entry != SIDE_TOMBSTONE && entry->object == object) {
            return &side_table[i];
        }
    }
}

static void side_insert(SableWeakEntry *entry);

static void side_grow(void) {
    SableWeakEntry **old = side_table;
    int64_t old_capacity = side_capacity;
    side_capacity = side_capacity == 0 ? 64 : side_capacity * 2;
    side_table = calloc((size_t)side_capacity, sizeof *side_table);
    side_count = 0;
    for (int64_t i = 0; i < old_capacity; i++) {
        if (old[i] != NULL && old[i] != SIDE_TOMBSTONE) {
            side_insert(old[i]);
        }
    }

    free(old);
}

static void side_insert(SableWeakEntry *entry) {
    if ((side_count + 1) * 2 > side_capacity) {
        side_grow();
    }

    size_t i = side_slot(entry->object);
    while (side_table[i] != NULL && side_table[i] != SIDE_TOMBSTONE) {
        i = (i + 1) & (size_t)(side_capacity - 1);
    }

    side_table[i] = entry;
    side_count++;
}

static void side_remove(const SableObject *object) {
    SableWeakEntry **slot = side_find(object);
    if (slot != NULL) {
        *slot = SIDE_TOMBSTONE;
    }
}

// The entry for an object, retained: made, and the object marked, the first time.
void *sable_weak_entry(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL) {
        return NULL;
    }

    SableWeakEntry *entry;
    if (object->flags & SABLE_HAS_SIDE) {
        entry = *side_find(object);
    } else {
        entry = malloc(sizeof *entry);
        entry->object = object;
        entry->count = 0;
        side_insert(entry);
        object->flags |= SABLE_HAS_SIDE;
    }

    entry->count++;
    return entry;
}

void sable_weak_retain(void *pointer) {
    SableWeakEntry *entry = pointer;
    if (entry != NULL) {
        entry->count++;
    }
}

void sable_weak_release(void *pointer) {
    SableWeakEntry *entry = pointer;
    if (entry == NULL || --entry->count > 0) {
        return;
    }

    if (entry->object != NULL) {
        side_remove(entry->object);
        entry->object->flags &= ~(uint32_t)SABLE_HAS_SIDE;
    }

    free(entry);
}

// Reading a weak reference: its object, retained, or null once it is gone.
void *sable_weak_load(void *pointer) {
    SableWeakEntry *entry = pointer;
    if (entry == NULL || entry->object == NULL) {
        return NULL;
    }

    sable_retain(entry->object);
    return entry->object;
}

// The object has died: its weak references now read as none.
static void sable_weak_object_died(SableObject *object) {
    SableWeakEntry **slot = side_find(object);
    if (slot != NULL) {
        (*slot)->object = NULL;
        *slot = SIDE_TOMBSTONE;
    }

    object->flags &= ~(uint32_t)SABLE_HAS_SIDE;
}

// ---- Stopping and writing -------------------------------------------

// Overflow, division by zero, an unreachable point reached: not catchable,
// not unwound. Say why, then stop (docs/06 §6.6).
#include <dlfcn.h>
#include <execinfo.h>

// With SABLE_BACKTRACE set, a trap says where it happened: a frame per line,
// as 'sable.Module.function + offset' where the symbol is known. Off by
// default, so that a trap's message is the whole of its output. Each return
// address is looked up one byte back: after a call that never returns, the
// address is past the end of the caller, inside whatever comes next.
__attribute__((noinline)) static void print_backtrace(void) {
    if (getenv("SABLE_BACKTRACE") == NULL) {
        return;
    }

    void *frames[64];
    int count = backtrace(frames, 64);
    fprintf(stderr, "backtrace:\n");
    // The first two frames are this function and sable_trap.
    for (int i = 2; i < count; i++) {
        const char *at = (const char *)frames[i] - 1;
        Dl_info info;
        if (dladdr(at, &info) != 0 && info.dli_sname != NULL) {
            fprintf(stderr, "  %2d  %s + %ld\n", i - 2, info.dli_sname, (long)(at + 1 - (const char *)info.dli_saddr));
        } else {
            fprintf(stderr, "  %2d  %p\n", i - 2, frames[i]);
        }
    }
}

_Noreturn void sable_trap(const char *message) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    print_backtrace();
    abort();
}

// ---- Strings -----------------------------------------------------------

// UTF-8 bytes, their number, and the number of Unicode scalars they hold —
// equal for ASCII, which makes indexing by scalar a matter of indexing bytes.
typedef struct SableString {
    SableObject header;
    int64_t length;
    int64_t count;
    char bytes[];
} SableString;

const SableMetadata sable_string_metadata = { NULL, "String", 0, NULL, NULL };

static int64_t count_scalars(const char *bytes, int64_t length) {
    int64_t count = 0;
    for (int64_t i = 0; i < length; i++) {
        if (((unsigned char)bytes[i] & 0xC0) != 0x80) {
            count++;
        }
    }

    return count;
}

// A string of 'length' bytes still to be written, holding 'count' scalars:
// the bytes are not zeroed first, as every caller writes them all.
static SableString *new_string(int64_t length, int64_t count) {
    SableString *string = alloc_object(&sable_string_metadata, (int64_t)sizeof(SableString) + length, false);
    string->length = length;
    string->count = count;
    return string;
}

// A string of bytes whose scalars are known to number 'count'.
static SableString *make_counted(const char *bytes, int64_t length, int64_t count) {
    SableString *string = new_string(length, count);
    memcpy(string->bytes, bytes, (size_t)length);
    return string;
}

static SableString *make_string(const char *bytes, int64_t length) {
    return make_counted(bytes, length, count_scalars(bytes, length));
}

static SableString *make_cstring(const char *text) {
    return make_string(text, (int64_t)strlen(text));
}

// The byte offset of scalar 'index'.
static int64_t offset_of(const SableString *s, int64_t index) {
    if (s->count == s->length) {
        return index;
    }

    int64_t seen = -1;
    for (int64_t i = 0; i < s->length; i++) {
        if (((unsigned char)s->bytes[i] & 0xC0) != 0x80 && ++seen == index) {
            return i;
        }
    }

    return s->length;
}

static int32_t decode(const char *bytes, int64_t *width) {
    const unsigned char *b = (const unsigned char *)bytes;
    if (b[0] < 0x80) {
        *width = 1;
        return b[0];
    }

    if (b[0] < 0xE0) {
        *width = 2;
        return ((b[0] & 0x1F) << 6) | (b[1] & 0x3F);
    }

    if (b[0] < 0xF0) {
        *width = 3;
        return ((b[0] & 0x0F) << 12) | ((b[1] & 0x3F) << 6) | (b[2] & 0x3F);
    }

    *width = 4;
    return ((b[0] & 0x07) << 18) | ((b[1] & 0x3F) << 12) | ((b[2] & 0x3F) << 6) | (b[3] & 0x3F);
}

static int encode(int32_t scalar, char *out) {
    if (scalar < 0x80) {
        out[0] = (char)scalar;
        return 1;
    }

    if (scalar < 0x800) {
        out[0] = (char)(0xC0 | (scalar >> 6));
        out[1] = (char)(0x80 | (scalar & 0x3F));
        return 2;
    }

    if (scalar < 0x10000) {
        out[0] = (char)(0xE0 | (scalar >> 12));
        out[1] = (char)(0x80 | ((scalar >> 6) & 0x3F));
        out[2] = (char)(0x80 | (scalar & 0x3F));
        return 3;
    }

    out[0] = (char)(0xF0 | (scalar >> 18));
    out[1] = (char)(0x80 | ((scalar >> 12) & 0x3F));
    out[2] = (char)(0x80 | ((scalar >> 6) & 0x3F));
    out[3] = (char)(0x80 | (scalar & 0x3F));
    return 4;
}

void sable_print_string(const SableString *s) {
    fwrite(s->bytes, 1, (size_t)s->length, stdout);
    fputc('\n', stdout);
}

// Byte-for-byte equality: strings compare by content, not identity.
bool sable_string_equal(const SableString *a, const SableString *b) {
    return a->length == b->length && memcmp(a->bytes, b->bytes, (size_t)a->length) == 0;
}

// The pieces end to end, in one allocation: an interpolation, or 'a + b + c',
// rather than a string for every '+'.
SableString *sable_string_concat_many(int64_t n, SableString *const *pieces) {
    int64_t length = 0, count = 0;
    for (int64_t i = 0; i < n; i++) {
        length += pieces[i]->length;
        count += pieces[i]->count;
    }

    SableString *s = new_string(length, count);
    char *end = s->bytes;
    for (int64_t i = 0; i < n; i++) {
        memcpy(end, pieces[i]->bytes, (size_t)pieces[i]->length);
        end += pieces[i]->length;
    }

    return s;
}

// A hash of the bytes, eight at a time, never negative: what Sable.Core's
// String.hash() gives. The interpreter computes exactly this
// (SilInterpreter.StringHash), so the two print the same.
int64_t sable_string_hash(const SableString *s) {
    const uint64_t multiplier = 0x9E3779B97F4A7C15ull;
    const unsigned char *bytes = (const unsigned char *)s->bytes;
    uint64_t h = 0x243F6A8885A308D3ull ^ (uint64_t)s->length;
    int64_t i = 0;
    for (; i + 8 <= s->length; i += 8) {
        uint64_t word = 0;
        for (int b = 0; b < 8; b++) {
            word |= (uint64_t)bytes[i + b] << (8 * b);
        }

        h = (h ^ word) * multiplier;
        h ^= h >> 29;
    }

    if (i < s->length) {
        uint64_t tail = 0;
        for (int shift = 0; i < s->length; i++, shift += 8) {
            tail |= (uint64_t)bytes[i] << shift;
        }

        h = (h ^ tail) * multiplier;
        h ^= h >> 29;
    }

    h ^= h >> 32;
    h *= 0xD6E8FEB86659FD93ull;
    h ^= h >> 32;
    return (int64_t)(h & INT64_MAX);
}

// Eight bytes from 'p' as one number whose order is the bytes' order: the
// first byte the most significant.
static inline uint64_t load_ordered(const char *p) {
    uint64_t word;
    memcpy(&word, p, sizeof word);
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
    word = __builtin_bswap64(word);
#endif
    return word;
}

// -1, 0 or 1 as 'a' orders before, with or after 'b': byte by byte, which
// for UTF-8 is scalar by scalar, a prefix first. Eight bytes are compared at
// a time, as numbers whose order is the bytes' (a sort's keys are mostly
// short and the byte loop's branches mispredict); the last eight end at the
// shorter's end, reaching back over bytes already found equal or, when it is
// shorter than eight, into the 'count' field before the bytes, which the
// shift then drops.
int64_t sable_string_compare(const SableString *a, const SableString *b) {
    int64_t shorter = a->length < b->length ? a->length : b->length;
    if (shorter > 0) {
        int64_t i = 0;
        for (; i + 8 < shorter; i += 8) {
            uint64_t x = load_ordered(a->bytes + i), y = load_ordered(b->bytes + i);
            if (x != y) {
                return x < y ? -1 : 1;
            }
        }

        unsigned drop = (unsigned)(8 - (shorter - i)) * 8;
        uint64_t x = load_ordered(a->bytes + shorter - 8), y = load_ordered(b->bytes + shorter - 8);
        if (drop != 0) {
            x <<= drop;
            y <<= drop;
        }
        if (x != y) {
            return x < y ? -1 : 1;
        }
    }

    return a->length < b->length ? -1 : a->length > b->length ? 1 : 0;
}

SableString *sable_string_concat(const SableString *a, const SableString *b) {
    SableString *s = new_string(a->length + b->length, a->count + b->count);
    memcpy(s->bytes, a->bytes, (size_t)a->length);
    memcpy(s->bytes + a->length, b->bytes, (size_t)b->length);
    return s;
}

int64_t sable_string_count(const SableString *s) {
    return s->count;
}

int32_t sable_string_at(const SableString *s, int64_t index) {
    if (index < 0 || index >= s->count) {
        sable_trap("string index out of range");
    }

    int64_t width;
    return decode(s->bytes + offset_of(s, index), &width);
}

SableString *sable_string_slice(const SableString *s, int64_t from, int64_t to) {
    if (from < 0 || to > s->count || from > to) {
        sable_trap("string slice out of range");
    }

    // Of an ASCII string, scalars are bytes: nothing to count.
    if (s->count == s->length) {
        return make_counted(s->bytes + from, to - from, to - from);
    }

    int64_t start = offset_of(s, from);
    int64_t end = offset_of(s, to);
    return make_string(s->bytes + start, end - start);
}

int64_t sable_string_index_of(const SableString *s, int32_t scalar) {
    int64_t index = 0;
    for (int64_t i = 0; i < s->length; index++) {
        int64_t width;
        if (decode(s->bytes + i, &width) == scalar) {
            return index;
        }

        i += width;
    }

    return -1;
}

bool sable_string_contains(const SableString *s, int32_t scalar) {
    return sable_string_index_of(s, scalar) >= 0;
}

// The whitespace the runtime knows: ASCII's.
static bool is_space(int c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
}

SableString *sable_string_trimmed(const SableString *s) {
    int64_t start = 0;
    int64_t end = s->length;
    while (start < end && is_space(s->bytes[start])) {
        start++;
    }

    while (end > start && is_space(s->bytes[end - 1])) {
        end--;
    }

    return make_string(s->bytes + start, end - start);
}

SableString *sable_string_from_char(int32_t scalar) {
    char bytes[4];
    return make_string(bytes, encode(scalar, bytes));
}

// Digits written backwards into a buffer, not through snprintf: an
// interpolated number is common, and the format machinery is most of its cost.
SableString *sable_string_from_int(int64_t value) {
    char text[24];
    char *end = text + sizeof text, *start = end;
    uint64_t magnitude = value < 0 ? 0 - (uint64_t)value : (uint64_t)value;
    do {
        *--start = (char)('0' + magnitude % 10);
        magnitude /= 10;
    } while (magnitude != 0);

    if (value < 0) {
        *--start = '-';
    }

    return make_counted(start, end - start, end - start);
}

// The shortest %.Ng that reads back as the same double — 0.1, not
// 0.10000000000000001. %.17g always does. The interpreter formats the same way.
static void format_f64(double value, char *text, size_t size) {
    if (isnan(value)) {
        snprintf(text, size, "nan");
        return;
    }

    for (int precision = 1; precision < 17; precision++) {
        snprintf(text, size, "%.*g", precision, value);
        if (strtod(text, NULL) == value) {
            return;
        }
    }

    snprintf(text, size, "%.17g", value);
}

SableString *sable_string_from_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
    return make_cstring(text);
}

// F32 the same way, against a float: 1.1, not 1.10000002.
static void format_f32(float value, char *text, size_t size) {
    if (isnan(value)) {
        snprintf(text, size, "nan");
        return;
    }

    for (int precision = 1; precision < 9; precision++) {
        snprintf(text, size, "%.*g", precision, (double)value);
        if (strtof(text, NULL) == value) {
            return;
        }
    }

    snprintf(text, size, "%.9g", (double)value);
}

SableString *sable_string_from_f32(float value) {
    char text[40];
    format_f32(value, text, sizeof text);
    return make_cstring(text);
}

SableString *sable_string_from_uint(uint64_t value) {
    char text[32];
    snprintf(text, sizeof text, "%llu", (unsigned long long)value);
    return make_cstring(text);
}

SableString *sable_string_from_bool(bool value) {
    return make_cstring(value ? "true" : "false");
}

// An optional sign and decimal digits, and nothing else.
bool sable_string_to_int(const SableString *s, int64_t *out) {
    char text[32];
    int64_t i = s->length > 0 && (s->bytes[0] == '+' || s->bytes[0] == '-') ? 1 : 0;
    if (i >= s->length || s->length >= (int64_t)sizeof text) {
        return false;
    }

    for (int64_t j = i; j < s->length; j++) {
        if (s->bytes[j] < '0' || s->bytes[j] > '9') {
            return false;
        }
    }

    memcpy(text, s->bytes, (size_t)s->length);
    text[s->length] = '\0';
    errno = 0;
    long long value = strtoll(text, NULL, 10);
    if (errno == ERANGE) {
        return false;
    }

    *out = value;
    return true;
}

// A decimal number — sign, digits, fraction, exponent — and nothing else: no
// infinities, NaN, hex or surrounding space, which strtod would also take —
// that is finite as a double.
bool sable_string_to_f64(const SableString *s, double *out) {
    const char *b = s->bytes;
    int64_t n = s->length, i = 0, digits = 0;
    if (i < n && (b[i] == '+' || b[i] == '-')) {
        i++;
    }

    while (i < n && b[i] >= '0' && b[i] <= '9') {
        i++;
        digits++;
    }

    if (i < n && b[i] == '.') {
        i++;
        while (i < n && b[i] >= '0' && b[i] <= '9') {
            i++;
            digits++;
        }
    }

    if (digits == 0) {
        return false;
    }

    if (i < n && (b[i] == 'e' || b[i] == 'E')) {
        i++;
        if (i < n && (b[i] == '+' || b[i] == '-')) {
            i++;
        }

        int64_t exponent = 0;
        while (i < n && b[i] >= '0' && b[i] <= '9') {
            i++;
            exponent++;
        }

        if (exponent == 0) {
            return false;
        }
    }

    if (i != n) {
        return false;
    }

    char *text = malloc((size_t)n + 1);
    memcpy(text, b, (size_t)n);
    text[n] = '\0';
    double value = strtod(text, NULL);
    free(text);

    // Too large for a double does not fit, as for sable_string_to_int.
    if (isinf(value)) {
        return false;
    }

    *out = value;
    return true;
}

int64_t sable_char_to_int(int32_t c) {
    return c;
}

// The scalar for a code point, if it is one: not a surrogate, not past U+10FFFF.
bool sable_char_from_int(int64_t code, int32_t *out) {
    if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) {
        *out = 0;
        return false;
    }

    *out = (int32_t)code;
    return true;
}

bool sable_char_is_digit(int32_t c) {
    return c >= '0' && c <= '9';
}

bool sable_char_is_letter(int32_t c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

bool sable_char_is_whitespace(int32_t c) {
    return is_space(c);
}

void sable_print_int(int64_t value) {
    printf("%lld\n", (long long)value);
}

void sable_print_uint(uint64_t value) {
    printf("%llu\n", (unsigned long long)value);
}

void sable_print_f32(float value) {
    char text[40];
    format_f32(value, text, sizeof text);
    puts(text);
}

void sable_print_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
    puts(text);
}

void sable_print_bool(bool value) {
    fputs(value ? "true\n" : "false\n", stdout);
}

// ---- Arrays ------------------------------------------------------------

// A counted buffer of elements, copied before it is changed while shared.
// The empty array is no buffer: a null pointer.
typedef struct SableArray {
    SableObject header;
    int64_t count;
    int64_t capacity;
    char *elements;
} SableArray;

void sable_array_destroy(void *pointer) {
    SableArray *array = pointer;
    const SableMetadata *type = array->header.type;
    if (type->destroy_element != NULL) {
        for (int64_t i = 0; i < array->count; i++) {
            type->destroy_element(array->elements + i * type->element_size);
        }
    }

    buffer_free(array->elements, array->capacity * type->element_size);
}

int64_t sable_array_count(const SableArray *array) {
    return array == NULL ? 0 : array->count;
}

void *sable_array_element(const SableArray *array, int64_t index) {
    if (array == NULL || index < 0 || index >= array->count) {
        sable_trap("array index out of range");
    }

    return array->elements + index * array->header.type->element_size;
}

// The array at '*slot', made the only reference to its buffer, so that it
// can be changed: a fresh one for the empty array, a copy of a shared one.
static SableArray *unique(SableArray **slot, const SableMetadata *type, int64_t room) {
    SableArray *array = *slot;
    if (array != NULL && array->header.strong == 1) {
        return array;
    }

    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    if (array != NULL) {
        int64_t size = type->element_size;
        copy->capacity = array->count + room;
        copy->elements = buffer_alloc(copy->capacity * size);
        for (int64_t i = 0; i < array->count; i++) {
            if (type->copy_element != NULL) {
                type->copy_element(copy->elements + i * size, array->elements + i * size);
            } else {
                memcpy(copy->elements + i * size, array->elements + i * size, (size_t)size);
            }
        }

        copy->count = array->count;
        sable_release(array);
    }

    *slot = copy;
    return copy;
}

// Made the only reference to its buffer, before a loop that writes it in
// place: the empty array stays empty, and the loop's index checks trap.
void sable_array_make_unique(SableArray **slot, const SableMetadata *type) {
    if (*slot != NULL && (*slot)->header.strong != 1) {
        unique(slot, type, 0);
    }
}

// Room for one more element at the end; the caller stores it.
void *sable_array_append_slot(SableArray **slot, const SableMetadata *type) {
    SableArray *array = unique(slot, type, 1);
    if (array->count == array->capacity) {
        int64_t old = array->capacity * type->element_size;
        array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;
        array->elements = buffer_resize(array->elements, old, array->capacity * type->element_size);
    }

    return array->elements + array->count++ * type->element_size;
}

void *sable_array_mutable_element(SableArray **slot, const SableMetadata *type, int64_t index) {
    if (*slot == NULL || index < 0 || index >= (*slot)->count) {
        sable_trap("array index out of range");
    }

    SableArray *array = unique(slot, type, 0);
    return array->elements + index * type->element_size;
}

// The last element, moved out: the caller loads it, and owns it.
void *sable_array_remove_last(SableArray **slot, const SableMetadata *type) {
    if (*slot == NULL || (*slot)->count == 0) {
        sable_trap("removeLast of an empty array");
    }

    SableArray *array = unique(slot, type, 0);
    return array->elements + --array->count * type->element_size;
}

// A copy of elements [from, to): a buffer of its own, each element copied.
SableArray *sable_array_slice(const SableArray *array, int64_t from, int64_t to, const SableMetadata *type) {
    int64_t count = sable_array_count(array);
    if (from < 0 || to > count || from > to) {
        sable_trap("array slice out of range");
    }

    if (from == to) {
        return NULL;
    }

    int64_t size = type->element_size;
    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    copy->capacity = to - from;
    copy->count = to - from;
    copy->elements = buffer_alloc(copy->capacity * size);
    for (int64_t i = 0; i < copy->count; i++) {
        const char *source = array->elements + (from + i) * size;
        if (type->copy_element != NULL) {
            type->copy_element(copy->elements + i * size, source);
        } else {
            memcpy(copy->elements + i * size, source, (size_t)size);
        }
    }

    return copy;
}

static void copy_string_element(void *destination, const void *source) {
    memcpy(destination, source, sizeof(void *));
    sable_retain(*(void *const *)source);
}

static void destroy_string_element(void *element) {
    sable_release(*(void **)element);
}

static const SableMetadata string_array_metadata = {
    sable_array_destroy, "[String]", sizeof(void *), copy_string_element, destroy_string_element,
};

SableArray *sable_string_split(const SableString *s, int32_t separator) {
    // An ASCII separator is a byte that never occurs inside another scalar's
    // encoding: the parts are found with memchr, counted first so that the
    // array is made once at its size, and an ASCII string's parts need no
    // scalars counted.
    if (separator < 0x80) {
        int64_t n = 1;
        for (const char *at = s->bytes, *end = s->bytes + s->length;
             (at = memchr(at, separator, (size_t)(end - at))) != NULL; at++) {
            n++;
        }

        SableArray *parts = sable_alloc(&string_array_metadata, (int64_t)sizeof(SableArray));
        parts->elements = buffer_alloc(n * (int64_t)sizeof(SableString *));
        parts->capacity = n;
        bool ascii = s->count == s->length;
        const char *start = s->bytes, *end = s->bytes + s->length;
        for (int64_t i = 0; i < n; i++) {
            const char *stop = i + 1 < n ? memchr(start, separator, (size_t)(end - start)) : end;
            int64_t length = stop - start;
            ((SableString **)parts->elements)[i] = ascii ? make_counted(start, length, length) : make_string(start, length);
            parts->count = i + 1;
            start = stop + 1;
        }

        return parts;
    }

    SableArray *parts = NULL;
    int64_t start = 0;
    for (int64_t i = 0; i <= s->length;) {
        int64_t width = 1;
        if (i == s->length || decode(s->bytes + i, &width) == separator) {
            SableString *part = make_string(s->bytes + start, i - start);
            *(SableString **)sable_array_append_slot(&parts, &string_array_metadata) = part;
            start = i + width;
        }

        i += width;
    }

    return parts;
}

// ---- Inline layout -------------------------------------------------------

// An @inline_layout value (docs/02 §2.5) is a block: one counted object
// holding the value's bytes, copied on write. The bytes are the layout the
// compiler describes for the runtime: the fixed-size fields first, packed,
// then each variable-size field in declaration order — a bounded array's
// count in the fewest bytes its bound needs, a grow-only one's as a LEB128
// varint, then the elements; a relative reference a signed offset from its
// own address, 0 for null. Nothing in it is an absolute pointer, so the
// bytes are the value wherever they are copied to.

enum {
    INLINE_SCALAR = 0, INLINE_BOOL = 1, INLINE_CHAR = 2, INLINE_REF = 3,
    INLINE_STRUCT = 4, INLINE_BOUNDED = 5, INLINE_GROW = 6,
};

typedef struct SableInlineLayout SableInlineLayout;

typedef struct SableInlineSlot {
    int32_t offset;        // in the prefix; -1 for a variable-size field
    int32_t kind;
    int32_t width;         // a scalar's or a reference's bytes; a bounded count's
    int32_t optional;      // a reference that may be null
    int32_t capacity;      // a bounded array's bound
    int32_t element_kind;  // an array's element: scalar, Bool, Char or struct
    int32_t element_width; // its bytes, when not a struct
    const SableInlineLayout *layout; // a struct's, an element's, a reference's target
} SableInlineSlot;

struct SableInlineLayout {
    const char *name;
    int32_t prefix;     // the fixed-size fields' bytes
    int32_t fixed_size; // every value's size, or -1 when it varies
    int32_t empty_size; // the empty value's size: zero counts, and so on down
    int32_t references; // whether a value of it can hold a relative reference
    int32_t count;
    SableInlineSlot slots[];
};

typedef struct SableBlock {
    SableObject header;
    int64_t length, capacity;
    uint8_t bytes[];
} SableBlock;

static const SableMetadata sable_block_metadata = { NULL, "inline block", 0, NULL, NULL };

static uint32_t read_unsigned(const uint8_t *p, int width) {
    uint32_t value = 0;
    for (int i = 0; i < width; i++) {
        value |= (uint32_t)p[i] << (8 * i);
    }

    return value;
}

static void write_unsigned(uint8_t *p, int width, uint32_t value) {
    for (int i = 0; i < width; i++) {
        p[i] = (uint8_t)(value >> (8 * i));
    }
}

// A varint's value and bytes, trusting it: the block was validated or built here.
static int64_t read_varint(const uint8_t *p, int *size) {
    int64_t value = 0;
    int i = 0;
    do {
        value |= (int64_t)(p[i] & 0x7F) << (7 * i);
    } while (p[i++] & 0x80);
    *size = i;
    return value;
}

static int write_varint(uint8_t *p, int64_t value) {
    int i = 0;
    do {
        uint8_t byte = value & 0x7F;
        value >>= 7;
        p[i++] = byte | (value != 0 ? 0x80 : 0);
    } while (value != 0);
    return i;
}

static int varint_size(int64_t value) {
    uint8_t scratch[10];
    return write_varint(scratch, value);
}

static const uint8_t *skip_value(const SableInlineLayout *layout, const uint8_t *p);

// An array's count, and where its elements start.
static int64_t array_count(const SableInlineSlot *slot, const uint8_t *array, const uint8_t **elements) {
    if (slot->kind == INLINE_BOUNDED) {
        *elements = array + slot->width;
        return read_unsigned(array, slot->width);
    }

    int size;
    int64_t count = read_varint(array, &size);
    *elements = array + size;
    return count;
}

static const uint8_t *skip_element(const SableInlineSlot *slot, const uint8_t *p) {
    return slot->element_kind == INLINE_STRUCT ? skip_value(slot->layout, p) : p + slot->element_width;
}

static const uint8_t *skip_slot(const SableInlineSlot *slot, const uint8_t *p) {
    if (slot->kind == INLINE_STRUCT) {
        return skip_value(slot->layout, p);
    }

    const uint8_t *element;
    int64_t count = array_count(slot, p, &element);
    if (slot->element_kind != INLINE_STRUCT) {
        return element + count * slot->element_width;
    }

    if (slot->layout->fixed_size >= 0) {
        return element + count * slot->layout->fixed_size;
    }

    for (int64_t i = 0; i < count; i++) {
        element = skip_value(slot->layout, element);
    }

    return element;
}

static const uint8_t *skip_value(const SableInlineLayout *layout, const uint8_t *p) {
    if (layout->fixed_size >= 0) {
        return p + layout->fixed_size;
    }

    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < layout->count; i++) {
        if (layout->slots[i].offset < 0) {
            q = skip_slot(&layout->slots[i], q);
        }
    }

    return q;
}

// The empty value's size: zero counts, and so on down — worked out by the compiler.
static int64_t empty_size(const SableInlineLayout *layout) {
    return layout->empty_size;
}

uint8_t *sable_inline_field(const SableInlineLayout *layout, uint8_t *p, int32_t slot) {
    if (layout->slots[slot].offset >= 0) {
        return p + layout->slots[slot].offset;
    }

    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < slot; i++) {
        if (layout->slots[i].offset < 0) {
            q = skip_slot(&layout->slots[i], q);
        }
    }

    return (uint8_t *)q;
}

int64_t sable_inline_count(const SableInlineSlot *s, const uint8_t *array) {
    const uint8_t *elements;
    return array_count(s, array, &elements);
}

uint8_t *sable_inline_first(const SableInlineSlot *s, uint8_t *array) {
    const uint8_t *elements;
    array_count(s, array, &elements);
    return (uint8_t *)elements;
}

uint8_t *sable_inline_next(const SableInlineSlot *s, uint8_t *element) {
    return (uint8_t *)skip_element(s, element);
}

uint8_t *sable_inline_at(const SableInlineSlot *s, uint8_t *array, int64_t index) {
    const uint8_t *elements;
    int64_t count = array_count(s, array, &elements);
    if (index < 0 || index >= count) {
        sable_trap("array index out of range");
    }

    int64_t size = s->element_kind == INLINE_STRUCT ? s->layout->fixed_size : s->element_width;
    return (uint8_t *)elements + index * size;
}

static SableBlock *new_block(int64_t length, int64_t capacity) {
    SableBlock *block = sable_alloc(&sable_block_metadata, (int64_t)sizeof(SableBlock) + capacity);
    block->length = length;
    block->capacity = capacity;
    return block;
}

SableBlock *sable_inline_new(const SableInlineLayout *layout) {
    int64_t size = empty_size(layout);
    return new_block(size, size);
}

// A block in the caller's frame, with room for 'capacity' bytes; one that
// outgrows it is copied to the heap, as a shared one is.
SableBlock *sable_inline_new_stack(void *memory, const SableInlineLayout *layout, int64_t capacity) {
    int64_t size = empty_size(layout);
    if (size > capacity) {
        return sable_inline_new(layout);
    }

    // The header and the empty value's bytes: the rest is written before it is read.
    SableBlock *block = memory;
    block->header.type = &sable_block_metadata;
    block->header.strong = 1;
    block->header.flags = SABLE_STACK;
    live_objects++;
    block->length = size;
    block->capacity = capacity;
    memset(block->bytes, 0, (size_t)size);
    return block;
}

// The block at '*slot', made the only reference to its bytes, with room
// for 'room' more: a copy of a shared one, a larger one for a full one.
static SableBlock *unique_block(SableBlock **slot, int64_t room) {
    SableBlock *block = *slot;
    int64_t needed = block->length + room;
    if (block->header.strong == 1 && !(block->header.flags & SABLE_IMMORTAL) && needed <= block->capacity) {
        return block;
    }

    int64_t capacity = needed <= block->capacity ? block->capacity : needed < 2 * block->capacity ? 2 * block->capacity : needed;
    // Grown in place, by realloc, only when it came from malloc: one from a
    // size class is copied below, as a shared one is.
    if (block->header.strong == 1 && !(block->header.flags & (SABLE_IMMORTAL | SABLE_STACK | SABLE_CLASS_MASK))) {
        SableBlock *grown = realloc(block, sizeof(SableBlock) + (size_t)capacity);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        grown->capacity = capacity;
        *slot = grown;
        return grown;
    }

    SableBlock *copy = new_block(block->length, capacity);
    memcpy(copy->bytes, block->bytes, (size_t)block->length);
    copy->header.flags |= block->header.flags & SABLE_LINKED;
    sable_release(block);
    *slot = copy;
    return copy;
}

uint8_t *sable_inline_unique(SableBlock **slot) {
    return unique_block(slot, 0)->bytes;
}

SableBlock *sable_inline_copy(const SableInlineLayout *layout, const uint8_t *p) {
    int64_t size = skip_value(layout, p) - p;
    SableBlock *block = new_block(size, size);
    block->header.flags |= SABLE_LINKED;
    memcpy(block->bytes, p, (size_t)size);
    return block;
}

// Every relative reference in a value: where it is, and where it points.
typedef struct InlineLink {
    int64_t at, target;
    int32_t width;
} InlineLink;

typedef struct InlineLinks {
    InlineLink *links;
    int64_t count, capacity;
} InlineLinks;

static void collect_value(const SableInlineLayout *layout, const uint8_t *base, const uint8_t *p, InlineLinks *links);

static void collect_element(const SableInlineSlot *slot, const uint8_t *base, const uint8_t *p, InlineLinks *links) {
    if (slot->element_kind == INLINE_STRUCT) {
        collect_value(slot->layout, base, p, links);
    }
}

static void collect_value(const SableInlineLayout *layout, const uint8_t *base, const uint8_t *p, InlineLinks *links) {
    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < layout->count; i++) {
        const SableInlineSlot *slot = &layout->slots[i];
        const uint8_t *field = slot->offset >= 0 ? p + slot->offset : q;
        if (slot->kind == INLINE_REF) {
            int32_t offset = slot->width == 2 ? (int16_t)read_unsigned(field, 2) : slot->width == 1 ? (int8_t)field[0] : (int32_t)read_unsigned(field, 4);
            if (offset != 0) {
                if (links->count == links->capacity) {
                    links->capacity = links->capacity < 8 ? 8 : links->capacity * 2;
                    links->links = realloc(links->links, (size_t)links->capacity * sizeof(InlineLink));
                }

                links->links[links->count++] = (InlineLink){ field - base, field - base + offset, slot->width };
            }
        } else if (slot->kind == INLINE_STRUCT) {
            collect_value(slot->layout, base, field, links);
        } else if (slot->kind == INLINE_BOUNDED || slot->kind == INLINE_GROW) {
            const uint8_t *element;
            int64_t count = array_count(slot, field, &element);
            for (int64_t j = 0; j < count; j++) {
                collect_element(slot, base, element, links);
                element = skip_element(slot, element);
            }
        }

        if (slot->offset < 0) {
            q = skip_slot(slot, q);
        }
    }
}

// Replaces 'removed' bytes at 'at' with 'inserted' ones, moving what
// follows, and keeps every relative reference pointing where it did: one
// whose field or target moved is measured again; one whose target went
// is a trap.
static SableBlock *splice(SableBlock **slot, const SableInlineLayout *layout, int64_t at, int64_t removed, const uint8_t *inserted, int64_t count) {
    SableBlock *block = unique_block(slot, count > removed ? count - removed : 0);
    InlineLinks links = { NULL, 0, 0 };
    if (block->header.flags & SABLE_LINKED) {
        collect_value(layout, block->bytes, block->bytes, &links);
    }

    int64_t delta = count - removed;
    if (block->length - at - removed > 0 && delta != 0) {
        memmove(block->bytes + at + count, block->bytes + at + removed, (size_t)(block->length - at - removed));
    }

    memcpy(block->bytes + at, inserted, (size_t)count);
    block->length += delta;

    for (int64_t i = 0; i < links.count; i++) {
        InlineLink link = links.links[i];
        if (link.at >= at && link.at < at + removed) {
            continue; // the reference itself was replaced
        }

        if (link.target >= at && link.target < at + removed && removed > 0) {
            sable_trap("a relative reference's target was replaced");
        }

        int64_t from = link.at >= at + removed ? link.at + delta : link.at;
        int64_t to = link.target >= at + removed ? link.target + delta : link.target;
        int64_t offset = to - from;
        if ((link.width == 2 && (offset < INT16_MIN || offset > INT16_MAX)) || (link.width == 1 && (offset < INT8_MIN || offset > INT8_MAX))) {
            sable_trap("a relative reference no longer fits its width");
        }

        write_unsigned(block->bytes + from, link.width, (uint32_t)offset);
    }

    free(links.links);
    return block;
}

// True when a value of the layout can hold a relative reference — worked out by the compiler.
static bool layout_has_references(const SableInlineLayout *layout, int depth) {
    (void)depth;
    return layout->references != 0;
}

void sable_inline_append_at(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *end, const uint8_t *element, int64_t size);

void sable_inline_append(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *element, int64_t size) {
    sable_inline_append_at(slot, root, s, array, skip_slot(s, array), element, size);
}

// As sable_inline_append, with where the array ends already found: the
// compiler's own walker, specialised to the layout, finds it.
void sable_inline_append_at(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *array_end, const uint8_t *element, int64_t size) {
    int64_t at = array - (*slot)->bytes;
    const uint8_t *elements;
    int64_t count = array_count(s, array, &elements);
    if (s->kind == INLINE_BOUNDED && count >= s->capacity) {
        sable_trap("inline array is full");
    }

    int64_t end = array_end - (*slot)->bytes;

    // A struct's size is read from it.
    if (size < 0) {
        size = skip_value(s->layout, element) - element;
    }

    // An element copied in from somewhere else in this block goes through a
    // copy of its own, since the splice may move or grow the block under it.
    SableBlock *block = *slot;
    uint8_t *copy = NULL;
    if (element >= block->bytes && element < block->bytes + block->length) {
        copy = malloc((size_t)(size > 0 ? size : 1));
        memcpy(copy, element, (size_t)size);
        element = copy;
    }

    // An element that may hold references of its own makes the block one that may.
    if (s->element_kind == INLINE_STRUCT && layout_has_references(s->layout, 0)) {
        block->header.flags |= SABLE_LINKED;
    }

    // The element first, then the count, which may take another byte.
    block = splice(slot, root, end, 0, element, size);
    free(copy);

    uint8_t encoded[10];
    int old_size = (int)(elements - array);
    int new_size;
    if (s->kind == INLINE_BOUNDED) {
        write_unsigned(encoded, s->width, (uint32_t)(count + 1));
        new_size = s->width;
    } else {
        new_size = write_varint(encoded, count + 1);
    }

    if (new_size == old_size) {
        memcpy(block->bytes + at, encoded, (size_t)new_size);
    } else {
        splice(slot, root, at, old_size, encoded, new_size);
    }
}

// Bytes as a bounded U8 array's elements: a string, as UTF-8.
void sable_inline_assign_bytes(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *bytes, int64_t count) {
    if (s->kind == INLINE_BOUNDED && count > s->capacity) {
        sable_trap("too many bytes for the inline array");
    }

    int64_t at = array - (*slot)->bytes;
    int64_t old_size = skip_slot(s, array) - array;
    int width = s->kind == INLINE_BOUNDED ? s->width : varint_size(count);

    // Short text, the usual case, is encoded on the stack.
    uint8_t small[256];
    uint8_t *encoded = width + count <= (int64_t)sizeof small ? small : malloc((size_t)(width + count));
    if (s->kind == INLINE_BOUNDED) {
        write_unsigned(encoded, width, (uint32_t)count);
    } else {
        write_varint(encoded, count);
    }

    memcpy(encoded + width, bytes, (size_t)count);
    splice(slot, root, at, old_size, encoded, width + count);
    if (encoded != small) {
        free(encoded);
    }
}

void sable_inline_assign_string(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const SableString *text) {
    sable_inline_assign_bytes(slot, root, s, array, (const uint8_t *)text->bytes, text->length);
}

// A nested struct replaced by another value of it.
void sable_inline_assign(SableBlock **slot, const SableInlineLayout *root, const SableInlineLayout *layout, uint8_t *field, const uint8_t *value) {
    int64_t at = field - (*slot)->bytes;
    int64_t old_size = skip_value(layout, field) - field;
    int64_t size = skip_value(layout, value) - value;
    SableBlock *block = *slot;
    uint8_t *copy = NULL;
    if (value >= block->bytes && value < block->bytes + block->length) {
        copy = malloc((size_t)(size > 0 ? size : 1));
        memcpy(copy, value, (size_t)size);
        value = copy;
    }

    // A value from another block may hold references of its own.
    block->header.flags |= SABLE_LINKED;
    splice(slot, root, at, old_size, value, size);
    free(copy);
}

uint8_t *sable_inline_ref_get(const uint8_t *field, int32_t width) {
    int32_t offset = width == 2 ? (int16_t)read_unsigned(field, 2) : width == 1 ? (int8_t)field[0] : (int32_t)read_unsigned(field, 4);
    return offset == 0 ? NULL : (uint8_t *)field + offset;
}

void sable_inline_ref_set(SableBlock **slot, uint8_t *field, int32_t width, const uint8_t *target) {
    SableBlock *block = *slot;
    if (target < block->bytes || target >= block->bytes + block->length) {
        sable_trap("a relative reference must point into its own block");
    }

    int64_t offset = target - field;
    if (offset == 0) {
        sable_trap("a relative reference cannot point at itself");
    }

    if ((width == 2 && (offset < INT16_MIN || offset > INT16_MAX)) || (width == 1 && (offset < INT8_MIN || offset > INT8_MAX))) {
        sable_trap("a relative reference does not fit its width");
    }

    write_unsigned(field, width, (uint32_t)offset);
    block->header.flags |= SABLE_LINKED;
}

// Bytes as text: UTF-8, each byte of an ill-formed sequence read as U+FFFD.
SableString *sable_inline_to_string(const SableInlineSlot *s, const uint8_t *array) {
    const uint8_t *bytes;
    int64_t count = array_count(s, array, &bytes);
    char *text = malloc((size_t)(count * 3 + 1));
    int64_t length = 0;
    for (int64_t i = 0; i < count;) {
        uint8_t b = bytes[i];
        int need = b < 0x80 ? 0 : b >= 0xC2 && b <= 0xDF ? 1 : b >= 0xE0 && b <= 0xEF ? 2 : b >= 0xF0 && b <= 0xF4 ? 3 : -1;
        bool ok = need >= 0 && i + need < count;
        for (int k = 1; ok && k <= need; k++) {
            uint8_t c = bytes[i + k];
            uint8_t low = 0x80, high = 0xBF;
            if (k == 1 && b == 0xE0) low = 0xA0;
            if (k == 1 && b == 0xED) high = 0x9F;
            if (k == 1 && b == 0xF0) low = 0x90;
            if (k == 1 && b == 0xF4) high = 0x8F;
            ok = c >= low && c <= high;
        }

        if (ok) {
            memcpy(text + length, bytes + i, (size_t)(need + 1));
            length += need + 1;
            i += need + 1;
        } else {
            memcpy(text + length, "\xEF\xBF\xBD", 3);
            length += 3;
            i++;
        }
    }

    SableString *result = make_string(text, length);
    free(text);
    return result;
}

SableArray *sable_inline_bytes(const SableInlineLayout *layout, const uint8_t *p, const SableMetadata *type) {
    int64_t size = skip_value(layout, p) - p;
    if (size == 0) {
        return NULL;
    }

    SableArray *array = sable_alloc(type, (int64_t)sizeof(SableArray));
    array->count = array->capacity = size;
    array->elements = buffer_alloc(size);
    memcpy(array->elements, p, (size_t)size);
    return array;
}

SableBlock *sable_inline_from(const SableArray *bytes) {
    int64_t size = sable_array_count(bytes);
    SableBlock *block = new_block(size, size);
    block->header.flags |= SABLE_LINKED;
    if (size > 0) {
        memcpy(block->bytes, bytes->elements, (size_t)size);
    }

    return block;
}

// ---- Validation ----------------------------------------------------------

// Trust nothing: every count, tag and length is checked against the end of
// the bytes before it is used, and every relative reference must point at
// the start of a value of its target's type, one the walk found.

typedef struct InlinePlace {
    int64_t at;
    const SableInlineLayout *layout;
} InlinePlace;

typedef struct InlineCheck {
    const uint8_t *base, *end;
    InlinePlace *places;
    int64_t place_count, place_capacity;
    InlineLink *links;
    const SableInlineLayout **targets;
    int64_t link_count, link_capacity;
    const char *why;
} InlineCheck;

static bool fail(InlineCheck *check, const char *why) {
    if (check->why == NULL) {
        check->why = why;
    }

    return false;
}

static bool check_value(InlineCheck *check, const SableInlineLayout *layout, const uint8_t *p, const uint8_t **out, int depth);

static bool check_scalar(InlineCheck *check, int kind, int width, const uint8_t *p) {
    if (check->end - p < width) {
        return fail(check, "truncated");
    }

    if (kind == INLINE_BOOL && p[0] > 1) {
        return fail(check, "a Bool that is neither 0 nor 1");
    }

    if (kind == INLINE_CHAR) {
        uint32_t c = read_unsigned(p, 4);
        if (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) {
            return fail(check, "a Char that is not a Unicode scalar value");
        }
    }

    return true;
}

static bool check_slot(InlineCheck *check, const SableInlineSlot *slot, const uint8_t *p, const uint8_t **out, int depth) {
    switch (slot->kind) {
        case INLINE_SCALAR:
        case INLINE_BOOL:
        case INLINE_CHAR:
            *out = p + slot->width;
            return check_scalar(check, slot->kind, slot->width, p);

        case INLINE_REF: {
            if (check->end - p < slot->width) {
                return fail(check, "truncated");
            }

            int32_t offset = slot->width == 2 ? (int16_t)read_unsigned(p, 2) : slot->width == 1 ? (int8_t)p[0] : (int32_t)read_unsigned(p, 4);
            if (offset == 0 && !slot->optional) {
                return fail(check, "a relative reference that may not be null is null");
            }

            if (offset != 0) {
                if (check->link_count == check->link_capacity) {
                    check->link_capacity = check->link_capacity < 8 ? 8 : check->link_capacity * 2;
                    check->links = realloc(check->links, (size_t)check->link_capacity * sizeof(InlineLink));
                    check->targets = realloc(check->targets, (size_t)check->link_capacity * sizeof(*check->targets));
                }

                check->links[check->link_count] = (InlineLink){ p - check->base, (p - check->base) + offset, slot->width };
                check->targets[check->link_count++] = slot->layout;
            }

            *out = p + slot->width;
            return true;
        }

        case INLINE_STRUCT:
            return check_value(check, slot->layout, p, out, depth + 1);

        default: {
            int64_t count;
            const uint8_t *element;
            if (slot->kind == INLINE_BOUNDED) {
                if (check->end - p < slot->width) {
                    return fail(check, "truncated");
                }

                count = read_unsigned(p, slot->width);
                if (count > slot->capacity) {
                    return fail(check, "an inline array longer than its bound");
                }

                element = p + slot->width;
            } else {
                count = 0;
                int i = 0;
                for (;; i++) {
                    if (i == 5) {
                        return fail(check, "a count that is not a varint");
                    }

                    if (p + i >= check->end) {
                        return fail(check, "truncated");
                    }

                    count |= (int64_t)(p[i] & 0x7F) << (7 * i);
                    if (!(p[i] & 0x80)) {
                        break;
                    }
                }

                if (count > INT32_MAX) {
                    return fail(check, "a count that is not a varint");
                }

                element = p + i + 1;
            }

            // Every element takes a byte at least, or the count is a lie.
            int64_t least = slot->element_kind == INLINE_STRUCT ? empty_size(slot->layout) : slot->element_width;
            if (least > 0 ? count > (check->end - element) / least : count > 65536) {
                return fail(check, "a count longer than the bytes");
            }

            for (int64_t i = 0; i < count; i++) {
                if (slot->element_kind == INLINE_STRUCT) {
                    if (!check_value(check, slot->layout, element, &element, depth + 1)) {
                        return false;
                    }
                } else {
                    if (!check_scalar(check, slot->element_kind, slot->element_width, element)) {
                        return false;
                    }

                    element += slot->element_width;
                }
            }

            *out = element;
            return true;
        }
    }
}

static bool check_value(InlineCheck *check, const SableInlineLayout *layout, const uint8_t *p, const uint8_t **out, int depth) {
    if (depth > 64) {
        return fail(check, "nested too deeply");
    }

    if (check->end - p < layout->prefix) {
        return fail(check, "truncated");
    }

    if (check->place_count == check->place_capacity) {
        check->place_capacity = check->place_capacity < 8 ? 8 : check->place_capacity * 2;
        check->places = realloc(check->places, (size_t)check->place_capacity * sizeof(InlinePlace));
    }

    check->places[check->place_count++] = (InlinePlace){ p - check->base, layout };

    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < layout->count; i++) {
        const SableInlineSlot *slot = &layout->slots[i];
        const uint8_t *after;
        if (slot->offset >= 0) {
            if (!check_slot(check, slot, p + slot->offset, &after, depth)) {
                return false;
            }
        } else {
            if (!check_slot(check, slot, q, &q, depth)) {
                return false;
            }
        }
    }

    *out = q;
    return true;
}

static int compare_places(const void *a, const void *b) {
    const InlinePlace *x = a, *y = b;
    return x->at < y->at ? -1 : x->at > y->at ? 1 : x->layout < y->layout ? -1 : x->layout > y->layout ? 1 : 0;
}

static const SableString empty_string_object = { { &sable_string_metadata, 1, SABLE_IMMORTAL }, 0, 0 };

// Null when the block holds a valid value of the layout; why not, otherwise.
SableString *sable_inline_invalid(const SableInlineLayout *layout, const SableBlock *block) {
    InlineCheck check = { block->bytes, block->bytes + block->length, NULL, 0, 0, NULL, NULL, 0, 0, NULL };
    const uint8_t *end;
    if (check_value(&check, layout, block->bytes, &end, 0) && end != check.end) {
        fail(&check, "bytes after the value");
    }

    if (check.why == NULL) {
        qsort(check.places, (size_t)check.place_count, sizeof(InlinePlace), compare_places);
        for (int64_t i = 0; i < check.link_count && check.why == NULL; i++) {
            InlinePlace key = { check.links[i].target, check.targets[i] };
            if (key.at < 0 || key.at >= block->length
                || bsearch(&key, check.places, (size_t)check.place_count, sizeof(InlinePlace), compare_places) == NULL) {
                fail(&check, "a relative reference that does not point at a value of its type");
            }
        }
    }

    free(check.places);
    free(check.links);
    free(check.targets);
    return check.why == NULL ? (SableString *)&empty_string_object : make_cstring(check.why);
}

// ---- Tasks -----------------------------------------------------------------

// Concurrency (docs/02 §2.7): tasks scheduled cooperatively. Each runs on a
// thread of its own, but one lock is held by whichever task is running, so
// exactly one runs at a time — the order is the scheduler's, and the counts
// need no atomics. A task gives the lock up only where it waits: on a task
// not yet finished, or at Task.yield(). Spawning queues a task; it runs when
// the running one next waits. The program's entry is a task too, and the
// program ends once every task has.

typedef struct SableTask {
    void (*body)(void *context);
    void *context;
    pthread_cond_t wake;
    bool done;
    bool released; // the program no longer holds it
    struct SableTask *next;     // in the run queue
    struct SableTask *waiters;  // tasks waiting for this one, through 'next_waiter'
    struct SableTask *next_waiter;
} SableTask;

static pthread_mutex_t task_lock = PTHREAD_MUTEX_INITIALIZER;
static SableTask main_task;
static SableTask *current_task;
static SableTask *queue_head, *queue_tail;

__attribute__((constructor)) static void sable_tasks_start(void) {
    pthread_mutex_lock(&task_lock);
    pthread_cond_init(&main_task.wake, NULL);
    current_task = &main_task;
}

static void enqueue(SableTask *task) {
    task->next = NULL;
    if (queue_tail != NULL) {
        queue_tail->next = task;
    } else {
        queue_head = task;
    }

    queue_tail = task;
}

static SableTask *dequeue(void) {
    SableTask *task = queue_head;
    if (task != NULL) {
        queue_head = task->next;
        if (queue_head == NULL) {
            queue_tail = NULL;
        }
    }

    return task;
}

// Hands the lock to the next task in the queue, and waits until a task hands it back.
static void switch_away(void) {
    SableTask *self = current_task;
    SableTask *next = dequeue();
    if (next == NULL) {
        sable_trap("every task is waiting: the program cannot go on");
    }

    current_task = next;
    pthread_cond_signal(&next->wake);
    while (current_task != self) {
        pthread_cond_wait(&self->wake, &task_lock);
    }
}

static void free_task(SableTask *task) {
    pthread_cond_destroy(&task->wake);
    free(task);
}

static void *task_thread(void *argument) {
    SableTask *self = argument;
    pthread_mutex_lock(&task_lock);
    while (current_task != self) {
        pthread_cond_wait(&self->wake, &task_lock);
    }

    self->body(self->context);

    // Done: whoever waits for it may go on.
    self->done = true;
    for (SableTask *waiter = self->waiters; waiter != NULL;) {
        SableTask *after = waiter->next_waiter;
        enqueue(waiter);
        waiter = after;
    }

    self->waiters = NULL;
    SableTask *next = dequeue();
    if (next == NULL) {
        sable_trap("every task is waiting: the program cannot go on");
    }

    current_task = next;
    pthread_cond_signal(&next->wake);
    if (self->released) {
        free_task(self);
    }

    pthread_mutex_unlock(&task_lock);
    return NULL;
}

// A new task, queued to run 'body(context)'; the program holds the handle until it releases it.
void *sable_task_spawn(void (*body)(void *), void *context) {
    SableTask *task = calloc(1, sizeof(SableTask));
    if (task == NULL) {
        sable_trap("out of memory");
    }

    task->body = body;
    task->context = context;
    pthread_cond_init(&task->wake, NULL);

    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    pthread_attr_setstacksize(&attributes, 8u << 20);
    pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
    pthread_t thread;
    if (pthread_create(&thread, &attributes, task_thread, task) != 0) {
        sable_trap("cannot start a task");
    }

    pthread_attr_destroy(&attributes);
    enqueue(task);
    return task;
}

// 'await task.value': waits until it has finished.
void sable_task_wait(void *handle) {
    SableTask *task = handle;
    while (!task->done) {
        SableTask *self = current_task;
        self->next_waiter = task->waiters;
        task->waiters = self;
        switch_away();
    }
}

// 'await Task.yield()': lets every task queued run before this one goes on.
void sable_task_yield(void) {
    if (queue_head == NULL) {
        return;
    }

    enqueue(current_task);
    switch_away();
}

void sable_task_release(void *handle) {
    SableTask *task = handle;
    if (task == NULL) {
        return;
    }

    if (task->done) {
        free_task(task);
    } else {
        task->released = true;
    }
}

// As the entry point returns: every task still to run, run.
void sable_tasks_drain(void) {
    while (queue_head != NULL) {
        sable_task_yield();
    }
}

// ---- The operating system (Sable.IO's floor) ----------------------------

// A call that can fail gives a value either way — an empty string, an empty
// array — and sets this; the compiler reads it straight after, to make the
// optional. One task runs at a time, so one flag serves them all.
static bool io_last_failed;

bool sable_io_succeeded(void) {
    return !io_last_failed;
}

static int saved_argc;
static char **saved_argv;

void sable_set_arguments(int32_t argc, char **argv) {
    saved_argc = argc;
    saved_argv = argv;
}

// The arguments after the program's own name.
int64_t sable_io_argument_count(void) {
    return saved_argc > 1 ? saved_argc - 1 : 0;
}

SableString *sable_io_argument(int64_t index) {
    if (index < 0 || index >= sable_io_argument_count()) {
        sable_trap("argument index out of range");
    }

    return make_cstring(saved_argv[index + 1]);
}

// A string as a C path: its bytes, NUL-terminated, in memory the caller frees.
static char *c_string(const SableString *s) {
    char *text = malloc((size_t)s->length + 1);
    if (text == NULL) {
        sable_trap("out of memory");
    }

    memcpy(text, s->bytes, (size_t)s->length);
    text[s->length] = '\0';
    return text;
}

void sable_io_write(const SableString *s) {
    fwrite(s->bytes, 1, (size_t)s->length, stdout);
}

void sable_io_write_error(const SableString *s) {
    fflush(stdout);
    fwrite(s->bytes, 1, (size_t)s->length, stderr);
}

// A line of standard input without its line ending; none at the end of input.
SableString *sable_io_read_line(void) {
    fflush(stdout);
    size_t capacity = 128;
    size_t length = 0;
    char *line = malloc(capacity);
    if (line == NULL) {
        sable_trap("out of memory");
    }

    int c;
    bool any = false;
    while ((c = fgetc(stdin)) != EOF) {
        any = true;
        if (c == '\n') {
            break;
        }

        if (length + 1 >= capacity) {
            capacity *= 2;
            char *grown = realloc(line, capacity);
            if (grown == NULL) {
                free(line);
                sable_trap("out of memory");
            }

            line = grown;
        }

        line[length++] = (char)c;
    }

    if (length > 0 && line[length - 1] == '\r') {
        length--;
    }

    io_last_failed = !any;
    SableString *result = make_string(line, (int64_t)length);
    free(line);
    return result;
}

SableString *sable_io_read_file(const SableString *path) {
    char *name = c_string(path);
    FILE *file = fopen(name, "rb");
    free(name);
    if (file == NULL) {
        io_last_failed = true;
        return make_string("", 0);
    }

    size_t capacity = 4096;
    size_t length = 0;
    char *bytes = malloc(capacity);
    if (bytes == NULL) {
        sable_trap("out of memory");
    }

    size_t read;
    while ((read = fread(bytes + length, 1, capacity - length, file)) > 0) {
        length += read;
        if (length == capacity) {
            capacity *= 2;
            char *grown = realloc(bytes, capacity);
            if (grown == NULL) {
                free(bytes);
                sable_trap("out of memory");
            }

            bytes = grown;
        }
    }

    io_last_failed = ferror(file) != 0;
    fclose(file);
    SableString *result = make_string(bytes, (int64_t)length);
    free(bytes);
    return result;
}

bool sable_io_write_file(const SableString *path, const SableString *text, bool append) {
    char *name = c_string(path);
    FILE *file = fopen(name, append ? "ab" : "wb");
    free(name);
    if (file == NULL) {
        return false;
    }

    bool written = fwrite(text->bytes, 1, (size_t)text->length, file) == (size_t)text->length;
    return fclose(file) == 0 && written;
}

#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>

bool sable_io_file_exists(const SableString *path) {
    char *name = c_string(path);
    struct stat info;
    bool exists = stat(name, &info) == 0;
    free(name);
    return exists;
}

bool sable_io_is_directory(const SableString *path) {
    char *name = c_string(path);
    struct stat info;
    bool directory = stat(name, &info) == 0 && S_ISDIR(info.st_mode);
    free(name);
    return directory;
}

// A file, or an empty directory.
bool sable_io_remove_file(const SableString *path) {
    char *name = c_string(path);
    bool removed = remove(name) == 0;
    free(name);
    return removed;
}

bool sable_io_make_directory(const SableString *path) {
    char *name = c_string(path);
    bool made = mkdir(name, 0777) == 0;
    free(name);
    return made;
}

static int compare_names(const void *a, const void *b) {
    return strcmp(*(char *const *)a, *(char *const *)b);
}

// The entries' names, sorted byte by byte so that every platform agrees;
// '.' and '..' left out.
SableArray *sable_io_list_directory(const SableString *path) {
    char *name = c_string(path);
    DIR *directory = opendir(name);
    free(name);
    SableArray *entries = NULL;
    if (directory == NULL) {
        io_last_failed = true;
        return entries;
    }

    size_t count = 0;
    size_t capacity = 16;
    char **names = malloc(capacity * sizeof(char *));
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }

        if (count == capacity) {
            capacity *= 2;
            names = realloc(names, capacity * sizeof(char *));
        }

        names[count++] = strdup(entry->d_name);
    }

    closedir(directory);
    qsort(names, count, sizeof(char *), compare_names);
    for (size_t i = 0; i < count; i++) {
        *(SableString **)sable_array_append_slot(&entries, &string_array_metadata) = make_cstring(names[i]);
        free(names[i]);
    }

    free(names);
    io_last_failed = false;
    return entries;
}

SableString *sable_io_environment(const SableString *variable) {
    char *name = c_string(variable);
    const char *value = getenv(name);
    free(name);
    io_last_failed = value == NULL;
    return value == NULL ? make_string("", 0) : make_cstring(value);
}

// ---- Time (Sable.Time's floor) ------------------------------------------

#include <time.h>

int64_t sable_time_monotonic(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

int64_t sable_time_wall_clock(void) {
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

// At least as long as asked: nanosleep resumes after a signal.
void sable_time_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0) {
        return;
    }

    struct timespec wait = { (time_t)(nanoseconds / 1000000000), (long)(nanoseconds % 1000000000) };
    while (nanosleep(&wait, &wait) != 0 && errno == EINTR) {
    }
}

// ---- Processes (Sable.Process's floor) ----------------------------------

#include <poll.h>
#include <spawn.h>
#include <sys/wait.h>

extern char **environ;

static int64_t process_last_status;

// Plain memory, not a Sable string: the runtime keeps it past the program's
// own objects, and the leak check counts only those.
static char *process_last_errors;
static size_t process_last_errors_length;

int64_t sable_process_status(void) {
    return process_last_status;
}

SableString *sable_process_error_output(void) {
    return make_string(process_last_errors == NULL ? "" : process_last_errors, (int64_t)process_last_errors_length);
}

typedef struct {
    char *bytes;
    size_t length;
    size_t capacity;
} Buffer;

static void buffer_add(Buffer *buffer, const char *bytes, size_t length) {
    if (buffer->length + length > buffer->capacity) {
        size_t capacity = buffer->capacity == 0 ? 4096 : buffer->capacity;
        while (capacity < buffer->length + length) {
            capacity *= 2;
        }

        char *grown = realloc(buffer->bytes, capacity);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        buffer->bytes = grown;
        buffer->capacity = capacity;
    }

    memcpy(buffer->bytes + buffer->length, bytes, length);
    buffer->length += length;
}

// The program's path: the name itself when it holds a '/', else the first
// executable of that name on PATH; NULL when there is none. Looked up here
// rather than left to posix_spawnp, which does not report a failed exec
// everywhere (under emulation, the child just exits 127).
static char *find_program(const char *name) {
    if (strchr(name, '/') != NULL) {
        return access(name, X_OK) == 0 ? strdup(name) : NULL;
    }

    const char *path = getenv("PATH");
    if (path == NULL || *name == '\0') {
        return NULL;
    }

    size_t name_length = strlen(name);
    while (*path != '\0') {
        const char *end = strchr(path, ':');
        size_t length = end == NULL ? strlen(path) : (size_t)(end - path);
        char *candidate = malloc(length + name_length + 2);
        if (length == 0) {
            memcpy(candidate, ".", 1);
            length = 1;
        } else {
            memcpy(candidate, path, length);
        }

        candidate[length] = '/';
        memcpy(candidate + length + 1, name, name_length + 1);
        struct stat info;
        if (access(candidate, X_OK) == 0 && stat(candidate, &info) == 0 && S_ISREG(info.st_mode)) {
            return candidate;
        }

        free(candidate);
        if (end == NULL) {
            break;
        }

        path = end + 1;
    }

    return NULL;
}

// Runs the program, found on PATH, to its end: the input written to it and
// its output and errors read as they come, all three at once, so that
// neither side waits on a full pipe. Gives what it wrote to standard
// output; how it ended and its errors are kept for the calls above.
SableString *sable_process_run(const SableString *program, const SableArray *arguments, const SableString *input) {
    int64_t count = arguments == NULL ? 0 : arguments->count;
    char **argv = malloc((size_t)(count + 2) * sizeof(char *));
    argv[0] = c_string(program);
    for (int64_t i = 0; i < count; i++) {
        argv[i + 1] = c_string(((SableString **)arguments->elements)[i]);
    }

    argv[count + 1] = NULL;

    char *found = find_program(argv[0]);
    if (found == NULL) {
        for (int64_t i = 0; i <= count; i++) {
            free(argv[i]);
        }

        free(argv);
        io_last_failed = true;
        return make_string("", 0);
    }

    int in[2], out[2], err[2];
    bool piped = pipe(in) == 0;
    piped = piped && pipe(out) == 0;
    piped = piped && pipe(err) == 0;

    pid_t child = 0;
    int spawned = -1;
    if (piped) {
        posix_spawn_file_actions_t actions;
        posix_spawn_file_actions_init(&actions);
        posix_spawn_file_actions_adddup2(&actions, in[0], 0);
        posix_spawn_file_actions_adddup2(&actions, out[1], 1);
        posix_spawn_file_actions_adddup2(&actions, err[1], 2);
        int ends[] = { in[0], in[1], out[0], out[1], err[0], err[1] };
        for (int i = 0; i < 6; i++) {
            posix_spawn_file_actions_addclose(&actions, ends[i]);
        }

        fflush(stdout);
        fflush(stderr);
        spawned = posix_spawn(&child, found, &actions, NULL, argv, environ);
        posix_spawn_file_actions_destroy(&actions);
        close(in[0]);
        close(out[1]);
        close(err[1]);
    }

    for (int64_t i = 0; i <= count; i++) {
        free(argv[i]);
    }

    free(argv);
    free(found);

    if (spawned != 0) {
        if (piped) {
            close(in[1]);
            close(out[0]);
            close(err[0]);
        }

        io_last_failed = true;
        return make_string("", 0);
    }

    Buffer output = { 0 }, errors = { 0 };
    int64_t written = 0;
    int writing = in[1];
    if (input->length == 0) {
        close(writing);
        writing = -1;
    }

    int reading_out = out[0], reading_err = err[0];
    char chunk[4096];
    while (reading_out >= 0 || reading_err >= 0) {
        struct pollfd watched[3];
        int n = 0;
        if (writing >= 0) {
            watched[n++] = (struct pollfd){ writing, POLLOUT, 0 };
        }

        if (reading_out >= 0) {
            watched[n++] = (struct pollfd){ reading_out, POLLIN, 0 };
        }

        if (reading_err >= 0) {
            watched[n++] = (struct pollfd){ reading_err, POLLIN, 0 };
        }

        if (poll(watched, (nfds_t)n, -1) < 0) {
            if (errno == EINTR) {
                continue;
            }

            break;
        }

        for (int i = 0; i < n; i++) {
            if (watched[i].revents == 0) {
                continue;
            }

            int fd = watched[i].fd;
            if (fd == writing) {
                ssize_t sent = write(writing, input->bytes + written, (size_t)(input->length - written));
                if (sent > 0) {
                    written += sent;
                }

                // Done, or it stopped reading: either way, its input ends.
                if (sent <= 0 || written == input->length) {
                    close(writing);
                    writing = -1;
                }

                continue;
            }

            ssize_t got = read(fd, chunk, sizeof chunk);
            if (got > 0) {
                buffer_add(fd == reading_out ? &output : &errors, chunk, (size_t)got);
            } else if (got == 0 || errno != EINTR) {
                close(fd);
                if (fd == reading_out) {
                    reading_out = -1;
                } else {
                    reading_err = -1;
                }
            }
        }
    }

    if (writing >= 0) {
        close(writing);
    }

    int status = 0;
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {
    }

    // A signal's end as the shell reports it: 128 and the signal.
    process_last_status = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
    free(process_last_errors);
    process_last_errors = errors.bytes;
    process_last_errors_length = errors.length;
    SableString *result = make_string(output.bytes == NULL ? "" : output.bytes, (int64_t)output.length);
    free(output.bytes);
    io_last_failed = false;
    return result;
}

SableString *sable_process_current_directory(void) {
    char path[4096];
    if (getcwd(path, sizeof path) == NULL) {
        return make_string("", 0);
    }

    return make_cstring(path);
}

bool sable_process_change_directory(const SableString *path) {
    char *name = c_string(path);
    bool changed = chdir(name) == 0;
    free(name);
    return changed;
}

int64_t sable_process_id(void) {
    return (int64_t)getpid();
}

// The path this program was started by: argv[0], made absolute when it
// names a file, so that the program can start itself again (the test
// harness does, a process per test). A name without a '/' was found on
// PATH, and is given as it is, for process_run to find the same way.
SableString *sable_process_executable_path(void) {
    if (saved_argc < 1 || saved_argv[0] == NULL) {
        return make_string("", 0);
    }

    if (strchr(saved_argv[0], '/') != NULL) {
        char *resolved = realpath(saved_argv[0], NULL);
        if (resolved != NULL) {
            SableString *path = make_cstring(resolved);
            free(resolved);
            return path;
        }
    }

    return make_cstring(saved_argv[0]);
}

// ---- Joining (Sable.Text's floor) ------------------------------------

// The parts with the separator between them, in one string made at its size:
// what Sable.Text's joined and TextBuilder build on, linear in the result.
SableString *sable_string_join(const SableArray *parts, const SableString *separator) {
    int64_t n = parts == NULL ? 0 : parts->count;
    SableString *const *pieces = n == 0 ? NULL : (SableString *const *)parts->elements;
    int64_t length = 0, count = 0;
    for (int64_t i = 0; i < n; i++) {
        length += pieces[i]->length;
        count += pieces[i]->count;
    }

    if (n > 1) {
        length += (n - 1) * separator->length;
        count += (n - 1) * separator->count;
    }

    SableString *s = new_string(length, count);
    char *end = s->bytes;
    for (int64_t i = 0; i < n; i++) {
        if (i > 0) {
            memcpy(end, separator->bytes, (size_t)separator->length);
            end += separator->length;
        }

        memcpy(end, pieces[i]->bytes, (size_t)pieces[i]->length);
        end += pieces[i]->length;
    }

    return s;
}
