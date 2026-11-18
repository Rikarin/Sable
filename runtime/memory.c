// The Sable runtime: memory — size classes, allocation, retain and release.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Memory ------------------------------------------------------------

// Small blocks — most objects, and the elements of most arrays — come from
// size classes kept here rather than from malloc. Programs that make and
// drop many small strings and arrays otherwise spend most of their time in
// malloc and free, where a .NET program bumps a pointer (docs/04 §4.1 names
// a size-class allocator). One task runs at a time (see Concurrency), so
// nothing here needs a lock.
//
// Each class has slabs of its own, taken from the system aligned to their
// size, so a block's slab is its address with the low bits cleared. A slab
// carves blocks in order, keeps those given back on a list of its own, and
// counts the blocks it has out: when that reaches zero the slab is given
// back to the system, unless it is the only one its class has room in — a
// program that makes and drops one object at a time would otherwise map
// and unmap a slab each time. A program that once held a million small
// objects and dropped them holds their memory no longer.
//
// An object records its class in its flags, and an array's elements are
// freed with their size, which the array knows.

// Classes up to a kilobyte: an array of a hundred numbers is as common as a
// small object. Each step is at most half again the one before, so no more
// than a third of a block is ever wasted.
enum { SMALL_CLASSES = 12, SMALL_TABLED = 256, SMALL_LARGEST = 1024 };

static const int64_t small_sizes[SMALL_CLASSES] = { 0, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024 };

// The class for a size up to 256, by sixteenths: the smallest that holds it.
static const uint8_t small_class_of[SMALL_TABLED / 16 + 1] = { 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7 };

// 64 KiB: sixty-three of the largest blocks, and Windows' allocation
// granularity, so a slab is aligned as the system gives it there. The
// header takes the first 64 bytes, which keeps the blocks 16-aligned.
enum { SLAB_SIZE = 64 * 1024, SLAB_HEADER = 64 };

typedef struct Slab {
    // Among its class's slabs with room, while it has some.
    struct Slab *next, *previous;
    // Blocks given back, to hand out again before carving more.
    void *free;
    // The first block never handed out.
    char *carved;
    // Blocks handed out and not given back.
    uint32_t live;
    bool listed;
} Slab;

_Static_assert(sizeof(Slab) <= SLAB_HEADER, "a slab's header fits before its first block");

// Each class's slabs with room, the one handing out blocks first.
static Slab *slabs_with_room[SMALL_CLASSES];

_Noreturn void sable_trap(const char *message);

// The class for 'size' bytes, or zero: too large, or nothing at all.
static inline unsigned small_class(int64_t size) {
    if (size <= 0 || size > SMALL_LARGEST) {
        return 0;
    }

    if (size <= SMALL_TABLED) {
        return small_class_of[(size + 15) >> 4];
    }

    return size <= 384 ? 8 : size <= 512 ? 9 : size <= 768 ? 10 : 11;
}

static inline Slab *slab_of(void *block) {
    return (Slab *)((uintptr_t)block & ~(uintptr_t)(SLAB_SIZE - 1));
}

static void slab_list(Slab *slab, unsigned class) {
    slab->previous = NULL;
    slab->next = slabs_with_room[class];
    if (slab->next != NULL) {
        slab->next->previous = slab;
    }

    slabs_with_room[class] = slab;
    slab->listed = true;
}

static void slab_unlist(Slab *slab, unsigned class) {
    if (slab->previous != NULL) {
        slab->previous->next = slab->next;
    } else {
        slabs_with_room[class] = slab->next;
    }

    if (slab->next != NULL) {
        slab->next->previous = slab->previous;
    }

    slab->listed = false;
}

static Slab *slab_new(unsigned class) {
    Slab *slab = map_slab(SLAB_SIZE);
    if (slab == NULL) {
        sable_trap("out of memory");
    }

    slab->free = NULL;
    slab->carved = (char *)slab + SLAB_HEADER;
    slab->live = 0;
    slab_list(slab, class);
    if (++slabs_held > slabs_most) {
        slabs_most = slabs_held;
    }

    return slab;
}

static void *small_alloc(unsigned class) {
    Slab *slab = slabs_with_room[class];
    if (slab == NULL) {
        slab = slab_new(class);
    }

    int64_t size = small_sizes[class];
    void *block = slab->free;
    if (block != NULL) {
        slab->free = *(void **)block;
    } else {
        block = slab->carved;
        slab->carved += size;
    }

    slab->live++;
    if (slab->free == NULL && (char *)slab + SLAB_SIZE - slab->carved < size) {
        slab_unlist(slab, class);
    }

    return block;
}

static inline void small_release(void *block, unsigned class) {
    Slab *slab = slab_of(block);
    *(void **)block = slab->free;
    slab->free = block;
    slab->live--;
    if (!slab->listed) {
        slab_list(slab, class);
    } else if (slab->live == 0 && (slab->next != NULL || slab->previous != NULL)) {
        // Empty, and its class has room elsewhere: back to the system.
        slab_unlist(slab, class);
        unmap_slab(slab, SLAB_SIZE);
        slabs_held--;
        slabs_returned++;
    }
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

static void forget_string(const SableObject *object);

// An object's memory, given back: to its class, or to malloc.
static void object_free(SableObject *object) {
    forget_string(object);
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
    object_born(object);
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
    object_born(object);
    return object;
}

// Compiled code does what these two do inline (LlvmEmitter's sable.retain
// and sable.release), so that a build without link-time optimisation keeps
// its retains and releases out of calls; the runtime's own code calls them.
void sable_retain(void *pointer) {
    SableObject *object = pointer;
    if (object != NULL && !(object->flags & SABLE_IMMORTAL)) {
        object->strong++;
        sable_retains++;
    }
}

void sable_release_last(void *pointer);

void sable_release(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object->flags & SABLE_IMMORTAL)) {
        return;
    }

    sable_releases++;
    if (--object->strong > 0) {
        return;
    }

    sable_release_last(object);
}

// The last strong reference gone: its count is zero, and it is counted.
// The deinit runs, the fields are destroyed, and the memory goes when no
// unowned reference holds it.
void sable_release_last(void *pointer) {
    SableObject *object = pointer;

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

    object_gone(object);
    if (!(object->flags & SABLE_STACK)) {
        object_free(object);
    }
}
