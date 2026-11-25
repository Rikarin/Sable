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
// Built with AddressSanitizer ('sable fuzz'), every block is malloc's: ASan
// knows nothing of the blocks of a slab, and would see neither a read past
// one's end nor one after it was freed.
#if defined(__SANITIZE_ADDRESS__)
#define SABLE_ASAN 1
#elif defined(__has_feature)
#if __has_feature(address_sanitizer)
#define SABLE_ASAN 1
#endif
#endif
#ifndef SABLE_ASAN
#define SABLE_ASAN 0
#endif

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
static struct {
    _Alignas(128) Slab *with_room; // a class's on cache lines of its own
    SpinLock lock;
} classes[SMALL_CLASSES];

_Noreturn void sable_trap(const char *message);

// The class for 'size' bytes, or zero: too large, or nothing at all.
static inline unsigned small_class(int64_t size) {
    if (SABLE_ASAN || size <= 0 || size > SMALL_LARGEST) {
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
    slab->next = classes[class].with_room;
    if (slab->next != NULL) {
        slab->next->previous = slab;
    }

    classes[class].with_room = slab;
    slab->listed = true;
}

static void slab_unlist(Slab *slab, unsigned class) {
    if (slab->previous != NULL) {
        slab->previous->next = slab->next;
    } else {
        classes[class].with_room = slab->next;
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

// Blocks back to their slab, linked from the first to the last: its class's
// lock held.
static void slab_give(Slab *slab, void *first, void *last, uint32_t count, unsigned class) {
    *(void **)last = slab->free;
    slab->free = first;
    slab->live -= count;
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
    int64_t size = small_sizes[class];
    int wanted = CACHE_BATCH;
    void *head = cache->head;
    spin_lock(&classes[class].lock);
    while (wanted > 0) {
        Slab *slab = classes[class].with_room;
        if (slab == NULL) {
            slab = slab_new(class);
        }

        // What this slab has: those given back, then those never handed out.
        // Each in the order it is in, a run at a time.
        int before = wanted;
        void *free_blocks = slab->free;
        if (free_blocks != NULL) {
            void *last = free_blocks;
            for (wanted--; wanted > 0 && *(void **)last != NULL; wanted--) {
                last = *(void **)last;
            }

            void *rest = *(void **)last;
            *(void **)last = head;
            head = free_blocks;
            free_blocks = rest;
        }

        char *carved = slab->carved, *end = (char *)slab + SLAB_SIZE;
        if (wanted > 0 && end - carved >= size) {
            void *first = carved, *last;
            do {
                last = carved;
                carved += size;
                *(void **)last = carved;
                wanted--;
            } while (wanted > 0 && end - carved >= size);

            *(void **)last = head;
            head = first;
        }

        slab->free = free_blocks;
        slab->carved = carved;
        slab->live += (uint32_t)(before - wanted);
        if (free_blocks == NULL && end - carved < size) {
            slab_unlist(slab, class);
        }
    }

    spin_unlock(&classes[class].lock);
    cache->head = head;
    cache->count += CACHE_BATCH;
}

// The thread holds too many blocks of the class: a batch back to the slabs.
__attribute__((noinline)) static void cache_drain(BlockCache *cache, unsigned class, int count) {
    spin_lock(&classes[class].lock);
    // Those one after another that are one slab's go back to it at once: what
    // was freed together was, mostly, allocated together.
    void *block = cache->head;
    int given = 0;
    while (given < count && block != NULL) {
        Slab *slab = slab_of(block);
        void *first = block, *last = block;
        uint32_t run = 1;
        block = *(void **)block;
        while (given + (int)run < count && block != NULL && slab_of(block) == slab) {
            last = block;
            block = *(void **)block;
            run++;
        }

        slab_give(slab, first, last, run, class);
        given += (int)run;
    }

    cache->head = block;
    cache->count -= given;
    spin_unlock(&classes[class].lock);
}

static inline void *small_alloc(SableThread *thread, unsigned class) {
    BlockCache *cache = &thread->blocks[class];
    if (cache->head == NULL) {
        cache_fill(cache, class);
    }

    void *block = cache->head;
    cache->head = *(void **)block;
    cache->count--;
    return block;
}

static inline void small_release(SableThread *thread, void *block, unsigned class) {
    BlockCache *cache = &thread->blocks[class];
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
// Not with AddressSanitizer, which would not see it freed.
enum { LARGE_BUFFER = SABLE_ASAN ? 0x7fffffff : 1 << 20 };
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
    if (__builtin_expect(sable_counting, 0)) {
        count_allocated(class != 0 ? small_sizes[class] : size);
    }

    if (class != 0) {
        return small_alloc(current_thread(), class);
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
        small_release(current_thread(), buffer, class);
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
        if (__builtin_expect(sable_counting, 0)) {
            count_allocated(size);
        }

        // Into the spare, when it fits: its pages are already the program's.
        void *spare = size >= LARGE_BUFFER ? take_spare(size) : NULL;
        if (spare != NULL) {
            memcpy(spare, buffer, (size_t)(old < size ? old : size));
            buffer_free(buffer, old);
            return spare;
        }

        // Never of nothing, which is not to have a buffer: an array of
        // elements of no size has one, and has no need of it.
        void *grown = realloc(buffer, (size_t)(size > 0 ? size : 1));
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
extern const SableMetadata sable_string_metadata;

// An object's memory, given back: to its class, or to malloc.
static inline void object_free(SableThread *thread, SableObject *object) {
    if (object->type == &sable_string_metadata) {
        forget_string(object);
    }

    unsigned class = (object->flags & SABLE_CLASS_MASK) >> SABLE_CLASS_SHIFT;
    if (class != 0) {
        small_release(thread, object, class);
    } else {
        free(object);
    }
}

// What is asked of an allocation but the memory: leaks' list, and the bytes
// (a large object's counted by its maker, who knows its size).
__attribute__((noinline)) static void observe_born(SableObject *object, unsigned class) {
    if (tracking) {
        track(object, true);
    }

    if (sable_counting && class != 0) {
        count_allocated(small_sizes[class]);
    }
}

// A new object's header, the rest being as it is: counted, and alive.
static inline void *object_begin(SableThread *thread, SableObject *object, const SableMetadata *type, unsigned class) {
    object->type = type;
    object->strong = 1;
    object->flags = class << SABLE_CLASS_SHIFT;
    thread_count(&thread->allocations, 1);
    if (__builtin_expect(observing, 0)) {
        observe_born(object, class);
    }

    return object;
}

// From malloc: an object too large for a class.
__attribute__((noinline)) static void *large_object(const SableMetadata *type, int64_t size, bool zero) {
    SableObject *object = zero ? calloc(1, (size_t)size) : malloc((size_t)size);
    if (object == NULL) {
        sable_trap("out of memory");
    }

    if (__builtin_expect(sable_counting, 0)) {
        count_allocated(size);
    }

    return object_begin(current_thread(), object, type, 0);
}

// The thread has no block of the class, or no state yet.
__attribute__((noinline)) static void *small_object_filling(const SableMetadata *type, unsigned class) {
    SableThread *thread = current_thread();
    return object_begin(thread, small_alloc(thread, class), type, class);
}

// A new object from a size class, its count one. With none of a call's frame
// where the thread has a block of it.
static void *small_object(const SableMetadata *type, unsigned class) {
    SableThread *thread = this_thread_get();
    if (__builtin_expect(thread == NULL || thread->blocks[class].head == NULL, 0)) {
        return small_object_filling(type, class);
    }

    return object_begin(thread, small_alloc(thread, class), type, class);
}

// A new object, its count one; the rest zero when 'zero' is set, and
// otherwise whatever was there, for the caller to write. Part of its caller,
// where the size is as often as not a constant and the class then one too.
__attribute__((always_inline)) static inline void *alloc_object(const SableMetadata *type, int64_t size, bool zero) {
    unsigned class = small_class(size);
    if (class == 0) {
        return large_object(type, size, zero);
    }

    SableObject *object = small_object(type, class);
    if (zero) {
        // A block's size is a multiple of sixteen. Most objects are a few
        // words, cleared here rather than by a call.
        if (size <= 64) {
            uint64_t *words = (uint64_t *)(void *)object;
            for (int64_t i = (int64_t)sizeof(SableObject) / 8; i < (size + 7) / 8; i++) {
                words[i] = 0;
            }
        } else {
            memset(object + 1, 0, (size_t)size - sizeof(SableObject));
        }
    }

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

// A new object, its count one, for compiled code that clears the rest itself:
// its fields' initial stores, which follow, are then the only ones made.
void *sable_alloc_uninitialised(const SableMetadata *type, int64_t size) {
    return alloc_object(type, size, false);
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

// The end of a deinited object there is more to than giving its block back.
__attribute__((noinline)) static void release_rest(SableObject *object) {
    // Unowned references hold the memory, not the object, until they go;
    // the last of them, on another thread, frees it once it sees it deinited.
    uint32_t flags = __atomic_load_n(&object->flags, __ATOMIC_ACQUIRE);
    if ((flags >> SABLE_UNOWNED_SHIFT) > 0 || (flags & SABLE_HAS_SIDE)) {
        flags = __atomic_fetch_or(&object->flags, SABLE_DEINITED, __ATOMIC_ACQ_REL);
        if ((flags >> SABLE_UNOWNED_SHIFT) > 0) {
            return;
        }
    }

    SableThread *thread = current_thread();
    object_gone_on(thread, object);
    if (!(flags & SABLE_STACK)) {
        object_free(thread, object);
    }
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

    if (checking_shared && (object_flags(object) & SABLE_SHARED)) {
        check_shared(object);
    }

    // An unowned reference may lead a walk that shares (tasks.c) here as the
    // fields go: it stops at an object that is shared.
    if ((object_flags(object) >> SABLE_UNOWNED_SHIFT) > 0) {
        flags_set(object, SABLE_SHARED);
    }

    // The deinit, and the destruction of the fields, see the object alive.
    object->strong = 1;
    if (object->type->destroy != NULL) {
        object->type->destroy(object);
        if (__builtin_expect(object->strong != 1, 0)) {
            sable_trap("an object outlived its deinit");
        }
    }

    object->strong = 0;
    // Most objects' end: nothing has the object and nothing can come to, so
    // that it is deinited need not be said, which would be an atomic
    // operation each, and its block goes back to the thread's. What is
    // tested with it is what only some objects have.
    uint32_t flags = __atomic_load_n(&object->flags, __ATOMIC_ACQUIRE);
    unsigned class = (flags & SABLE_CLASS_MASK) >> SABLE_CLASS_SHIFT;
    SableThread *thread = this_thread_get();
    bool plain = flags < SABLE_UNOWNED_ONE && (flags & (SABLE_HAS_SIDE | SABLE_STACK)) == 0 && class != 0;
    if (__builtin_expect(!plain || thread == NULL || tracking, 0)) {
        release_rest(object);
        return;
    }

    if (object->type == &sable_string_metadata) {
        forget_string(object);
    }

    thread_count(&thread->gone_objects, 1);
    small_release(thread, object, class);
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

#if SABLE_ASAN
    // Nothing after the room, which ASan would take for part of it.
    void *start = NULL;
    if (posix_memalign(&start, (size_t)alignment, (size_t)(alignment + size)) != 0) {
        sable_trap("out of memory");
    }

    void *place = (char *)start + alignment;
    ((void **)place)[-1] = start;
    return place;
#endif

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
