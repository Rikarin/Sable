// The Sable runtime: locks — the system's mutex and condition variable, as
// handles, for Sable.Concurrency's Mutex, Condition, Semaphore, Barrier and
// CountdownLatch and the collections built on them; and RwLock's lock. Part
// of sable_runtime.c, which includes every part in order (see there).
//
// A thread that waits on one blocks: it is the system's, not a task's
// suspension, as .NET's Monitor and Rust's Mutex block. A task that must not
// hold a worker while it waits uses an actor (AsyncMutex, AsyncSemaphore).

// Each lock on cache lines of its own: locks made one after another (a
// sharded collection's) would otherwise share a line, and threads taking
// different locks would still pass it between their cores. Before the mutex,
// on the same line, is room for what it guards, the maker's to use
// (Runtime.lockRoom): whoever takes the lock has that line already.
enum { LOCK_ALIGNMENT = 128, LOCK_ROOM = 24 };

static void *lock_memory(size_t size) {
    return sable_raw_alloc(((int64_t)size + LOCK_ALIGNMENT - 1) / LOCK_ALIGNMENT * LOCK_ALIGNMENT, LOCK_ALIGNMENT);
}

void *sable_lock_new(void) {
    char *room = lock_memory(LOCK_ROOM + sizeof(PlatformMutex));
    mutex_init((PlatformMutex *)(room + LOCK_ROOM));
    return room + LOCK_ROOM;
}

void sable_lock_free(void *mutex) {
    sable_raw_free((char *)mutex - LOCK_ROOM);
}

// A lock held by another thread is usually let go within a few hundred
// nanoseconds — a collection's shard, a queue's end — so a thread tries
// again a few times before it asks the system to put it to sleep, which
// costs the sleeper and the thread that wakes it each a call into the kernel.
//
// The pause between is short on ARM ('yield'): a thread that waits out the
// holder awake there only passes the lock to and fro, where asleep it leaves
// the holder to take it again and again. On x86, where a sleep and its wake
// cost more, the pause is twice as long each time instead, up to some
// microseconds: long enough for the holder to take the lock many times over
// with no one else at its cache line. And the lock is looked at before it is
// tried (mutex_looks_free), as a try that fails takes the line from the
// holder too.
#if defined(__aarch64__)
enum { LOCK_SPINS = 40, LOCK_FIRST_PAUSES = 8, LOCK_MOST_PAUSES = 8 };
#else
enum { LOCK_SPINS = 12, LOCK_FIRST_PAUSES = 1, LOCK_MOST_PAUSES = 128 };
#endif

static inline void lock_pause(void) {
#if defined(__aarch64__)
    __asm__ __volatile__("yield");
#else
    spin_pause();
#endif
}

static __attribute__((noinline)) void lock_acquire_held(void *mutex) {
    count_executor(&locks_contended);
    int pauses = LOCK_FIRST_PAUSES;
    for (int spin = 0; spin < LOCK_SPINS; spin++) {
        for (int pause = 0; pause < pauses; pause++) {
            lock_pause();
        }

        pauses = pauses * 2 < LOCK_MOST_PAUSES ? pauses * 2 : LOCK_MOST_PAUSES;
        if (mutex_looks_free(mutex) && mutex_try_lock(mutex)) {
            return;
        }
    }

    count_executor(&locks_slept);
    mutex_lock(mutex);
}

void sable_lock_acquire(void *mutex) {
    if (!mutex_try_lock(mutex)) {
        lock_acquire_held(mutex);
    }
}

// For a thread that takes the lock to have many things at once of those the
// threads it meets there add one at a time: if it is held, one of them is at
// it now, and a wait of a microsecond or so, away from its cache line, finds
// many more. Back at once, the thread would find a few and soon be back
// again, and each of the two would wait for the line at each.
void sable_lock_acquire_later(void *mutex) {
    if (mutex_try_lock(mutex)) {
        return;
    }

    spin_turn();
    spin_turn();
    sable_lock_acquire(mutex);
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

// ---- Reader/writer locks ---------------------------------------------------------

// Readers count themselves in and out, and then look whether a writer is
// there; a writer says it is there, and then waits for the readers to be
// gone. Each does its first before its second in the one order of the
// sequentially consistent operations, so of a reader and a writer that come
// at once, at least one sees the other: the reader then takes itself out
// again and waits for the writers to be gone. So a writer waiting goes
// before readers that come after it.
//
// 'writers' counts those waiting and the one writing; 'exclusive', a lock,
// lets one in at a time. Readers count themselves in 'readers' until
// several are found there at once often: then in 'slots', one for each core
// on cache lines of its own, a thread always in the same — else every
// reader on every core would pass one line around, at a hundred times the
// cost of counting. The slots stay for as long as the lock does; a reader
// counts itself out where it counted itself in, which 'sable_rwlock_read'
// returns. A reader that came by a slot before a writer said it was there
// is seen by it: the writer reads 'slots' after, in that one order.
//
// Whoever finds it must wait looks again a few times, then sleeps: the
// readers until no writer is left, the writer until no reader is. Each says
// it sleeps ('readers_asleep', 'writer_asleep') and then looks once more,
// under 'inner'; whoever changes what is waited for does so and then looks
// who sleeps: again one sees the other.
enum { RW_CROWDED = 64, RW_MOST_SLOTS = 64 };

typedef struct RwSlot {
    int64_t readers;
    char padding[LOCK_ALIGNMENT - sizeof(int64_t)];
} RwSlot;

typedef struct SableRwLock {
    int64_t readers;
    RwSlot *slots;
    int32_t writers;
    int32_t crowded;        // how often a reader found another
    int32_t writer_asleep;  // both under 'inner', and read without it
    int32_t readers_asleep;
    PlatformMutex inner;
    PlatformCondition readers_go, writer_goes;
    PlatformMutex exclusive;
} SableRwLock;

// How many slots: one a core, and a power of two.
static int rw_slot_count(void) {
    static int count;
    int known = __atomic_load_n(&count, __ATOMIC_RELAXED);
    if (known == 0) {
        int cores = processor_count();
        for (known = 1; known < cores && known < RW_MOST_SLOTS; known *= 2) {
        }

        __atomic_store_n(&count, known, __ATOMIC_RELAXED);
    }

    return known;
}

void *sable_rwlock_new(void) {
    SableRwLock *lock = lock_memory(sizeof(SableRwLock));
    memset(lock, 0, sizeof *lock);
    mutex_init(&lock->inner);
    condition_init(&lock->readers_go);
    condition_init(&lock->writer_goes);
    mutex_init(&lock->exclusive);
    return lock;
}

void sable_rwlock_free(void *pointer) {
    SableRwLock *lock = pointer;
    if (lock->slots != NULL) {
        sable_raw_free(lock->slots);
    }

    sable_raw_free(lock);
}

// The wait before a thread looks again at what it waits for: twice as long
// each time, up to a point, so that threads that wait leave the one that
// does not, and the cache line it works on, alone. Whether to look again:
// a wait longer than a few microseconds is the system's.
enum { SPIN_ROUNDS = 8, SPIN_LONGEST = 4 };

static inline bool spin_backoff(int *round) {
    if (*round == SPIN_ROUNDS) {
        return false;
    }

    int pauses = 1 << (*round < SPIN_LONGEST ? *round : SPIN_LONGEST);
    (*round)++;
    for (int i = 0; i < pauses; i++) {
        spin_pause();
    }

    return true;
}

static bool rw_has_readers(SableRwLock *lock) {
    if (__atomic_load_n(&lock->readers, __ATOMIC_SEQ_CST) != 0) {
        return true;
    }

    RwSlot *slots = __atomic_load_n(&lock->slots, __ATOMIC_SEQ_CST);
    for (int i = slots != NULL ? rw_slot_count() : 0; --i >= 0;) {
        if (__atomic_load_n(&slots[i].readers, __ATOMIC_SEQ_CST) != 0) {
            return true;
        }
    }

    return false;
}

// A reader counted out: a writer may be waiting for it.
static inline void rw_reader_gone(SableRwLock *lock, int64_t *counted) {
    __atomic_fetch_sub(counted, 1, __ATOMIC_SEQ_CST);
    if (__builtin_expect(__atomic_load_n(&lock->writer_asleep, __ATOMIC_SEQ_CST), 0)) {
        mutex_lock(&lock->inner);
        condition_signal(&lock->writer_goes);
        mutex_unlock(&lock->inner);
    }
}

// Slots for a lock whose readers meet.
static __attribute__((noinline)) void rw_make_slots(SableRwLock *lock) {
    size_t size = (size_t)rw_slot_count() * sizeof(RwSlot);
    RwSlot *slots = lock_memory(size);
    memset(slots, 0, size);
    __atomic_store_n(&lock->slots, slots, __ATOMIC_SEQ_CST);
}

// Waits until no writer is there.
static __attribute__((noinline)) void rw_await_writers(SableRwLock *lock) {
    for (int round = 0; spin_backoff(&round);) {
        if (__atomic_load_n(&lock->writers, __ATOMIC_RELAXED) == 0) {
            return;
        }
    }

    mutex_lock(&lock->inner);
    __atomic_fetch_add(&lock->readers_asleep, 1, __ATOMIC_SEQ_CST);
    while (__atomic_load_n(&lock->writers, __ATOMIC_SEQ_CST) != 0) {
        condition_wait(&lock->readers_go, &lock->inner, -1);
    }

    __atomic_fetch_sub(&lock->readers_asleep, 1, __ATOMIC_RELAXED);
    mutex_unlock(&lock->inner);
}

// In to read: where it counted itself, for 'sable_rwlock_read_done'.
void *sable_rwlock_read(void *pointer) {
    SableRwLock *lock = pointer;
    for (;;) {
        RwSlot *slots = __atomic_load_n(&lock->slots, __ATOMIC_SEQ_CST);
        int64_t *counted = slots != NULL ? &slots[current_thread()->index & (rw_slot_count() - 1)].readers : &lock->readers;
        int64_t others = __atomic_fetch_add(counted, 1, __ATOMIC_SEQ_CST);
        if (__builtin_expect(__atomic_load_n(&lock->writers, __ATOMIC_SEQ_CST) == 0, 1)) {
            // One reader alone counts to RW_CROWDED: it makes the slots.
            if (slots == NULL && others != 0 && PLATFORM_THREADS && __atomic_add_fetch(&lock->crowded, 1, __ATOMIC_RELAXED) == RW_CROWDED) {
                rw_make_slots(lock);
            }

            return counted;
        }

        rw_reader_gone(lock, counted);
        rw_await_writers(lock);
    }
}

void sable_rwlock_read_done(void *lock, void *counted) {
    rw_reader_gone(lock, counted);
}

// In to write, alone.
void sable_rwlock_write(void *pointer) {
    SableRwLock *lock = pointer;
    __atomic_fetch_add(&lock->writers, 1, __ATOMIC_SEQ_CST);
    sable_lock_acquire(&lock->exclusive);
    for (int round = 0; rw_has_readers(lock);) {
        if (spin_backoff(&round)) {
            continue;
        }

        mutex_lock(&lock->inner);
        __atomic_store_n(&lock->writer_asleep, 1, __ATOMIC_SEQ_CST);
        while (rw_has_readers(lock)) {
            condition_wait(&lock->writer_goes, &lock->inner, -1);
        }

        __atomic_store_n(&lock->writer_asleep, 0, __ATOMIC_RELAXED);
        mutex_unlock(&lock->inner);
        break;
    }
}

void sable_rwlock_write_done(void *pointer) {
    SableRwLock *lock = pointer;
    mutex_unlock(&lock->exclusive);
    if (__atomic_sub_fetch(&lock->writers, 1, __ATOMIC_SEQ_CST) == 0 && __atomic_load_n(&lock->readers_asleep, __ATOMIC_SEQ_CST) > 0) {
        mutex_lock(&lock->inner);
        condition_broadcast(&lock->readers_go);
        mutex_unlock(&lock->inner);
    }
}
