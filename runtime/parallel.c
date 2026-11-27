// The Sable runtime: threads a program starts of its own, the values each
// thread has one of, and the loops the pool's threads share (Sable.Concurrency's
// Thread, ThreadLocal and parallel loops).
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Thread-locals -------------------------------------------------------------

// A ThreadLocal is a place in each thread's table, and a thread's value there
// is in raw memory of its own, with what destroys it: made as the thread first
// asks, destroyed as it ends, the latest made first. The place is another
// ThreadLocal's once the first is gone; the key says which of them an entry is
// for, its upper half being how many had the place before.
typedef struct ThreadLocalValue {
    int64_t key;                   // 0: none
    int64_t cell;
    void (*destroy)(void *holder);
    void *holder;
    int32_t previous, next;        // made before it and after it, each a place and one
} ThreadLocalValue;

static SpinLock local_lock;
static int32_t *local_free, *local_generations; // places free; how many had each
static int32_t local_free_count, local_places;

int64_t sable_thread_local_key(void) {
    spin_lock(&local_lock);
    if (local_free_count == 0) {
        int32_t places = local_places == 0 ? 16 : local_places * 2;
        local_free = realloc(local_free, (size_t)places * sizeof *local_free);
        local_generations = realloc(local_generations, (size_t)places * sizeof *local_generations);
        if (local_free == NULL || local_generations == NULL) {
            sable_trap("out of memory");
        }

        for (int32_t place = places - 1; place >= local_places; place--) {
            local_generations[place] = 0;
            local_free[local_free_count++] = place;
        }

        local_places = places;
    }

    int32_t place = local_free[--local_free_count];
    int32_t generation = local_generations[place] = (local_generations[place] & 0x3fffffff) + 1;
    spin_unlock(&local_lock);
    return (int64_t)generation << 32 | place;
}

// Out of the thread's table, and destroyed.
static void local_destroy(SableThread *thread, int32_t place) {
    ThreadLocalValue value = thread->locals[place];
    thread->locals[place] = (ThreadLocalValue){ 0 };
    if (value.previous != 0) {
        thread->locals[value.previous - 1].next = value.next;
    }

    if (value.next != 0) {
        thread->locals[value.next - 1].previous = value.previous;
    } else {
        thread->last_local = value.previous;
    }

    // What its deinit does may be to ask for a thread-local.
    value.destroy(value.holder);
    sable_release(value.holder);
}

// The ThreadLocal is gone: this thread's value goes with it, and another's as
// that thread ends, or the place is taken there.
void sable_thread_local_free(int64_t key) {
    int32_t place = (int32_t)key;
    SableThread *thread = this_thread_get();
    if (thread != NULL && place < thread->local_count && thread->locals[place].key == key) {
        local_destroy(thread, place);
    }

    spin_lock(&local_lock);
    local_free[local_free_count++] = place;
    spin_unlock(&local_lock);
}

// Where this thread's value is, or 0 if it has none yet.
int64_t sable_thread_local_find(int64_t key) {
    SableThread *thread = this_thread_get();
    uint32_t place = (uint32_t)key;
    if (__builtin_expect(thread == NULL || place >= (uint32_t)thread->local_count, 0)) {
        return 0;
    }

    ThreadLocalValue *value = &thread->locals[place];
    return __builtin_expect(value->key == key, 1) ? value->cell : 0;
}

// This thread's value, made, and what destroys it. It has none.
void sable_thread_local_set(void (*destroy)(void *), void *holder, int64_t key, int64_t cell) {
    // One C made is to be told of as it ends.
    if (current_task == &main_task && !on_main_thread) {
        sable_foreign_enter();
    }

    SableThread *thread = current_thread();
    int32_t place = (int32_t)key;
    if (place >= thread->local_count) {
        int32_t count = thread->local_count == 0 ? 16 : thread->local_count;
        while (count <= place) {
            count *= 2;
        }

        thread->locals = realloc(thread->locals, (size_t)count * sizeof *thread->locals);
        if (thread->locals == NULL) {
            sable_trap("out of memory");
        }

        memset(thread->locals + thread->local_count, 0, (size_t)(count - thread->local_count) * sizeof *thread->locals);
        thread->local_count = count;
    }

    // One of a ThreadLocal that is gone; destroying it may have the table moved.
    while (thread->locals[place].key != 0) {
        local_destroy(thread, place);
    }

    if (thread->last_local != 0) {
        thread->locals[thread->last_local - 1].next = place + 1;
    }

    thread->locals[place] = (ThreadLocalValue){ key, cell, destroy, holder, thread->last_local, 0 };
    thread->last_local = place + 1;
}

// A thread's values destroyed, the latest made first — and those that their
// deinits make, which a deinit that always makes another would do for ever.
static bool locals_destroy(SableThread *thread) {
    bool any = false;
    for (int64_t left = 1 << 20; thread->last_local != 0 && left > 0; left--) {
        local_destroy(thread, thread->last_local - 1);
        any = true;
    }

    return any;
}

// As the program ends: those of the main thread, and of the pool's, which do
// not end and run nothing by now. What a thread C made has is its own: it
// may be running yet.
static bool locals_end(void) {
    bool any = locals_destroy(current_thread());
    spin_lock(&threads_lock);
    SableThread *thread = all_threads;
    spin_unlock(&threads_lock);
    for (; thread != NULL; thread = thread->next) {
        if (!thread->ends && thread != this_thread_get()) {
            any |= locals_destroy(thread);
        }
    }

    return any;
}

// ---- Threads the program starts --------------------------------------------------

// What a Thread names: one the program started, or any that asked which it is.
// It is held by the Thread objects there are of it, and by the thread itself.
typedef struct OsThread {
    int32_t references;
    int64_t id;
    char *name;                    // NULL: none
    int64_t stack_size, priority;
    void (*run)(void *holder);
    void *holder;
    PlatformMutex lock;            // of what follows
    PlatformCondition changed;
    bool ended;
    bool token;                    // an unpark that no park has taken
} OsThread;

static int64_t thread_ids;

static OsThread *os_thread_make(const char *name, size_t length) {
    OsThread *thread = calloc(1, sizeof *thread);
    if (thread == NULL) {
        sable_trap("out of memory");
    }

    thread->references = 1;
    thread->id = __atomic_add_fetch(&thread_ids, 1, __ATOMIC_RELAXED);
    mutex_init(&thread->lock);
    condition_init(&thread->changed);
    if (length > 0) {
        thread->name = malloc(length + 1);
        if (thread->name == NULL) {
            sable_trap("out of memory");
        }

        memcpy(thread->name, name, length);
        thread->name[length] = '\0';
    }

    return thread;
}

int64_t sable_thread_new(const SableString *name, int64_t stack_size, int64_t priority) {
    OsThread *thread = os_thread_make(name->bytes, (size_t)name->length);
    thread->stack_size = stack_size;
    thread->priority = priority;
    return (intptr_t)thread;
}

static void os_thread_release(OsThread *thread) {
    if (__atomic_sub_fetch(&thread->references, 1, __ATOMIC_ACQ_REL) == 0) {
        free(thread->name);
        free(thread);
    }
}

void sable_thread_release(int64_t thread) {
    os_thread_release((OsThread *)(intptr_t)thread);
}

// The calling thread, counted for the caller.
static OsThread *os_thread_current(void) {
    if (current_task == &main_task && !on_main_thread) {
        sable_foreign_enter();
    }

    SableThread *state = current_thread();
    if (state->os == NULL) {
        char name[32] = "";
        if (on_main_thread) {
            strcpy(name, "main");
        } else if (current_worker != NULL) {
            snprintf(name, sizeof name, "sable-worker-%d", current_worker->index);
        }

        state->os = os_thread_make(name, strlen(name));
    }

    __atomic_fetch_add(&state->os->references, 1, __ATOMIC_RELAXED);
    return state->os;
}

int64_t sable_thread_current(void) {
    return (intptr_t)os_thread_current();
}

int64_t sable_thread_id(int64_t thread) {
    return ((OsThread *)(intptr_t)thread)->id;
}

SableString *sable_thread_name(int64_t thread) {
    const char *name = ((OsThread *)(intptr_t)thread)->name;
    return name != NULL ? make_ctext(name) : make_string("", 0);
}

static void os_thread_main(void *argument) {
    OsThread *thread = argument;
    // With what a thread C made is given: a task to be running, which those it
    // spawns are children of, and the allocator's state.
    ForeignThread *foreign = foreign_thread_attach();
    SableThread *state = current_thread();
    state->attached = true;
    state->os = thread; // the reference it was started with
    __atomic_fetch_add(&thread->references, 1, __ATOMIC_RELAXED);
    if (thread->name != NULL) {
        thread_set_name(thread->name);
    }

    if (thread->priority != 0) {
        thread_set_priority((int)thread->priority);
    }

    thread->run(thread->holder);
    sable_release(thread->holder);

    // Its thread-locals are destroyed before it is said to have ended.
    foreign_thread_end(foreign);
    mutex_lock(&thread->lock);
    thread->ended = true;
    condition_broadcast(&thread->changed);
    mutex_unlock(&thread->lock);
    os_thread_release(thread);

    active_begin();
    __atomic_fetch_sub(&waits.blocking_running, 1, __ATOMIC_SEQ_CST);
    active_done();
}

// The thread started: it runs 'run(holder)', and then is done with the
// holder. Until it ends the program has something to wait for.
void sable_thread_start(void (*run)(void *), void *holder, int64_t handle) {
    OsThread *thread = (OsThread *)(intptr_t)handle;
    if (!PLATFORM_THREADS) {
        sable_trap("this system has one thread: no other can be started");
    }

    sable_threads_begin();
    sable_share(holder);
    thread->run = run;
    thread->holder = holder;
    __atomic_store_n(&threads_started, true, __ATOMIC_RELEASE);
    __atomic_fetch_add(&waits.blocking_running, 1, __ATOMIC_SEQ_CST);
    __atomic_fetch_add(&thread->references, 1, __ATOMIC_RELAXED);
    if (!thread_start_sized(os_thread_main, thread, thread->stack_size > 0 ? (size_t)thread->stack_size : THREAD_STACK)) {
        sable_trap("the system cannot start another thread");
    }
}

// Waits until the thread has ended, or, 'until' not negative, until that time
// of the monotonic clock at the latest: whether it has.
bool sable_thread_join(int64_t handle, int64_t until) {
    OsThread *thread = (OsThread *)(intptr_t)handle;
    if (current_thread()->os == thread) {
        sable_trap("a thread waits for itself to end");
    }

    mutex_lock(&thread->lock);
    while (!thread->ended && (until < 0 || sable_time_monotonic() < until)) {
        condition_wait(&thread->changed, &thread->lock, until);
    }

    bool ended = thread->ended;
    mutex_unlock(&thread->lock);
    return ended;
}

bool sable_thread_ended(int64_t handle) {
    OsThread *thread = (OsThread *)(intptr_t)handle;
    mutex_lock(&thread->lock);
    bool ended = thread->ended;
    mutex_unlock(&thread->lock);
    return ended;
}

// Takes the calling thread's token, waiting for one until then at the latest,
// as above: whether it had one.
bool sable_thread_park(int64_t until) {
    OsThread *thread = os_thread_current();
    mutex_lock(&thread->lock);
    while (!thread->token && (until < 0 || sable_time_monotonic() < until)) {
        condition_wait(&thread->changed, &thread->lock, until);
    }

    bool token = thread->token;
    thread->token = false;
    mutex_unlock(&thread->lock);
    os_thread_release(thread);
    return token;
}

void sable_thread_unpark(int64_t handle) {
    OsThread *thread = (OsThread *)(intptr_t)handle;
    mutex_lock(&thread->lock);
    thread->token = true;
    condition_broadcast(&thread->changed);
    mutex_unlock(&thread->lock);
}

void sable_thread_yield(void) {
    thread_yield();
}

// How many threads run the program's tasks, 0 being the main thread alone.
int64_t sable_thread_workers(void) {
    pool_start_once();
    return worker_count;
}

// WorkerPool.configure: the cores to leave to the system, which the pool
// takes as it starts — whether it will, as it has not started yet, nor does
// SABLE_WORKERS set its size, or had as many already — and the priority and
// processors each worker is to have, which it takes as it next looks for
// work (placement_follow).
bool sable_thread_pool_place(int64_t reserved, int64_t priority, const SableArray *processors) {
    int32_t *ids, count;
    if (reserved < 0 || reserved > 1 << 20 || priority < -2 || priority > 2 || !processor_ids(processors, &ids, &count)) {
        return false;
    }

    if (count == 0) {
        free(ids);
        ids = NULL;
    }

    const char *setting = getenv("SABLE_WORKERS");
    bool fixed = !PLATFORM_THREADS || (setting != NULL && setting[0] != '\0');
    spin_lock(&placement.lock);
    int32_t *before = placement.processors;
    placement.processors = ids;
    placement.count = count;
    placement.priority = (int32_t)priority;
    if (!placement.sized) {
        placement.reserved = (int32_t)reserved;
    }

    bool kept = !fixed && placement.reserved == reserved;
    __atomic_store_n(&placement.generation, placement.generation + 1, __ATOMIC_RELEASE);
    spin_unlock(&placement.lock);
    free(before);
    return kept;
}

// ---- Blocking on a task -----------------------------------------------------------

// 'Thread.blockOn': the calling thread waits for a task to say it is done. The
// main thread runs what is its to run meanwhile, as it does where its own task
// waits; a worker may not, as the task could be one it alone would run.
typedef struct TaskBlock {
    int32_t done, references; // done is under the lock, but on the main thread
    bool main;
    PlatformMutex lock;
    PlatformCondition condition;
} TaskBlock;

int64_t sable_thread_block_new(void) {
    if (current_worker != NULL) {
        sable_trap("a thread that runs tasks blocks on one: await it instead");
    }

    if (on_main_thread && current_task != &main_task) {
        sable_trap("a task of the main actor's blocks on another: await it instead");
    }

    TaskBlock *block = calloc(1, sizeof *block);
    if (block == NULL) {
        sable_trap("out of memory");
    }

    block->references = 2; // the thread's, and the task's
    block->main = on_main_thread;
    mutex_init(&block->lock);
    condition_init(&block->condition);
    return (intptr_t)block;
}

static void block_release(TaskBlock *block) {
    if (__atomic_sub_fetch(&block->references, 1, __ATOMIC_ACQ_REL) == 0) {
        free(block);
    }
}

// By the task.
void sable_thread_block_done(int64_t handle) {
    TaskBlock *block = (TaskBlock *)(intptr_t)handle;
    mutex_lock(&block->lock);
    __atomic_store_n(&block->done, 1, __ATOMIC_SEQ_CST);
    condition_broadcast(&block->condition);
    mutex_unlock(&block->lock);
    if (block->main) {
        wake_main();
    }

    block_release(block);
}

void sable_thread_block_wait(int64_t handle) {
    TaskBlock *block = (TaskBlock *)(intptr_t)handle;
    if (block->main) {
        main_loop(NULL, &block->done);
    } else {
        mutex_lock(&block->lock);
        while (!block->done) {
            condition_wait(&block->condition, &block->lock, -1);
        }

        mutex_unlock(&block->lock);
    }

    block_release(block);
}

// ---- Loops --------------------------------------------------------------------------

// A loop several threads run: the work is run by the thread that began it, and,
// at the same time, by each worker that comes to help, at most as many as were
// asked for. What they divide is the work's own affair — it takes pieces until
// none is left, and returns — so that nothing is queued: a loop no worker has
// time for is run by its thread alone, and one within another, on whatever
// thread, cannot wait for a worker that waits for it. A thread waits, at the
// loop's end, only for those that have begun to help, which have each a piece
// at most to finish.
//
// A worker looks for a loop once it finds no task. The first to find nothing
// left in one closes it, so no more come.
//
// A worker runs the work as a child of the task that began the loop: cancelled
// with it, and with its task-locals.
typedef struct Loop {
    void (*run)(void *holder);
    void *holder;
    SableTask *task;
    int64_t since;
    int32_t room;                  // for how many more; under loops_lock, as the rest of this
    bool open;                     // on the list
    struct Loop *previous, *next;
    int32_t helping;               // workers in it: what the loop's thread waits for
    int32_t waiting;               // it does
    struct Loop *next_free;
    PlatformMutex lock;
    PlatformCondition done;
} Loop;

// Waking a worker costs more than some loops do. A thread whose last few were
// brief wakes none as it begins another: it wakes them, if they are to be,
// once the loop has gone on for a while, as it takes its next piece
// (sable_thread_loop_more). Workers awake and idle see it meanwhile.
enum { BRIEF_LOOPS = 4 };

static const int64_t BRIEF = 40000;

static _Thread_local int brief_loops;
static _Thread_local Loop *unwoken_loop;

static _Alignas(128) SpinLock loops_lock;
static Loop *loops_head, *loops_tail, *loops_free;

// Off the list: under its lock.
static void loop_close(Loop *loop) {
    if (!loop->open) {
        return;
    }

    loop->open = false;
    *(loop->previous != NULL ? &loop->previous->next : &loops_head) = loop->next;
    *(loop->next != NULL ? &loop->next->previous : &loops_tail) = loop->previous;
    __atomic_fetch_sub(&loops.open, 1, __ATOMIC_SEQ_CST);
}

// Up to two workers woken to look, if they are parked: each wakes two more, as
// long as the loop has room, so that all have come soon.
static void loop_wake(int32_t room) {
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    for (int i = 0; i < 2 && i < room; i++) {
        Worker *worker = __atomic_load_n(&hot.parked, __ATOMIC_SEQ_CST) > 0 ? idle_pop() : NULL;
        if (worker != NULL) {
            notify(worker);
        } else {
            wake_reactor();
            return;
        }
    }
}

static void *helper_start(void *context) {
    return context;
}

static uint8_t helper_resume(void *frame) {
    (void)frame;
    return 1;
}

static void helper_drop(void *frame) {
    (void)frame;
}

// The loop that was opened first helped with, if there is one: whether there was.
static bool loop_help(void) {
    spin_lock(&loops_lock);
    Loop *loop = loops_head;
    if (loop == NULL) {
        spin_unlock(&loops_lock);
        return false;
    }

    __atomic_fetch_add(&loop->helping, 1, __ATOMIC_RELAXED);
    int32_t room = --loop->room;
    if (room == 0) {
        loop_close(loop);
    }

    spin_unlock(&loops_lock);
    loop_wake(room);

    SableTask *task = task_make(helper_start, helper_resume, helper_drop, NULL, loop->task->base_priority, loop->task);
    SableTask *previous = current_task;
    switch_to(task);
    loop->run(loop->holder);
    switch_to(previous);

    // None is left: no more need come. The task has finished once those it spawned have.
    spin_lock(&loops_lock);
    loop_close(loop);
    spin_unlock(&loops_lock);
    task_release(task); // the program's reference, which it has none of
    if (__atomic_sub_fetch(&task->live, 1, __ATOMIC_ACQ_REL) == 0) {
        complete(task);
    }

    // After this the loop may be another's: but it stays a loop, and the
    // lock of its condition its own, as loops are never freed.
    if (__atomic_sub_fetch(&loop->helping, 1, __ATOMIC_SEQ_CST) == 0 && __atomic_load_n(&loop->waiting, __ATOMIC_SEQ_CST)) {
        mutex_lock(&loop->lock);
        condition_broadcast(&loop->done);
        mutex_unlock(&loop->lock);
    }

    return true;
}

// The thread is to take another piece of a loop.
void sable_thread_loop_more(void) {
    Loop *loop = unwoken_loop;
    if (__builtin_expect(loop != NULL, 0) && sable_time_monotonic() - loop->since >= BRIEF / 4) {
        unwoken_loop = NULL;
        loop_wake(2);
    }
}

// 'run(holder)' run by this thread and by at most 'degree' less one others,
// and the holder let go of once all have returned. A degree of 0 is as many as
// there are workers.
void sable_thread_loop(void (*run)(void *), void *holder, int64_t degree) {
    pool_start_once();
    if (degree <= 0) {
        degree = worker_count;
    }

    if (degree > worker_count + 1) {
        degree = worker_count + 1;
    }

    if (degree <= 1 || worker_count <= 0) {
        run(holder);
        sable_release(holder);
        return;
    }

    // Others are to run it: what it holds, and the globals' values, are shared.
    sable_share(holder);
    spin_lock(&loops_lock);
    Loop *loop = loops_free;
    if (loop != NULL) {
        loops_free = loop->next_free;
    }

    spin_unlock(&loops_lock);
    if (loop == NULL) {
        loop = calloc(1, sizeof *loop);
        if (loop == NULL) {
            sable_trap("out of memory");
        }

        mutex_init(&loop->lock);
        condition_init(&loop->done);
    }

    loop->run = run;
    loop->holder = holder;
    loop->task = current_task;
    loop->room = (int32_t)degree - 1;
    loop->next = NULL;
    spin_lock(&loops_lock);
    loop->open = true;
    loop->previous = loops_tail;
    *(loops_tail != NULL ? &loops_tail->next : &loops_head) = loop;
    loops_tail = loop;
    __atomic_fetch_add(&loops.open, 1, __ATOMIC_SEQ_CST);
    spin_unlock(&loops_lock);
    Loop *outer = unwoken_loop;
    unwoken_loop = NULL;
    loop->since = sable_time_monotonic();
    if (brief_loops >= BRIEF_LOOPS) {
        unwoken_loop = loop;
    } else {
        loop_wake((int32_t)degree - 1);
    }

    run(holder);
    unwoken_loop = outer;
    brief_loops = sable_time_monotonic() - loop->since < BRIEF ? brief_loops + (brief_loops < BRIEF_LOOPS) : 0;

    spin_lock(&loops_lock);
    loop_close(loop);
    spin_unlock(&loops_lock);
    for (int turn = 0; turn < 4 * SPIN_TURNS && __atomic_load_n(&loop->helping, __ATOMIC_SEQ_CST) != 0; turn++) {
        spin_turn();
    }

    if (__atomic_load_n(&loop->helping, __ATOMIC_SEQ_CST) != 0) {
        mutex_lock(&loop->lock);
        __atomic_store_n(&loop->waiting, 1, __ATOMIC_SEQ_CST);
        while (__atomic_load_n(&loop->helping, __ATOMIC_SEQ_CST) != 0) {
            condition_wait(&loop->done, &loop->lock, -1);
        }

        __atomic_store_n(&loop->waiting, 0, __ATOMIC_SEQ_CST);
        mutex_unlock(&loop->lock);
    }

    sable_release(holder);
    spin_lock(&loops_lock);
    loop->next_free = loops_free;
    loops_free = loop;
    spin_unlock(&loops_lock);
}
