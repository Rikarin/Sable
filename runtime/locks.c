// The Sable runtime: locks — the system's mutex and condition variable, as
// handles, for Sable.Core's Mutex, RwLock, Condition, Semaphore, Barrier and
// CountdownLatch (lib/Sable.Core/Locks.sb). Part of sable_runtime.c, which
// includes every part in order (see there).
//
// A thread that waits on one blocks: it is the system's, not a task's
// suspension, as .NET's Monitor and Rust's Mutex block. A task that must not
// hold a worker while it waits uses an actor (AsyncMutex, AsyncSemaphore).

void *sable_lock_new(void) {
    PlatformMutex *mutex = malloc(sizeof *mutex);
    if (mutex == NULL) {
        sable_trap("out of memory");
    }
    mutex_init(mutex);
    return mutex;
}

void sable_lock_free(void *mutex) {
    free(mutex);
}

void sable_lock_acquire(void *mutex) {
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
