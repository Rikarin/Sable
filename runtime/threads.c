// The Sable runtime: threads — what every part shares now that tasks run on
// several of them at once.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Threads -----------------------------------------------------------------

// Tasks run on a pool of threads, one per core (tasks.c), and the program's
// own thread runs its entry and the main executor. Each thread has a state
// of its own (SableThread): the blocks its allocations come from first
// (memory.c), and what it has counted for SABLE_STATS and SABLE_CHECK_LEAKS,
// added up as the program ends. A thread's state is made the first time it
// asks for it and kept for as long as the program runs: the pool's threads
// never end.
//
// What the threads share is guarded by a spin lock where the work under it
// is a few instructions — a size class's slabs, the weak references' table,
// the set of live objects — and by the platform's mutex where a thread may
// wait for a while (the executor's).

// Sizes of the allocator's classes (memory.c), which each thread keeps blocks of.
enum { SMALL_CLASSES = 12 };

typedef struct BlockCache {
    void *head;    // blocks, each holding the next
    int32_t count;
} BlockCache;

typedef struct SableThread {
    BlockCache blocks[SMALL_CLASSES];
    // Counted by this thread alone; another may have made what it frees.
    int64_t live_objects, allocations;
    int64_t frames_made, frame_chunks; // async frames (frames.c)
    struct SableThread *next; // every thread's, the latest first
} SableThread;

static SableThread *all_threads;

// Where each thread finds its state. On Apple's systems a _Thread_local is
// reached through a call (_tlv_get_addr), and every allocation and every
// free asks: there it is a pthread key's slot instead, read straight from
// the thread's own table, whose address the machine keeps in a register.
// That is all pthread_getspecific does (libpthread's
// _pthread_getspecific_direct), less the call.
#if defined(__APPLE__) && (defined(__aarch64__) || defined(__x86_64__))
#include <pthread.h>

static pthread_key_t thread_key;
static bool thread_key_made;

static void make_thread_key(void) {
    if (pthread_key_create(&thread_key, NULL) != 0) {
        abort();
    }

    __atomic_store_n(&thread_key_made, true, __ATOMIC_RELEASE);
}

static inline SableThread *this_thread_get(void) {
    if (__builtin_expect(!__atomic_load_n(&thread_key_made, __ATOMIC_ACQUIRE), 0)) {
        static pthread_once_t once = PTHREAD_ONCE_INIT;
        pthread_once(&once, make_thread_key);
    }

#if defined(__aarch64__)
    uintptr_t base;
    __asm__("mrs %0, tpidrro_el0" : "=r"(base));
    return ((SableThread *const *)(base & ~(uintptr_t)7))[thread_key];
#else
    SableThread *thread;
    __asm__("movq %%gs:(,%1,8), %0" : "=r"(thread) : "r"((uintptr_t)thread_key));
    return thread;
#endif
}

static inline void this_thread_set(SableThread *thread) {
    pthread_setspecific(thread_key, thread);
}
#else
static _Thread_local SableThread *this_thread;

static inline SableThread *this_thread_get(void) {
    return this_thread;
}

static inline void this_thread_set(SableThread *thread) {
    this_thread = thread;
}
#endif

// A lock for a few instructions: taken by spinning, then by letting
// another thread run, since its holder may have been preempted.
typedef struct SpinLock {
    int32_t held;
} SpinLock;

static inline void spin_lock(SpinLock *lock) {
    for (int spins = 0; __atomic_exchange_n(&lock->held, 1, __ATOMIC_ACQUIRE); spins++) {
        while (__atomic_load_n(&lock->held, __ATOMIC_RELAXED)) {
            if (++spins > 64) {
                thread_yield();
                spins = 0;
            }
        }
    }
}

static inline void spin_unlock(SpinLock *lock) {
    __atomic_store_n(&lock->held, 0, __ATOMIC_RELEASE);
}

static SpinLock threads_lock;

_Noreturn void sable_trap(const char *message);

static SableThread *make_thread(void) {
    SableThread *thread = calloc(1, sizeof(SableThread));
    if (thread == NULL) {
        sable_trap("out of memory");
    }

    spin_lock(&threads_lock);
    thread->next = all_threads;
    all_threads = thread;
    spin_unlock(&threads_lock);
    this_thread_set(thread);
    return thread;
}

// The calling thread's state, made the first time.
static inline SableThread *current_thread(void) {
    SableThread *thread = this_thread_get();
    return thread != NULL ? thread : make_thread();
}

// A count only its thread changes, read by another as the program ends.
static inline void thread_count(int64_t *count, int64_t by) {
    __atomic_store_n(count, *count + by, __ATOMIC_RELAXED);
}

// Whether SABLE_STATS asked for counts of retains and releases: compiled
// code counts only then (LlvmEmitter's sable.retain), each with an atomic
// add, so that a program that does not ask shares no counter between cores.
bool sable_counting;
int64_t sable_retains, sable_releases;

static inline void count_retain(void) {
    if (sable_counting) {
        __atomic_fetch_add(&sable_retains, 1, __ATOMIC_RELAXED);
    }
}

static inline void count_release(void) {
    if (sable_counting) {
        __atomic_fetch_add(&sable_releases, 1, __ATOMIC_RELAXED);
    }
}
