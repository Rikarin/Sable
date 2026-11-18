// The Sable runtime: memory — size classes, allocation, retain and release.
// Part of sable_runtime.c, which includes every part in order (see there).

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

// Classes up to a kilobyte: an array of a hundred numbers is as common as a
// small object. Each step is at most half again the one before, so no more
// than a third of a block is ever wasted.
enum { SMALL_CLASSES = 12, SMALL_TABLED = 256, SMALL_LARGEST = 1024, SLAB_SIZE = 256 * 1024 };

static const int64_t small_sizes[SMALL_CLASSES] = { 0, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024 };

// The class for a size up to 256, by sixteenths: the smallest that holds it.
static const uint8_t small_class_of[SMALL_TABLED / 16 + 1] = { 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7 };

static void *small_free[SMALL_CLASSES];
static char *slab_next, *slab_end;

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

    object_gone(object);
    if (!(object->flags & SABLE_STACK)) {
        object_free(object);
    }
}
