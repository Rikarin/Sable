// The Sable runtime: memory — size classes, allocation, retain and release.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Memory ------------------------------------------------------------

// Small blocks — most objects, and the elements of most arrays — come from
// size classes kept here rather than from malloc. Programs that make and
// drop many small strings and arrays otherwise spend most of their time in
// malloc and free, where a .NET program bumps a pointer (docs/04 §4.1 names
// a size-class allocator).
//
// Tasks run on several threads (tasks.c), and an object made on one may be
// freed on another. Each thread keeps a few free blocks of each class of
// its own (threads.c's SableThread), and allocates from them and frees to
// them without a lock; only as they run out, or grow past a limit, does it
// take its class's lock and move a batch to or from the slabs.
//
// Each class has slabs of its own, taken from the system aligned to their
// size, so a block's slab is its address with the low bits cleared. A slab
// carves blocks in order, keeps those given back on a list of its own, and
// counts the blocks it has out: when that reaches zero the slab is given
// back, unless it is the only one its class has room in — a program that
// makes and drops one object at a time would otherwise map and unmap a slab
// each time. A slab given back is kept as a spare for any class, up to
// SPARE_SLABS of them (8 MiB), and past that goes back to the system: a
// program that once held a million small objects and dropped them holds
// their memory no longer.
//
// An object records its class in its flags, and an array's elements are
// freed with their size, which the array knows.

// Classes up to a kilobyte: an array of a hundred numbers is as common as a
// small object. Each step is at most half again the one before, so no more
// than a third of a block is ever wasted.
enum { SMALL_TABLED = 256, SMALL_LARGEST = 1024 };

// How many free blocks of a class a thread keeps before giving a batch back.
enum { CACHE_BATCH = 32, CACHE_LIMIT = 2 * CACHE_BATCH };

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

// Each class's slabs with room, the one handing out blocks first, and the
// lock its slabs are taken and given back under.
static Slab *slabs_with_room[SMALL_CLASSES];
static SpinLock class_locks[SMALL_CLASSES];

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

// Empty slabs kept for the next burst, of any class, each holding the next:
// a program that makes and drops many objects a round at a time would
// otherwise have the system map, and fault in, its slabs afresh each round.
// Past SPARE_SLABS an empty slab goes back to the system.
enum { SPARE_SLABS = 128 };
static Slab *spare_slabs;
static int64_t spare_count;
static SpinLock spare_lock;

static Slab *slab_new(unsigned class) {
    spin_lock(&spare_lock);
    Slab *slab = spare_slabs;
    if (slab != NULL) {
        spare_slabs = slab->next;
        spare_count--;
    }

    spin_unlock(&spare_lock);
    if (slab == NULL) {
        slab = map_slab(SLAB_SIZE);
        if (slab == NULL) {
            sable_trap("out of memory");
        }

        // Counted across the classes, each under its own lock.
        int64_t held = __atomic_add_fetch(&slabs_held, 1, __ATOMIC_RELAXED);
        int64_t most = __atomic_load_n(&slabs_most, __ATOMIC_RELAXED);
        while (held > most && !__atomic_compare_exchange_n(&slabs_most, &most, held, true, __ATOMIC_RELAXED, __ATOMIC_RELAXED)) {
        }
    }

    slab->free = NULL;
    slab->carved = (char *)slab + SLAB_HEADER;
    slab->live = 0;
    slab_list(slab, class);
    return slab;
}

// An empty slab: kept as a spare, or back to the system when there are
// spares enough.
static void slab_retire(Slab *slab) {
    spin_lock(&spare_lock);
    bool kept = spare_count < SPARE_SLABS;
    if (kept) {
        slab->next = spare_slabs;
        spare_slabs = slab;
        spare_count++;
    }

    spin_unlock(&spare_lock);
    if (!kept) {
        unmap_slab(slab, SLAB_SIZE);
        __atomic_fetch_sub(&slabs_held, 1, __ATOMIC_RELAXED);
        __atomic_fetch_add(&slabs_returned, 1, __ATOMIC_RELAXED);
    }
}

// A block from the class's slabs; its lock held.
static void *slab_take(unsigned class) {
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

// A block back to its slab; its class's lock held.
static void slab_give(void *block, unsigned class) {
    Slab *slab = slab_of(block);
    *(void **)block = slab->free;
    slab->free = block;
    slab->live--;
    if (!slab->listed) {
        slab_list(slab, class);
    } else if (slab->live == 0 && (slab->next != NULL || slab->previous != NULL)) {
        // Empty, and its class has room elsewhere: back to the system.
        slab_unlist(slab, class);
        slab_retire(slab);
    }
}

// The thread's blocks of the class ran out: a batch from the slabs.
__attribute__((noinline)) static void cache_fill(BlockCache *cache, unsigned class) {
    spin_lock(&class_locks[class]);
    for (int i = 0; i < CACHE_BATCH; i++) {
        void *block = slab_take(class);
        *(void **)block = cache->head;
        cache->head = block;
    }

    spin_unlock(&class_locks[class]);
    cache->count += CACHE_BATCH;
}

// The thread holds too many blocks of the class: a batch back to the slabs.
__attribute__((noinline)) static void cache_drain(BlockCache *cache, unsigned class, int count) {
    spin_lock(&class_locks[class]);
    for (int i = 0; i < count && cache->head != NULL; i++) {
        void *block = cache->head;
        cache->head = *(void **)block;
        cache->count--;
        slab_give(block, class);
    }

    spin_unlock(&class_locks[class]);
}

static inline void *small_alloc(unsigned class) {
    BlockCache *cache = &current_thread()->blocks[class];
    if (cache->head == NULL) {
        cache_fill(cache, class);
    }

    void *block = cache->head;
    cache->head = *(void **)block;
    cache->count--;
    return block;
}

static inline void small_release(void *block, unsigned class) {
    BlockCache *cache = &current_thread()->blocks[class];
    *(void **)block = cache->head;
    cache->head = block;
    if (++cache->count > CACHE_LIMIT) {
        cache_drain(cache, class, CACHE_BATCH);
    }
}

// Every block the thread keeps, back to the slabs: as a thread finishes
// with a burst of work, so that what it freed can go back to the system.
static void cache_flush(SableThread *thread) {
    for (unsigned class = 1; class < SMALL_CLASSES; class++) {
        if (thread->blocks[class].count > 0) {
            cache_drain(&thread->blocks[class], class, thread->blocks[class].count);
        }
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
static SpinLock spare_lock;

// The spare, if it holds 'size' bytes without wasting more than as much again.
static void *take_spare(int64_t size) {
    spin_lock(&spare_lock);
    void *buffer = NULL;
    if (large_spare != NULL && large_spare_size >= size && large_spare_size <= 2 * size) {
        buffer = large_spare;
        large_spare = NULL;
    }

    spin_unlock(&spare_lock);
    return buffer;
}

// A large buffer let go: kept as the spare if it is the larger, freed otherwise.
static void keep_spare(void *buffer, int64_t size) {
    spin_lock(&spare_lock);
    void *freed = buffer;
    if (large_spare == NULL || large_spare_size < size) {
        freed = large_spare;
        large_spare = buffer;
        large_spare_size = size;
    }

    spin_unlock(&spare_lock);
    free(freed);
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
    thread_count(&current_thread()->allocations, 1);
    return object;
}

// How many objects this thread has allocated since it started: what a test
// that something allocates nothing compares before and after.
int64_t sable_memory_allocations(void) {
    return current_thread()->allocations;
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
    if (object == NULL) {
        return;
    }

    uint32_t flags = object_flags(object);
    if (flags & SABLE_IMMORTAL) {
        return;
    }

    if (flags & SABLE_SHARED) {
        __atomic_fetch_add(&object->strong, 1, __ATOMIC_RELAXED);
    } else {
        object->strong++;
    }

    count_retain();
}

void sable_release_last(void *pointer);

// With a profile's arc-checks, compiled code's retain and release call this
// for an object whose count is already zero: its deinit has run, and the
// reference counted outlived it. Counting it would write what may be freed
// memory, so the program stops instead. operation is 0 for a retain, 1 for
// a release.
_Noreturn void sable_arc_dead(void *pointer, int32_t operation) {
    (void)pointer;
    sable_trap(operation == 0
        ? "retain of an object whose deinit has run"
        : "release of an object whose deinit has run");
}

void sable_release(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL) {
        return;
    }

    uint32_t flags = object_flags(object);
    if (flags & SABLE_IMMORTAL) {
        return;
    }

    count_release();
    if (flags & SABLE_SHARED) {
        // The last release sees every write the others made before theirs:
        // acquire and release in the one operation, rather than a fence
        // after, which ThreadSanitizer does not see.
        if (__atomic_fetch_sub(&object->strong, 1, __ATOMIC_ACQ_REL) != 1) {
            return;
        }
    } else if (--object->strong > 0) {
        return;
    }

    sable_release_last(object);
}

// The only reference to an object made in a frame (docs/04 §3.7), which the
// compiler found nothing ever copied, held weakly or unowned, or kept: its
// count is one and stays so, and it is destroyed where it is without being
// counted down — compiled code's 'destroy_value [uncounted]'. One that is on
// the heap after all, a block that outgrew its room in the frame, is
// released as any is.
void sable_destroy_uncounted(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL) {
        return;
    }

    if (!(object_flags(object) & SABLE_STACK)) {
        sable_release(object);
        return;
    }

    if (object->strong != 1) {
        sable_trap("an uncounted object was shared");
    }

    if (object->type->destroy != NULL) {
        object->type->destroy(object);
    }

    if (object->strong != 1) {
        sable_trap("an object outlived its deinit");
    }

    object->strong = 0;
    object_gone(object);
}

// An object whose initialiser threw, before its allocator lets go of it:
// never made, so when it goes its class's destroy function runs no deinit
// and only destroys the fields — those set before the throw; the rest are
// still empty.
void sable_object_unmade(void *pointer) {
    flags_set(pointer, SABLE_UNMADE);
}

// The last strong reference gone: its count is zero, and it is counted.
// The deinit runs, the fields are destroyed, and the memory goes when no
// unowned reference holds it. No other thread can reach it now but through
// a weak reference, which reads as none from here on.
void sable_release_last(void *pointer) {
    SableObject *object = pointer;
    if (object_flags(object) & SABLE_HAS_SIDE) {
        sable_weak_object_died(object);
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
    // Unowned references hold the memory, not the object, until they go;
    // the last of them, on another thread, frees it once it sees it deinited.
    // With none now, and no weak ones, nothing has the object and nothing can
    // come to: that it is deinited need not be said, which is most objects'
    // end and would be an atomic operation each.
    uint32_t flags = __atomic_load_n(&object->flags, __ATOMIC_ACQUIRE);
    if ((flags >> SABLE_UNOWNED_SHIFT) > 0 || (flags & SABLE_HAS_SIDE)) {
        flags = __atomic_fetch_or(&object->flags, SABLE_DEINITED, __ATOMIC_ACQ_REL);
        if ((flags >> SABLE_UNOWNED_SHIFT) > 0) {
            return;
        }
    }

    object_gone(object);
    if (!(flags & SABLE_STACK)) {
        object_free(object);
    }
}

// ---- Raw memory ----------------------------------------------------------

// RawPtr's and NativeMemory's (lib/Sable.Core/RawPtr.sb): room of any size
// at any power-of-two alignment, from malloc, with the block malloc gave
// recorded just before the room so that it can be given back. Not an object:
// counted by no one, and freed only when the program says so.

void *sable_raw_alloc(int64_t size, int64_t alignment) {
    if (size < 0) {
        sable_trap("cannot allocate a negative number of bytes");
    }
    if (alignment <= 0 || (alignment & (alignment - 1)) != 0) {
        sable_trap("an allocation's alignment must be a power of two");
    }
    if (alignment < (int64_t)sizeof(void *)) {
        alignment = (int64_t)sizeof(void *);
    }
    if (size > INT64_MAX - alignment - (int64_t)sizeof(void *)) {
        sable_trap("out of memory");
    }

    char *block = malloc((size_t)(size + alignment + (int64_t)sizeof(void *)));
    if (block == NULL) {
        sable_trap("out of memory");
    }

    uintptr_t room = ((uintptr_t)block + sizeof(void *) + (uintptr_t)alignment - 1) & ~((uintptr_t)alignment - 1);
    ((void **)room)[-1] = block;
    return (void *)room;
}

void sable_raw_free(void *room) {
    if (room != NULL) {
        free(((void **)room)[-1]);
    }
}
