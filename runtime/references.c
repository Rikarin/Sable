// The Sable runtime: classes, regions, and unowned and weak references.
// Part of sable_runtime.c, which includes every part in order (see there).

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
    int64_t cleanup_count, guard_count;
} SableRegionMark;

// With SABLE_CHECK_REGIONS set, a region checks how its memory is used
// (docs/02 §2.1): each value is followed by guard bytes, looked at when the
// value is given back, and what is given back — by a reset, the end of a
// 'region frame = pool' block, or the region's end — is poisoned, so that a
// raw address kept past it reads REGION_POISON rather than the old value.
#define REGION_GUARD_SIZE 16
#define REGION_GUARD 0xFD
#define REGION_POISON 0xDE

// Checking: where a value's guard bytes are, and how big the value is.
typedef struct SableRegionGuard {
    unsigned char *at;
    size_t size;
} SableRegionGuard;

typedef struct SableRegion {
    SableObject header;
    SableChunk *chunk;
    SableCleanup *cleanups;
    int64_t cleanup_count, cleanup_capacity;
    SableRegionMark *marks;
    int64_t mark_count, mark_capacity;
    bool checking;
    SableRegionGuard *guards;
    int64_t guard_count, guard_capacity;
} SableRegion;

// Traps unless every guard from 'count' on is as it was put.
static void region_check_guards(SableRegion *region, int64_t count) {
    for (int64_t i = region->guard_count - 1; i >= count; i--) {
        for (int k = 0; k < REGION_GUARD_SIZE; k++) {
            if (region->guards[i].at[k] != REGION_GUARD) {
                char message[160];
                snprintf(message, sizeof message, "a write ran past a value of %lld bytes in a region: the guard bytes after it were overwritten",
                    (long long)region->guards[i].size);
                sable_trap(message);
            }
        }
    }

    region->guard_count = count;
}

static void sable_region_destroy(void *pointer) {
    SableRegion *region = pointer;
    for (int64_t i = region->cleanup_count - 1; i >= 0; i--) {
        region->cleanups[i].destroy(region->cleanups[i].value);
    }

    if (region->checking) {
        region_check_guards(region, 0);
    }

    free(region->cleanups);
    free(region->marks);
    free(region->guards);
    for (SableChunk *chunk = region->chunk; chunk != NULL;) {
        SableChunk *next = chunk->next;
        if (region->checking) {
            memset(chunk->bytes, REGION_POISON, chunk->size);
        }

        free(chunk);
        chunk = next;
    }
}

static const SableMetadata region_metadata = { sable_region_destroy, "Region", 0, NULL, NULL, sizeof(SableRegion), _Alignof(SableRegion), NULL, NULL };

// Whether SABLE_CHECK_REGIONS is set: -1 until a region asks.
static int region_checking = -1;

void *sable_region_create(void) {
    SableRegion *region = sable_alloc(&region_metadata, (int64_t)sizeof(SableRegion));
    int checking = __atomic_load_n(&region_checking, __ATOMIC_RELAXED);
    if (checking < 0) {
        checking = getenv("SABLE_CHECK_REGIONS") != NULL;
        __atomic_store_n(&region_checking, checking, __ATOMIC_RELAXED);
    }

    region->checking = checking != 0;
    return region;
}

// Room for a value of 'size' bytes, aligned to 16; with a destroy, it is
// destroyed when the region goes. The memory is zero: empty, until stored.
void *sable_region_alloc(void *pointer, int64_t size, void (*destroy)(void *value)) {
    SableRegion *region = pointer;
    size_t rounded = ((size_t)size + (region->checking ? REGION_GUARD_SIZE : 0) + 15) & ~(size_t)15;
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
    if (region->checking) {
        // Poisoned when it was last given back: zero again, then the guard.
        unsigned char *guard = (unsigned char *)value + size;
        memset(value, 0, rounded);
        memset(guard, REGION_GUARD, REGION_GUARD_SIZE);
        if (region->guard_count == region->guard_capacity) {
            region->guard_capacity = region->guard_capacity == 0 ? 16 : region->guard_capacity * 2;
            region->guards = realloc(region->guards, (size_t)region->guard_capacity * sizeof(SableRegionGuard));
            if (region->guards == NULL) {
                sable_trap("out of memory");
            }
        }

        region->guards[region->guard_count++] = (SableRegionGuard){ guard, (size_t)size };
    }

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
// Checking, the guards put since 'guards' are looked at first, and what is
// given back is poisoned rather than zeroed.
static void region_back_to(SableRegion *region, SableChunk *chunk, size_t used, int64_t count, int64_t guards) {
    for (int64_t i = region->cleanup_count - 1; i >= count; i--) {
        region->cleanups[i].destroy(region->cleanups[i].value);
    }

    region->cleanup_count = count;
    if (region->checking) {
        region_check_guards(region, guards);
    }

    while (region->chunk != chunk) {
        SableChunk *next = region->chunk->next;
        if (region->checking) {
            memset(region->chunk->bytes, REGION_POISON, region->chunk->size);
        }

        free(region->chunk);
        region->chunk = next;
    }

    if (chunk != NULL) {
        memset(chunk->bytes + used, region->checking ? REGION_POISON : 0, chunk->used - used);
        chunk->used = used;
    }
}

// 'arena.reset()': everything in it destroyed; the newest chunk kept, empty,
// for what comes next — a frame's worth of churn in one store.
void sable_region_reset(void *pointer) {
    SableRegion *region = pointer;
    SableChunk *newest = region->chunk;
    region_back_to(region, newest, 0, 0, 0);
    if (newest != NULL) {
        for (SableChunk *older = newest->next; older != NULL;) {
            SableChunk *next = older->next;
            if (region->checking) {
                memset(older->bytes, REGION_POISON, older->size);
            }

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
        region->chunk, region->chunk == NULL ? 0 : region->chunk->used, region->cleanup_count, region->guard_count,
    };
}

void sable_region_pop(void *pointer) {
    SableRegion *region = pointer;
    if (region->mark_count == 0) {
        return;
    }

    SableRegionMark mark = region->marks[--region->mark_count];
    region_back_to(region, mark.chunk, mark.used, mark.cleanup_count, mark.guard_count);
}

// ---- Unowned references --------------------------------------------------

void sable_unowned_retain(void *pointer) {
    SableObject *object = pointer;
    if (object != NULL && !(object_flags(object) & SABLE_IMMORTAL)) {
        __atomic_fetch_add(&object->flags, SABLE_UNOWNED_ONE, __ATOMIC_RELAXED);
    }
}

void sable_unowned_release(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object_flags(object) & SABLE_IMMORTAL)) {
        return;
    }

    uint32_t flags = __atomic_sub_fetch(&object->flags, SABLE_UNOWNED_ONE, __ATOMIC_ACQ_REL);
    if ((flags >> SABLE_UNOWNED_SHIFT) == 0 && (flags & SABLE_DEINITED)) {
        SableThread *thread = current_thread();
        object_gone_on(thread, object);
        object_free(thread, object);
    }
}

// Reading an unowned reference: the object, retained — or a trap, if it is gone.
void sable_unowned_load(void *pointer) {
    SableObject *object = pointer;
    if (object_flags(object) & SABLE_DEINITED) {
        trap_here("read an unowned reference to an object that is gone");
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

// The table and every entry's count, for every thread: weak references are
// few, and each operation on one is short.
static SpinLock side_lock;

#define SIDE_TOMBSTONE ((SableWeakEntry *)1)

static size_t side_slot(const SableObject *object) {
    uint64_t key = (uintptr_t)object;
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
// A weak reference may be read on any thread — whoever holds it, an object
// or a closure another task reaches — so the object it names is shared
// from the first, while only the thread making it holds it, and counted
// atomically from then on: a load through it on another thread never races
// a plain count.
void sable_share(void *pointer);

void *sable_weak_entry(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL) {
        return NULL;
    }

    if (!(object_flags(object) & SABLE_HAS_SIDE)) {
        sable_share(object);
    }

    spin_lock(&side_lock);
    SableWeakEntry *entry;
    if (object_flags(object) & SABLE_HAS_SIDE) {
        entry = *side_find(object);
    } else {
        entry = malloc(sizeof *entry);
        if (entry == NULL) {
            sable_trap("out of memory");
        }

        entry->object = object;
        entry->count = 0;
        side_insert(entry);
        flags_set(object, SABLE_HAS_SIDE);
    }

    entry->count++;
    spin_unlock(&side_lock);
    return entry;
}

void sable_weak_retain(void *pointer) {
    SableWeakEntry *entry = pointer;
    if (entry != NULL) {
        spin_lock(&side_lock);
        entry->count++;
        spin_unlock(&side_lock);
    }
}

void sable_weak_release(void *pointer) {
    SableWeakEntry *entry = pointer;
    if (entry == NULL) {
        return;
    }

    spin_lock(&side_lock);
    if (--entry->count > 0) {
        spin_unlock(&side_lock);
        return;
    }

    if (entry->object != NULL) {
        side_remove(entry->object);
        flags_clear(entry->object, SABLE_HAS_SIDE);
    }

    spin_unlock(&side_lock);
    free(entry);
}

// Reading a weak reference: its object, retained, or null once it is gone.
// Another thread may be releasing it at the same time: a count that has
// reached zero is never raised again, and the dying object is taken out of
// its entry, under the same lock, before its deinit runs.
void *sable_weak_load(void *pointer) {
    SableWeakEntry *entry = pointer;
    if (entry == NULL) {
        return NULL;
    }

    spin_lock(&side_lock);
    SableObject *object = entry->object;
    if (object != NULL) {
        uint32_t strong = __atomic_load_n(&object->strong, __ATOMIC_RELAXED);
        do {
            if (strong == 0) {
                object = NULL;
                break;
            }
        } while (!__atomic_compare_exchange_n(&object->strong, &strong, strong + 1, true, __ATOMIC_RELAXED, __ATOMIC_RELAXED));
    }

    spin_unlock(&side_lock);
    if (object != NULL) {
        count_retain();
    }

    return object;
}

// The object is dying: its weak references now read as none.
static void sable_weak_object_died(SableObject *object) {
    spin_lock(&side_lock);
    SableWeakEntry **slot = side_find(object);
    if (slot != NULL) {
        (*slot)->object = NULL;
        *slot = SIDE_TOMBSTONE;
    }

    flags_clear(object, SABLE_HAS_SIDE);
    spin_unlock(&side_lock);
}
