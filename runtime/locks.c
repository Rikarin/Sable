// The Sable runtime: locks — the system's mutex and condition variable, as
// handles, for Sable.Core's Mutex, RwLock, Condition, Semaphore, Barrier and
// CountdownLatch (lib/Sable.Core/Locks.sb). Part of sable_runtime.c, which
// includes every part in order (see there).
//
// A thread that waits on one blocks: it is the system's, not a task's
// suspension, as .NET's Monitor and Rust's Mutex block. A task that must not
// hold a worker while it waits uses an actor (AsyncMutex, AsyncSemaphore).

// Each lock on cache lines of its own: locks made one after another (a
// sharded collection's) would otherwise share a line, and threads taking
// different locks would still pass it between their cores.
enum { LOCK_ALIGNMENT = 128 };

void *sable_lock_new(void) {
    int64_t size = ((int64_t)sizeof(PlatformMutex) + LOCK_ALIGNMENT - 1) / LOCK_ALIGNMENT * LOCK_ALIGNMENT;
    PlatformMutex *mutex = sable_raw_alloc(size, LOCK_ALIGNMENT);
    mutex_init(mutex);
    return mutex;
}

void sable_lock_free(void *mutex) {
    sable_raw_free(mutex);
}

// A pause in a spin: the processor's hint that this is a wait loop.
static inline void spin_pause(void) {
#if defined(__aarch64__)
    __asm__ __volatile__("yield");
#elif defined(__x86_64__) || defined(__i386__)
    __builtin_ia32_pause();
#endif
}

// A lock held by another thread is usually let go within a few hundred
// nanoseconds — a collection's shard, a queue's end — so a thread tries
// again a few times before it asks the system to put it to sleep, which
// costs the sleeper and the thread that wakes it each a call into the kernel.
enum { LOCK_SPINS = 40 };

void sable_lock_acquire(void *mutex) {
    if (mutex_try_lock(mutex)) {
        return;
    }

    for (int spin = 0; spin < LOCK_SPINS; spin++) {
        for (int pause = 0; pause < 8; pause++) {
            spin_pause();
        }

        if (mutex_try_lock(mutex)) {
            return;
        }
    }

    mutex_lock(mutex);
}

bool sable_lock_try_acquire(void *mutex) {
    return mutex_try_lock(mutex);
}

void sable_lock_release(void *mutex) {
    mutex_unlock(mutex);
}

void *sable_condition_new(void) {
    PlatformCondition *condition = malloc(sizeof *condition);
    if (condition == NULL) {
        sable_trap("out of memory");
    }
    condition_init(condition);
    return condition;
}

void sable_condition_free(void *condition) {
    free(condition);
}

// Waits, the mutex held, until signalled — or, 'until' not negative, until
// that time of the monotonic clock at the latest. May wake for no reason:
// the caller looks again at what it waits for.
void sable_condition_wait(void *condition, void *mutex, int64_t until) {
    condition_wait(condition, mutex, until);
}

void sable_condition_signal(void *condition) {
    condition_signal(condition);
}

void sable_condition_broadcast(void *condition) {
    condition_broadcast(condition);
}
