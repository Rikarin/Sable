// The Sable runtime: tasks — the executor.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Tasks -----------------------------------------------------------------

// Concurrency (docs/02 §2.7): tasks are stackless (#276). The compiler makes
// each async function a coroutine (AsyncLowering): a frame, on its task's
// slab (frames.c), a resume function that runs it until it finishes or must
// wait, and a drop function that destroys what it holds part-way. A task is
// the root of such a chain: its body's start function, which makes the root
// frame, and its resume and drop functions.
//
// The pool. Tasks run on a pool of worker threads, one per core (or as
// many as SABLE_WORKERS says), started as the first task is spawned. Each
// worker has a run queue of its own for each band of priority — a
// Chase–Lev deque, which the worker pushes to and which every worker,
// itself included, takes from at the far end, so that a worker runs its
// tasks in the order they became ready and an idle one steals the oldest.
// A task made ready on a thread that is not a worker — the program's own,
// or a timer firing — goes to the global injection queue, one per band.
// A worker looking for work takes the highest band first: its own queue,
// the injection queue, then the others'. A worker with nothing to do parks
// on a condition, until a task is queued or the earliest sleeper is due.
// Tasks never block a worker: they wait by suspending.
//
// Waiting. A task that must wait says so where it waits — on a task not yet
// finished, a group's next child, Task.yield(), a sleep, an actor, a wait
// list — and returns all the way up to the worker, having only noted what
// it waits for (task->pending). Only once its resume function has returned
// does the worker put it where it will be woken: until then another worker
// must not resume it. Whatever it waits for may have happened meanwhile;
// the worker checks, under the lock that guards it, and queues the task at
// once if so — the task asks again as it resumes.
//
// The task tree — parents, children, waiters, groups, cancellation — is
// guarded by one lock (tree_lock), held for a few instructions at a time.
// The sleepers are a heap by deadline under a lock of their own, served by
// whichever worker looks for work first once one is due; a parked worker
// waits no longer than until the earliest.
//
// Actors. Each actor has a serial executor in two words after its fields
// (SilGen.Actors.cs), whose address names the actor here: an inbox, which a task that finds the actor busy
// pushes itself onto without a lock, with a bit for 'held'; and a queue,
// which only the task holding the actor touches. A call into an actor from
// outside it hops (sable_actor_hop): the task takes the actor if no one
// holds it, and runs on; otherwise it waits in the inbox, and is handed the
// actor as its holder lets go — when the holder hops away, finishes, or
// suspends, since actors are reentrant (docs/06 §6.7): a task suspended
// inside an actor's method lets other calls in until it resumes, when it
// must take the actor again. Hopping to the actor the task is already on costs a
// comparison. The waiting tasks are handed the actor in the order of
// their priority bands, then of their arrival.
//
// The main executor. The program's own thread runs its entry (the main
// task) and every task on the main actor ('@MainActor' code): such tasks
// go to the main queue, which the main thread runs while the main task
// waits, and as the program ends. With SABLE_WORKERS=0 there is no pool:
// the main thread runs every task, one at a time, in the order they become
// ready — stage 2's single executor, deterministic, which the interpreter
// reproduces.
//
// Tasks are structured (SYNTAX.md §19). A task spawned from another is its
// child: it starts with its parent's priority, it is cancelled when its
// parent is, and its parent, once its own body has finished, finishes only
// when every child has. A detached task has no parent. Cancelling is
// cooperative: a cancelled task is marked, the cancellation handlers it has
// installed run there and then, a sleep it is in ends at once, and so on
// down through its children; the task itself goes on until it asks.
//
// Priorities order the run queues, by band (high, medium, low,
// background). A task waiting for another of a lower priority escalates it,
// and what it waits for and its children, to its own: they are queued in
// the higher band from then on. Task.currentPriority is the priority a task
// was given, escalated or not.
//
// The program ends once every task has finished or can no longer go on
// (sable_tasks_drain): with nothing queued, running or asleep, a task still
// waiting — on something no task will ever give — is dropped, its frames
// destroyed by their drop functions, so what they held is given back.

typedef struct SableHandler {
    void (*run)(void *context);   // a cancellation handler: run as the task is cancelled
    void *context;
    struct SableHandler *next;    // the one installed before it
} SableHandler;

typedef struct SableGroup SableGroup;

// What a task waits for, noted where it suspends, acted on once it has.
enum {
    PENDING_NONE,
    PENDING_WAIT,   // a task to finish
    PENDING_GROUP,  // a group's child to finish
    PENDING_YIELD,  // the others queued to run
    PENDING_SLEEP,  // its deadline
    PENDING_HOP,    // an actor, or the main thread
    PENDING_PARK,   // a wait list to be woken
    PENDING_IO,     // a socket to be ready
    PENDING_BLOCKING, // its blocking work to be done, on the blocking pool
};

// An actor's serial executor: three words at the end of the actor object.
typedef struct SableExecutor {
    uintptr_t inbox;          // tasks waiting, the latest first, with EXECUTOR_HELD
    struct SableTask *queue;  // tasks waiting, in turn order: the holder's alone
    struct SableTask *holder; // the task holding it, when known: what a waiter escalates
} SableExecutor;

enum { EXECUTOR_HELD = 1 };

// The main actor, as compiled code names it: an actor identity, not an object.
#define MAIN_ACTOR ((void *)1)

typedef struct SableTask {
    void *(*start)(void *context);   // makes the root frame
    uint8_t (*resume)(void *frame);  // runs it; its low bit: finished
    void (*drop)(void *frame);       // destroys what it holds, part-way
    void *context;
    void *frame;                     // the root frame, once begun
    struct SableTask *next;          // in a run queue, the main queue or an injection queue
    struct SableTask *next_mail;     // in an actor's inbox or queue
    struct SableTask *waiters;       // tasks waiting for this one, through 'next_waiter'
    struct SableTask *next_waiter;   // there, or in a wait list
    struct SableTask *waiting_on;    // the task it waits for, which its escalation reaches
    struct SableTask *parent;        // NULL for a detached task
    struct SableTask *children;      // children not yet finished, through 'next_sibling'
    struct SableTask *next_sibling, *previous_sibling;
    struct SableTask *next_alive, *previous_alive; // every task not yet freed
    SableGroup *group;               // the group it was spawned in, until taken from it
    struct SableTask *next_done;     // among its group's finished children
    SableHandler *handlers;          // its cancellation handlers, the latest first
    void *actor;                     // where it runs: NULL (anywhere), MAIN_ACTOR, or an actor's executor
    void *held;                      // the actor's executor it holds, or NULL
    void *pending_on;                // what it waits for
    int32_t pending;                 // PENDING_*
    int32_t io_direction;            // PENDING_IO: 0 to read, 1 to write
    bool io_background;              // and what it waits for is a background event
    void *io_source;                 // the socket it waits on, or NULL; under the source's lock
    void (*blocking_run)(void *context); // PENDING_BLOCKING: the work, and what it is given
    void *blocking_context;
    struct SableTask *next_blocking; // in the blocking pool's queue
    int32_t runnable;                // queued and not yet taken: the first to take it runs it
    int32_t inject_band;             // the injection queue it is in, or -1; under inject_lock
    int64_t deadline;                // asleep until this monotonic time
    int64_t sleeper;                 // its place among the sleepers, or -1
    uint64_t sequence;               // when it began to sleep, for ties
    int64_t base_priority;           // as given: Task.currentPriority
    int64_t priority;                // as escalated: its band
    int64_t live_children;           // children not yet finished
    bool done;
    bool finishing;                  // its body has finished, its children not yet
    bool released;                   // the program no longer holds it
    bool cancelled;                  // asked to stop: it asks Task.isCancelled, and sleeps end at once
    int64_t park_ticket;             // PENDING_PARK: not parked if the list was woken since (-1: no ticket)
    bool park_cancellable;           // PENDING_PARK: not parked if cancelled, and woken by a cancellation
    struct SableWaitList *parked_in; // the list it is parked in so that a cancellation wakes it; under tree_lock
    FrameSlab frames;
    _Alignas(FRAME_ALIGN) char first_frames[FRAME_FIRST_CHUNK];
} SableTask;

// A task group's children (Sable.Core's TaskGroup): those finished, in the
// order they finished, until taken; how many are still running; and the
// task waiting for the next, if one is.
struct SableGroup {
    SableTask *waiter;
    SableTask *done_head, *done_tail;
    int64_t running;
};

// Tasks parked until woken: what Channel's receivers wait in; and how many
// times it has been woken, the ticket a park can be made conditional on.
typedef struct SableWaitList {
    SableTask *head;
    int64_t ticket;
} SableWaitList;

// A task parked cancellably, taken out of its list. Under tree_lock.
static void unpark(SableTask *task) {
    SableWaitList *list = task->parked_in;
    if (list == NULL) {
        return;
    }

    for (SableTask **at = &list->head; *at != NULL; at = &(*at)->next_waiter) {
        if (*at == task) {
            *at = task->next_waiter;
            break;
        }
    }

    task->next_waiter = NULL;
    task->parked_in = NULL;
}

// TaskPriority.medium: the main task's, and a detached task's unless it says.
enum { PRIORITY_MEDIUM = 21, BANDS = 4 };

static int band_of(int64_t priority) {
    return priority >= 25 ? 0 : priority >= 21 ? 1 : priority >= 17 ? 2 : 3;
}

static SableTask main_task;
static _Thread_local SableTask *current_task = &main_task;
static _Thread_local bool on_main_thread;

static SpinLock tree_lock;
static SableTask *alive;

// What the executor has done, for SABLE_STATS; under tree_lock.
static int64_t tasks_spawned, tasks_alive, tasks_most, tasks_dropped, actor_escalations;

// ---- Sharing -----------------------------------------------------------------

// An object, and every object it reaches, marked as reachable from more
// than one thread: their counts are atomic from now on (objects.c). Called
// where a value crosses from one task to another — a spawned task's
// closure, a task's result, an actor call's arguments and result — before
// the other can see it. An object already marked was marked with what it
// held then; the walk stops there. Iterative, so a long list is no deep
// recursion.
static _Thread_local SableObject **share_stack;
static _Thread_local int64_t share_count, share_capacity;

static void share_push(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object_flags(object) & (SABLE_IMMORTAL | SABLE_SHARED))) {
        return;
    }

    flags_set(object, SABLE_SHARED);
    if (share_count == share_capacity) {
        share_capacity = share_capacity < 64 ? 64 : share_capacity * 2;
        share_stack = realloc(share_stack, (size_t)share_capacity * sizeof *share_stack);
        if (share_stack == NULL) {
            sable_trap("out of memory");
        }
    }

    share_stack[share_count++] = object;
}

void sable_share(void *pointer) {
    if (pointer == NULL || (object_flags(pointer) & (SABLE_IMMORTAL | SABLE_SHARED))) {
        return;
    }

    void (*saved)(void *) = visit_hook;
    visit_hook = share_push;
    int64_t base = share_count;
    share_push(pointer);
    while (share_count > base) {
        visit_object(share_stack[--share_count]);
    }

    visit_hook = saved;
}

static void share_one(void *pointer) {
    sable_share(pointer);
}

// Shares each object a value of a type known only by its metadata holds
// (#297): what its visit witness hands sable_heap_visit.
void sable_opaque_share(void *value, const SableTypeMetadata *type) {
    if ((type->flags & SABLE_TYPE_TRIVIAL) != 0) {
        return;
    }

    void (*saved)(void *) = visit_hook;
    visit_hook = share_one;
    type->witnesses->visit(value, type);
    visit_hook = saved;
}

// ---- Run queues: Chase–Lev deques ---------------------------------------------

// A worker's queue for one band: it pushes at the bottom; every worker,
// itself included, takes from the top. Its buffer grows as it fills; an old
// buffer may still be read by a thief, so it is kept until the program ends.
typedef struct DequeBuffer {
    int64_t size; // a power of two
    struct DequeBuffer *older;
    SableTask *tasks[];
} DequeBuffer;

typedef struct Deque {
    int64_t top;
    char pad1[56];
    int64_t bottom;
    char pad2[56];
    DequeBuffer *buffer;
} Deque;

static DequeBuffer *deque_buffer(int64_t size, DequeBuffer *older) {
    DequeBuffer *buffer = calloc(1, sizeof(DequeBuffer) + (size_t)size * sizeof(SableTask *));
    if (buffer == NULL) {
        sable_trap("out of memory");
    }

    buffer->size = size;
    buffer->older = older;
    return buffer;
}

static void deque_push(Deque *deque, SableTask *task) {
    int64_t bottom = __atomic_load_n(&deque->bottom, __ATOMIC_RELAXED);
    int64_t top = __atomic_load_n(&deque->top, __ATOMIC_ACQUIRE);
    DequeBuffer *buffer = __atomic_load_n(&deque->buffer, __ATOMIC_RELAXED);
    if (bottom - top > buffer->size - 1) {
        DequeBuffer *grown = deque_buffer(buffer->size * 2, buffer);
        for (int64_t i = top; i < bottom; i++) {
            grown->tasks[i & (grown->size - 1)] = __atomic_load_n(&buffer->tasks[i & (buffer->size - 1)], __ATOMIC_RELAXED);
        }

        __atomic_store_n(&deque->buffer, grown, __ATOMIC_RELEASE);
        buffer = grown;
    }

    __atomic_store_n(&buffer->tasks[bottom & (buffer->size - 1)], task, __ATOMIC_RELAXED);
    // A release store rather than a fence: the same order, and one
    // ThreadSanitizer sees.
    __atomic_store_n(&deque->bottom, bottom + 1, __ATOMIC_RELEASE);
}

// The oldest task, or NULL when there is none — or when another took it
// first, which the caller sees as none for now.
static SableTask *deque_take(Deque *deque) {
    int64_t top = __atomic_load_n(&deque->top, __ATOMIC_ACQUIRE);
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    int64_t bottom = __atomic_load_n(&deque->bottom, __ATOMIC_ACQUIRE);
    if (top >= bottom) {
        return NULL;
    }

    DequeBuffer *buffer = __atomic_load_n(&deque->buffer, __ATOMIC_ACQUIRE);
    SableTask *task = __atomic_load_n(&buffer->tasks[top & (buffer->size - 1)], __ATOMIC_RELAXED);
    if (!__atomic_compare_exchange_n(&deque->top, &top, top + 1, false, __ATOMIC_SEQ_CST, __ATOMIC_RELAXED)) {
        return NULL;
    }

    return task;
}

// ---- The pool ----------------------------------------------------------------

typedef struct Worker {
    Deque queues[BANDS];
    int index;
    // Where it parks: its own lock and condition, so that waking one
    // worker contends with no other.
    PlatformMutex lock;
    PlatformCondition condition;
    bool notified;
    bool idle;                // on the idle list, under idle_lock
    struct Worker *next_idle;
} Worker;

// The workers parked, the latest first.
static SpinLock idle_lock;
static Worker *idle_head;

static Worker *workers;
static int worker_count = -1;        // -1 until the pool is started
static _Thread_local Worker *current_worker;

// Tasks made ready off the workers, by band, in order.
static SpinLock inject_lock;
static SableTask *inject_head[BANDS], *inject_tail[BANDS];

// Tasks for the main thread: the main task, and those on the main actor.
static SpinLock main_lock;
static SableTask *main_head, *main_tail;

// Tasks in a run queue of any kind; tasks queued or running; workers parked.
static int64_t queued, active;
static int32_t parked;

static PlatformMutex park_lock;
static PlatformCondition main_condition;  // the main thread waiting

// The sleepers: a heap by deadline under its lock, and the earliest
// deadline, read without it.
static PlatformMutex timer_lock;
static SableTask **sleepers;
static int64_t sleeper_count, sleeper_capacity;
static uint64_t sleep_sequence;
static int64_t earliest_deadline = INT64_MAX;

// The I/O reactor (platform.h), opened as the pool starts or a socket is
// first watched. One thread at a time waits in it — a parked worker, or
// with no pool the main thread — until a socket it watches is ready or the
// earliest sleeper is due ('polling' says one is); the others park on
// their conditions. A wake for the thread in it is a reactor_wake.
static bool reactor_on;
static int32_t polling;

// Tasks waiting for a socket, and tasks whose blocking work is on the
// blocking pool: neither is queued or asleep, but each will go on.
static int64_t io_waiting, blocking_running;
// Waiting for a background event, which alone keeps no program going.
static int64_t background_waiting;

// Tasks that wait until no other can go on (Task.settle()), the first to
// wait first: woken one at a time, each as 'active' comes to nothing. While
// the main thread runs the entry's own code it is not counted in 'active',
// so 'main_running' says it.
static SpinLock settle_lock;
static SableTask *settle_head, *settle_tail;
static int64_t settling;
static int32_t main_running = 1;

// The blocking pool's queue (below).
static PlatformMutex blocking_lock;
static PlatformCondition blocking_condition;

static void tasks_start(void) {
    slab_init(&main_task.frames, main_task.first_frames, sizeof main_task.first_frames);
    main_task.sleeper = -1;
    main_task.inject_band = -1;
    main_task.base_priority = main_task.priority = PRIORITY_MEDIUM;
    main_task.actor = MAIN_ACTOR;
    current_frames = &main_task.frames;
    on_main_thread = true;
    mutex_init(&park_lock);
    mutex_init(&timer_lock);
    condition_init(&main_condition);
    mutex_init(&blocking_lock);
    condition_init(&blocking_condition);
}

static void switch_to(SableTask *task) {
    current_task = task;
    current_frames = &task->frames;
}

// A task runs here only on the main thread.
static bool main_bound(const SableTask *task) {
    return task == &main_task || task->actor == MAIN_ACTOR;
}

// Whether the main thread waits on its condition: only then is it signalled.
static int32_t main_waiting;

static void wake_main(void) {
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    // With no pool, the main thread may be the one in the reactor.
    if (worker_count <= 0 && __atomic_load_n(&polling, __ATOMIC_SEQ_CST)) {
        reactor_wake();
    }

    // One waker signals; the others see it no longer waiting.
    if (__atomic_load_n(&main_waiting, __ATOMIC_SEQ_CST) && __atomic_exchange_n(&main_waiting, 0, __ATOMIC_SEQ_CST)) {
        mutex_lock(&park_lock);
        condition_broadcast(&main_condition);
        mutex_unlock(&park_lock);
    }
}

// A short spin, for work that often comes right after: whether it came.
static bool spin_for(const void *what, bool pointer) {
    for (int spin = 0; spin < 4000; spin++) {
        if (pointer ? __atomic_load_n((void *const *)what, __ATOMIC_RELAXED) != NULL : __atomic_load_n((const int64_t *)what, __ATOMIC_RELAXED) > 0) {
            return true;
        }

        if ((spin & 255) == 255) {
            thread_yield();
        }
    }

    return false;
}

// Workers looking for work, spinning before they park: while one is, a
// task queued will be found without waking another.
static int32_t searching;

// A parked worker, taken off the idle list, or NULL.
static Worker *idle_pop(void) {
    spin_lock(&idle_lock);
    Worker *worker = idle_head;
    if (worker != NULL) {
        idle_head = worker->next_idle;
        worker->idle = false;
        __atomic_fetch_sub(&parked, 1, __ATOMIC_SEQ_CST);
    }

    spin_unlock(&idle_lock);
    return worker;
}

static void notify(Worker *worker) {
    mutex_lock(&worker->lock);
    worker->notified = true;
    condition_signal(&worker->condition);
    mutex_unlock(&worker->lock);
}

// A worker to look for work, if one is parked and none is looking; with no
// pool, the main thread.
static void wake_worker(void) {
    if (worker_count <= 0) {
        wake_main();
        return;
    }

    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    if (__atomic_load_n(&searching, __ATOMIC_SEQ_CST) != 0) {
        return;
    }

    Worker *worker = __atomic_load_n(&parked, __ATOMIC_SEQ_CST) > 0 ? idle_pop() : NULL;
    if (worker != NULL) {
        notify(worker);
    } else if (__atomic_load_n(&polling, __ATOMIC_SEQ_CST)) {
        // The one parked is waiting in the reactor.
        reactor_wake();
    }
}

// Every parked worker: an earlier deadline than they wait for.
static void wake_all_workers(void) {
    for (Worker *worker; (worker = idle_pop()) != NULL;) {
        notify(worker);
    }
}

// Off the injection queue's band it is in. Under inject_lock.
static void inject_unlink(SableTask *task) {
    int band = task->inject_band;
    SableTask *previous = NULL;
    for (SableTask *at = inject_head[band]; at != NULL; previous = at, at = at->next) {
        if (at != task) {
            continue;
        }

        if (previous != NULL) {
            previous->next = task->next;
        } else {
            __atomic_store_n(&inject_head[band], task->next, __ATOMIC_RELAXED);
        }

        if (inject_tail[band] == task) {
            inject_tail[band] = previous;
        }

        break;
    }

    task->inject_band = -1;
}

// At the back of its band's injection queue. A task there already — an
// escalation queued it again — moves to its band now. Whether it added an
// entry.
static bool inject_push(SableTask *task) {
    int band = band_of(__atomic_load_n(&task->priority, __ATOMIC_RELAXED));
    spin_lock(&inject_lock);
    bool added = task->inject_band < 0;
    if (!added) {
        inject_unlink(task);
    }

    task->next = NULL;
    if (inject_tail[band] != NULL) {
        inject_tail[band]->next = task;
    } else {
        __atomic_store_n(&inject_head[band], task, __ATOMIC_RELAXED);
    }

    inject_tail[band] = task;
    task->inject_band = band;
    spin_unlock(&inject_lock);
    return added;
}

static SableTask *inject_take(int band) {
    if (__atomic_load_n(&inject_head[band], __ATOMIC_RELAXED) == NULL) {
        return NULL;
    }

    spin_lock(&inject_lock);
    SableTask *task = inject_head[band];
    if (task != NULL) {
        __atomic_store_n(&inject_head[band], task->next, __ATOMIC_RELAXED);
        if (inject_head[band] == NULL) {
            inject_tail[band] = NULL;
        }

        task->inject_band = -1;
    }

    spin_unlock(&inject_lock);
    return task;
}

static void main_push(SableTask *task) {
    // With no pool the main thread runs everything in one order.
    if (worker_count <= 0) {
        if (inject_push(task)) {
            __atomic_fetch_add(&queued, 1, __ATOMIC_SEQ_CST);
        }

        wake_main();
        return;
    }

    task->next = NULL;
    spin_lock(&main_lock);
    if (main_tail != NULL) {
        main_tail->next = task;
    } else {
        __atomic_store_n(&main_head, task, __ATOMIC_RELAXED);
    }

    main_tail = task;
    spin_unlock(&main_lock);
    wake_main();
}

static SableTask *main_take(void) {
    spin_lock(&main_lock);
    SableTask *task = main_head;
    if (task != NULL) {
        __atomic_store_n(&main_head, task->next, __ATOMIC_RELAXED);
        if (main_head == NULL) {
            main_tail = NULL;
        }
    }

    spin_unlock(&main_lock);
    return task;
}

// Runnable: queued where a thread that may run it will find it. It counts
// as active until it has run and waits, or finishes.
// 'behind' puts it behind every task queued in its band, as Task.yield()
// asks: in the injection queue, which every worker takes from.
static void schedule_at(SableTask *task, bool behind) {
    __atomic_fetch_add(&active, 1, __ATOMIC_SEQ_CST);
    __atomic_store_n(&task->runnable, 1, __ATOMIC_RELEASE);
    if (main_bound(task)) {
        main_push(task);
        return;
    }

    bool added = true;
    if (current_worker != NULL && !behind) {
        deque_push(&current_worker->queues[band_of(__atomic_load_n(&task->priority, __ATOMIC_RELAXED))], task);
    } else {
        added = inject_push(task);
    }

    if (added) {
        __atomic_fetch_add(&queued, 1, __ATOMIC_SEQ_CST);
    }

    wake_worker();
}

static void schedule(SableTask *task) {
    schedule_at(task, false);
}

// A task queued, and since escalated: queued again in its new band, in
// the injection queue. Whichever of its entries is taken first runs it;
// the other is passed over (find_work).
static void requeue(SableTask *task) {
    if (main_bound(task) || !__atomic_load_n(&task->runnable, __ATOMIC_ACQUIRE)) {
        return;
    }

    if (inject_push(task)) {
        __atomic_fetch_add(&queued, 1, __ATOMIC_SEQ_CST);
    }

    wake_worker();
}

// Nothing queued, running, asleep, waiting for a socket or blocking:
// nothing more will happen.
static bool quiescent(void) {
    return __atomic_load_n(&active, __ATOMIC_SEQ_CST) == 0 && __atomic_load_n(&sleeper_count, __ATOMIC_SEQ_CST) == 0
           && __atomic_load_n(&io_waiting, __ATOMIC_SEQ_CST) == 0 && __atomic_load_n(&blocking_running, __ATOMIC_SEQ_CST) == 0
           && __atomic_load_n(&settling, __ATOMIC_SEQ_CST) == 0;
}

// Nothing may be queued or running any more: if so, the first of the tasks
// that wait for that goes on. Whoever makes it so calls this after, and two
// that do both look under the lock, where the first leaves a task active.
static void wake_settler(void) {
    if (__atomic_load_n(&settling, __ATOMIC_SEQ_CST) == 0) {
        return;
    }

    spin_lock(&settle_lock);
    SableTask *task = settle_head;
    if (task != NULL && __atomic_load_n(&active, __ATOMIC_SEQ_CST) == 0 && !__atomic_load_n(&main_running, __ATOMIC_SEQ_CST)) {
        settle_head = task->next_waiter;
        if (settle_head == NULL) {
            settle_tail = NULL;
        }

        // Active first, so that the program never looks finished between.
        schedule(task);
        __atomic_fetch_sub(&settling, 1, __ATOMIC_SEQ_CST);
    }

    spin_unlock(&settle_lock);
}

// A task has run and waits, or finished: one that waits for that goes on,
// or else the main thread hears when it leaves nothing to do.
static void active_done(void) {
    if (__atomic_sub_fetch(&active, 1, __ATOMIC_SEQ_CST) == 0) {
        wake_settler();
        if (quiescent()) {
            wake_main();
        }
    }
}

// The highest band's oldest task: a worker's own, then the injected, then
// another worker's — the injected first every so often, so that a worker
// whose own tasks keep it busy does not starve them. With no pool, the
// injected alone.
static _Thread_local uint32_t polls;

static SableTask *find_work(Worker *self) {
    bool injected_first = (++polls & 15) == 0;
    for (int band = 0; band < BANDS; band++) {
        SableTask *task = injected_first ? inject_take(band) : NULL;
        if (task == NULL && self != NULL) {
            task = deque_take(&self->queues[band]);
        }

        if (task == NULL && !injected_first) {
            task = inject_take(band);
        }

        for (int i = 1; task == NULL && self != NULL && i < worker_count; i++) {
            task = deque_take(&workers[(self->index + i) % worker_count].queues[band]);
        }

        if (task != NULL) {
            __atomic_fetch_sub(&queued, 1, __ATOMIC_SEQ_CST);
            int32_t was = 1;
            if (__atomic_compare_exchange_n(&task->runnable, &was, 0, false, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED)) {
                return task;
            }

            // Run already, by an entry an escalation added: this one is passed over.
            band--;
        }
    }

    return NULL;
}

// ---- The sleepers: a binary heap, the earliest deadline first ----------------

static bool sleeps_less(const SableTask *a, const SableTask *b) {
    return a->deadline < b->deadline || (a->deadline == b->deadline && a->sequence < b->sequence);
}

static void sleeper_place(int64_t index, SableTask *task) {
    sleepers[index] = task;
    task->sleeper = index;
}

static void sleepers_up(int64_t index) {
    SableTask *task = sleepers[index];
    while (index > 0) {
        int64_t parent = (index - 1) / 2;
        if (!sleeps_less(task, sleepers[parent])) {
            break;
        }

        sleeper_place(index, sleepers[parent]);
        index = parent;
    }

    sleeper_place(index, task);
}

static void sleepers_down(int64_t index) {
    SableTask *task = sleepers[index];
    for (;;) {
        int64_t child = index * 2 + 1;
        if (child >= sleeper_count) {
            break;
        }

        if (child + 1 < sleeper_count && sleeps_less(sleepers[child + 1], sleepers[child])) {
            child++;
        }

        if (!sleeps_less(sleepers[child], task)) {
            break;
        }

        sleeper_place(index, sleepers[child]);
        index = child;
    }

    sleeper_place(index, task);
}

static void earliest_update(void) {
    __atomic_store_n(&earliest_deadline, sleeper_count > 0 ? sleepers[0]->deadline : INT64_MAX, __ATOMIC_SEQ_CST);
}

// Under timer_lock. A deadline earlier than any before wakes the parked,
// whose waits end at the earliest they knew of.
static void sleepers_add(SableTask *task) {
    if (sleeper_count == sleeper_capacity) {
        int64_t capacity = sleeper_capacity == 0 ? 64 : sleeper_capacity * 2;
        SableTask **grown = realloc(sleepers, (size_t)capacity * sizeof *sleepers);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        sleepers = grown;
        sleeper_capacity = capacity;
    }

    task->sequence = sleep_sequence++;
    sleeper_place(sleeper_count, task);
    __atomic_store_n(&sleeper_count, sleeper_count + 1, __ATOMIC_SEQ_CST);
    sleepers_up(sleeper_count - 1);
    bool earlier = task->deadline < __atomic_load_n(&earliest_deadline, __ATOMIC_SEQ_CST);
    earliest_update();
    if (earlier) {
        wake_all_workers();
        mutex_lock(&park_lock);
        condition_broadcast(&main_condition);
        mutex_unlock(&park_lock);
        if (__atomic_load_n(&polling, __ATOMIC_SEQ_CST)) {
            reactor_wake();
        }
    }
}

// Under timer_lock.
static void sleepers_remove(SableTask *task) {
    int64_t index = task->sleeper;
    task->sleeper = -1;
    SableTask *last = sleepers[sleeper_count - 1];
    __atomic_store_n(&sleeper_count, sleeper_count - 1, __ATOMIC_SEQ_CST);
    if (index != sleeper_count) {
        sleeper_place(index, last);
        sleepers_up(index);
        sleepers_down(last->sleeper);
    }

    earliest_update();
}

// Every sleeper whose time has come, queued in the order of their
// deadlines — each made active before it stops sleeping, so that the
// program never looks finished between.
static void wake_sleepers(void) {
    if (sable_time_monotonic() < __atomic_load_n(&earliest_deadline, __ATOMIC_SEQ_CST)) {
        return;
    }

    mutex_lock(&timer_lock);
    int64_t now = sable_time_monotonic();
    while (sleeper_count > 0 && sleepers[0]->deadline <= now) {
        SableTask *task = sleepers[0];
        __atomic_fetch_add(&active, 1, __ATOMIC_SEQ_CST);
        sleepers_remove(task);
        schedule(task);
        active_done();
    }

    if (sleeper_count == 0) {
        free(sleepers);
        sleepers = NULL;
        sleeper_capacity = 0;
    }

    mutex_unlock(&timer_lock);
}

// ---- I/O: sockets the reactor watches ----------------------------------------

// A socket a task may wait on (Sable.IO's TcpListener and TcpStream, net.c):
// added to the reactor once, edge-triggered for both directions, as Tokio's
// and Go's are. What the reactor has said is kept here, so a readiness that
// comes while no task waits is not lost: 'ready' has a bit per direction,
// set as the reactor reports it and cleared only when a call finds the
// socket would block with no report since it began ('ticks'). A task waits
// only while its direction is not ready.
//
// A source is never given back to the system: closed, it goes to a free
// list and is used again for another socket, its generation bumped. A
// handle carries the generation it was made with, so one kept past its
// close names nothing; and a report the reactor had already gathered for
// the old socket finds a source that is still memory, where at worst it
// marks the new socket ready for nothing — whose call then finds it would
// block, and waits.
//
// A source may be a pipe (a child's stream) rather than a socket; or an
// event, which the reactor does not watch: reactor_ready says when it
// happens (a child ended, a signal came). A background event is one that
// may never happen, and the program need not wait for: one the main task
// waits for is waited for, but a program whose main has returned ends,
// whatever other tasks still wait for one.
enum { SOURCE_SOCKET, SOURCE_PIPE, SOURCE_EVENT, SOURCE_BACKGROUND };

typedef struct IoSource {
    SpinLock lock;
    PlatformSocket socket;      // or the pipe; -1 once closed
    uint8_t kind;               // SOURCE_*
    int32_t port;               // a listener's, as bound
    uint16_t generation;
    uint8_t ready;              // REACTOR_* reported and not yet used up
    uint64_t ticks[2];          // reports, to read and to write
    SableTask *waiter[2];       // the task waiting to read, to write
    struct IoSource *next_free;
} IoSource;

static SpinLock sources_lock;
static IoSource *free_sources;

// A handle: the source's address, its generation in the top 16 bits.
static int64_t source_handle(IoSource *source) {
    return (int64_t)(((uint64_t)source->generation << 48) | (uint64_t)(uintptr_t)source);
}

// The source a handle names, or NULL once it is closed.
static IoSource *source_of(int64_t handle) {
    IoSource *source = (IoSource *)(uintptr_t)((uint64_t)handle & 0xFFFFFFFFFFFFull);
    if (source == NULL || (uint16_t)((uint64_t)handle >> 48) != __atomic_load_n(&source->generation, __ATOMIC_ACQUIRE)) {
        return NULL;
    }

    return source;
}

// The reactor, opened the first time it is needed.
static bool reactor_start(void) {
    if (__atomic_load_n(&reactor_on, __ATOMIC_ACQUIRE)) {
        return true;
    }

    static SpinLock opening;
    spin_lock(&opening);
    if (!reactor_on && reactor_open()) {
        __atomic_store_n(&reactor_on, true, __ATOMIC_RELEASE);
    }

    spin_unlock(&opening);
    return reactor_on;
}

static void io_source_free(IoSource *source) {
    spin_lock(&sources_lock);
    source->next_free = free_sources;
    free_sources = source;
    spin_unlock(&sources_lock);
}

static void source_close(PlatformSocket socket, uint8_t kind) {
    if (kind == SOURCE_SOCKET) {
        socket_close(socket);
    } else if (kind == SOURCE_PIPE) {
        pipe_close(socket);
    }
}

// The socket watched from now on: its handle, or 0 — the socket closed —
// when the reactor will not take it. 'ready' is what it is taken to be
// ready for until the reactor says more: both, to be tried first; for a
// connection still being made, not writing.
static int64_t io_source_open(PlatformSocket socket, uint8_t kind, uint8_t ready) {
    if (!reactor_start()) {
        source_close(socket, kind);
        return 0;
    }

    spin_lock(&sources_lock);
    IoSource *source = free_sources;
    if (source != NULL) {
        free_sources = source->next_free;
    }

    spin_unlock(&sources_lock);
    if (source == NULL) {
        source = calloc(1, sizeof(IoSource));
        if (source == NULL) {
            sable_trap("out of memory");
        }
    }

    spin_lock(&source->lock);
    __atomic_store_n(&source->socket, socket, __ATOMIC_RELEASE);
    source->kind = kind;
    source->port = 0;
    source->ready = ready;
    source->waiter[0] = source->waiter[1] = NULL;
    int64_t handle = source_handle(source);
    spin_unlock(&source->lock);
    bool watched = kind == SOURCE_SOCKET ? reactor_add(socket, source) : kind == SOURCE_PIPE ? pipe_watch(socket, source) : true;
    if (!watched) {
        spin_lock(&source->lock);
        __atomic_store_n(&source->socket, -1, __ATOMIC_RELEASE);
        __atomic_store_n(&source->generation, (uint16_t)(source->generation + 1), __ATOMIC_RELEASE);
        spin_unlock(&source->lock);
        source_close(socket, kind);
        io_source_free(source);
        return 0;
    }

    return handle;
}

// A task taken from a source's waiters, queued: active before it stops
// waiting, so the program never looks finished between.
static void io_resume(SableTask *task) {
    __atomic_fetch_add(&active, 1, __ATOMIC_SEQ_CST);
    __atomic_fetch_sub(task->io_background ? &background_waiting : &io_waiting, 1, __ATOMIC_SEQ_CST);
    schedule(task);
    active_done();
}

// Under the source's lock: the task waiting in that direction, taken, or NULL.
static SableTask *io_take_waiter(IoSource *source, int direction) {
    SableTask *task = source->waiter[direction];
    if (task != NULL) {
        source->waiter[direction] = NULL;
        __atomic_store_n(&task->io_source, NULL, __ATOMIC_RELEASE);
    }

    return task;
}

// The socket closed: no longer watched, and whoever waits on it woken, to
// find it closed. Its handle names nothing from now on.
static void io_source_close(int64_t handle) {
    IoSource *source = source_of(handle);
    if (source == NULL) {
        return;
    }

    spin_lock(&source->lock);
    if (source->generation != (uint16_t)((uint64_t)handle >> 48)) {
        spin_unlock(&source->lock);
        return;
    }

    PlatformSocket socket = source->socket;
    uint8_t kind = source->kind;
    __atomic_store_n(&source->socket, -1, __ATOMIC_RELEASE);
    __atomic_store_n(&source->generation, (uint16_t)(source->generation + 1), __ATOMIC_RELEASE);
    SableTask *reader = io_take_waiter(source, 0);
    SableTask *writer = io_take_waiter(source, 1);
    spin_unlock(&source->lock);
    if (kind == SOURCE_SOCKET) {
        reactor_remove(socket);
    } else if (kind == SOURCE_PIPE) {
        pipe_unwatch(socket);
    }

    source_close(socket, kind);
    if (reader != NULL) {
        io_resume(reader);
    }

    if (writer != NULL) {
        io_resume(writer);
    }

    io_source_free(source);
}

// The reports in a direction so far: taken before a call that may find
// the socket would block.
static uint64_t io_ticks(IoSource *source, int direction) {
    spin_lock(&source->lock);
    uint64_t ticks = source->ticks[direction];
    spin_unlock(&source->lock);
    return ticks;
}

// The call found the socket would block: not ready that way, unless the
// reactor has said otherwise since it began.
static void io_would_block(IoSource *source, int direction, uint64_t ticks) {
    spin_lock(&source->lock);
    if (source->ticks[direction] == ticks) {
        source->ready &= (uint8_t) ~(direction == 0 ? REACTOR_READABLE : REACTOR_WRITABLE);
    }

    spin_unlock(&source->lock);
}

// The reactor says the socket is ready: noted, and the tasks waiting for it woken.
static void io_ready(IoSource *source, int events) {
    SableTask *woken[2] = { NULL, NULL };
    spin_lock(&source->lock);
    if (source->socket != -1) {
        for (int direction = 0; direction < 2; direction++) {
            if (events & (direction == 0 ? REACTOR_READABLE : REACTOR_WRITABLE)) {
                source->ticks[direction]++;
                source->ready |= (uint8_t)(direction == 0 ? REACTOR_READABLE : REACTOR_WRITABLE);
                woken[direction] = io_take_waiter(source, direction);
            }
        }
    }

    spin_unlock(&source->lock);
    for (int direction = 0; direction < 2; direction++) {
        if (woken[direction] != NULL) {
            io_resume(woken[direction]);
        }
    }
}

static void reactor_ready(void *key, int events) {
    io_ready(key, events);
}

// An event that has not happened: its handle, or 0. No reactor watches it,
// but waiting for one is as waiting for a socket is, and the thread it
// happens on may be any: io_event says it has.
static int64_t io_event_open(bool background) {
    return io_source_open(0, background ? SOURCE_BACKGROUND : SOURCE_EVENT, 0);
}

static void io_event(int64_t handle) {
    IoSource *source = source_of(handle);
    if (source != NULL) {
        reactor_ready(source, REACTOR_READABLE);
    }
}

// The event is to happen again before a task waiting for it goes on.
static void io_event_reset(int64_t handle) {
    IoSource *source = source_of(handle);
    if (source != NULL) {
        spin_lock(&source->lock);
        source->ready = 0;
        spin_unlock(&source->lock);
    }
}

// This thread waits in the reactor — until a socket is ready, a wake, or
// the earliest sleeper's deadline; or, not blocking, only looks — if no
// other thread is in it, and wakes the tasks it says may go on. Whether it
// was this thread's turn. The sleepers due are served by whoever looks for
// work next.
static bool poll_reactor(bool block) {
    if (!__atomic_load_n(&reactor_on, __ATOMIC_ACQUIRE) || __atomic_load_n(&polling, __ATOMIC_RELAXED)) {
        return false;
    }

    int32_t free_now = 0;
    if (!__atomic_compare_exchange_n(&polling, &free_now, 1, false, __ATOMIC_SEQ_CST, __ATOMIC_RELAXED)) {
        return false;
    }

    // Looked at after 'polling' is set: a task queued after this sees it
    // set, and wakes the reactor.
    if (block && __atomic_load_n(&queued, __ATOMIC_SEQ_CST) > 0) {
        __atomic_store_n(&polling, 0, __ATOMIC_SEQ_CST);
        return true;
    }

    ReactorEvent events[64];
    int64_t earliest = __atomic_load_n(&earliest_deadline, __ATOMIC_SEQ_CST);
    int n = reactor_wait(events, 64, !block ? 0 : earliest == INT64_MAX ? -1 : earliest);
    __atomic_store_n(&polling, 0, __ATOMIC_SEQ_CST);
    for (int i = 0; i < n; i++) {
        io_ready(events[i].key, events[i].events);
    }

    return true;
}

// 'await Task.waitForIO(handle, writable)': whether the running task must
// wait for the socket to be ready that way. Not when it is ready already,
// nor when it is closed — the call made next says so.
bool sable_task_io_wait(int64_t handle, bool writable) {
    IoSource *source = source_of(handle);
    if (source == NULL) {
        return false;
    }

    int bit = writable ? REACTOR_WRITABLE : REACTOR_READABLE;
    spin_lock(&source->lock);
    bool ready = (source->ready & bit) != 0 || source->socket == -1;
    spin_unlock(&source->lock);
    if (ready) {
        return false;
    }

    current_task->pending = PENDING_IO;
    current_task->pending_on = source;
    current_task->io_direction = writable ? 1 : 0;
    return true;
}

// The task has suspended to wait on the source: among its waiters, unless
// the socket became ready, or closed, or the task was cancelled, meanwhile.
static void io_wait_after_suspend(SableTask *task, IoSource *source) {
    int direction = task->io_direction;
    spin_lock(&source->lock);
    task->io_background = source->kind == SOURCE_BACKGROUND;
    __atomic_fetch_add(task->io_background ? &background_waiting : &io_waiting, 1, __ATOMIC_SEQ_CST);
    bool go_on = (source->ready & (direction == 0 ? REACTOR_READABLE : REACTOR_WRITABLE)) != 0 || source->socket == -1
                 || __atomic_load_n(&task->cancelled, __ATOMIC_ACQUIRE);
    if (!go_on) {
        if (source->waiter[direction] != NULL) {
            spin_unlock(&source->lock);
            sable_trap("two tasks wait to use one socket the same way at once");
        }

        source->waiter[direction] = task;
        __atomic_store_n(&task->io_source, source, __ATOMIC_RELEASE);
    }

    spin_unlock(&source->lock);
    if (go_on) {
        io_resume(task);
    }
}

// A task cancelled, or dropped, while it waits on a socket: woken, or
// forgotten, now.
static void io_cancel(SableTask *task, bool resume) {
    IoSource *source = __atomic_load_n(&task->io_source, __ATOMIC_ACQUIRE);
    if (source == NULL) {
        return;
    }

    spin_lock(&source->lock);
    bool taken = false;
    for (int direction = 0; direction < 2; direction++) {
        if (source->waiter[direction] == task) {
            io_take_waiter(source, direction);
            taken = true;
        }
    }

    spin_unlock(&source->lock);
    if (taken && resume) {
        io_resume(task);
    } else if (taken) {
        __atomic_fetch_sub(task->io_background ? &background_waiting : &io_waiting, 1, __ATOMIC_SEQ_CST);
    }
}

// ---- The blocking pool ---------------------------------------------------------

// Work that must block — a call into C that waits, a file read whole —
// runs on threads of its own, as Tokio's spawn_blocking does, so that no
// worker waits: 'await Task.blocking { … }' suspends its task, the work
// runs on a blocking thread, and the task is queued again once it is done.
// Threads are made as work comes and none is free, up to BLOCKING_MOST
// (or SABLE_BLOCKING_THREADS), and kept, waiting for more.
enum { BLOCKING_MOST = 512 };

static SableTask *blocking_head, *blocking_tail;
static int32_t blocking_threads, blocking_idle, blocking_queued, blocking_most;

// The work of a task in the blocking pool, run; the task queued again.
static void blocking_work(SableTask *task) {
    // The work runs as its task: Task.isCancelled asks it.
    SableTask *previous = current_task;
    switch_to(task);
    task->blocking_run(task->blocking_context);
    switch_to(previous);

    __atomic_fetch_add(&active, 1, __ATOMIC_SEQ_CST);
    __atomic_fetch_sub(&blocking_running, 1, __ATOMIC_SEQ_CST);
    schedule(task);
    active_done();
}

static void blocking_main(void *argument) {
    (void)argument;
    for (;;) {
        mutex_lock(&blocking_lock);
        while (blocking_head == NULL) {
            blocking_idle++;
            condition_wait(&blocking_condition, &blocking_lock, -1);
            blocking_idle--;
        }

        SableTask *task = blocking_head;
        blocking_head = task->next_blocking;
        if (blocking_head == NULL) {
            blocking_tail = NULL;
        }

        blocking_queued--;
        mutex_unlock(&blocking_lock);
        blocking_work(task);
    }
}

static void blocking_submit(SableTask *task) {
    __atomic_fetch_add(&blocking_running, 1, __ATOMIC_SEQ_CST);
    if (!PLATFORM_THREADS) {
        // No thread to give it to: it runs here, and the task goes on after.
        blocking_work(task);
        return;
    }

    mutex_lock(&blocking_lock);
    if (blocking_most == 0) {
        const char *setting = getenv("SABLE_BLOCKING_THREADS");
        blocking_most = setting != NULL && atoi(setting) > 0 ? atoi(setting) : BLOCKING_MOST;
    }

    task->next_blocking = NULL;
    if (blocking_tail != NULL) {
        blocking_tail->next_blocking = task;
    } else {
        blocking_head = task;
    }

    blocking_tail = task;
    blocking_queued++;
    bool more = blocking_queued > blocking_idle && blocking_threads < blocking_most;
    if (more) {
        blocking_threads++;
    }

    condition_signal(&blocking_condition);
    mutex_unlock(&blocking_lock);
    if (more && !thread_start(blocking_main, NULL)) {
        sable_trap("cannot start a thread for blocking work");
    }
}

// 'await Task.blocking { … }': the running task suspends, and 'run(context)'
// runs on the blocking pool; the task goes on once it has. The context is
// the closure's, which another thread runs: shared. Always a suspension.
bool sable_task_blocking(void (*run)(void *), void *context) {
    sable_share(context);
    current_task->pending = PENDING_BLOCKING;
    current_task->blocking_run = run;
    current_task->blocking_context = context;
    return true;
}

// ---- Actors' executors -------------------------------------------------------

// An actor is named by its executor's address.
static SableExecutor *executor_of(void *actor) {
    return actor;
}

static void escalate(SableTask *task, int64_t priority);

// The actor taken by the task, if no one holds it.
static bool executor_try_take(SableExecutor *executor, SableTask *task) {
    uintptr_t free_now = 0;
    if (!__atomic_compare_exchange_n(&executor->inbox, &free_now, EXECUTOR_HELD, false, __ATOMIC_ACQUIRE, __ATOMIC_RELAXED)) {
        return false;
    }

    __atomic_store_n(&executor->holder, task, __ATOMIC_RELEASE);
    return true;
}

// The actor taken for the task, or the task in its inbox, to be handed it.
// A task that waits escalates the holder to its own priority, so that a
// lower-priority holder, queued behind others, does not keep it waiting
// (docs/06 §6.7). The holder is read under the tree's lock: a task is freed
// only under it, and lets the actor go — clearing 'holder' — before.
static bool executor_take_or_wait(SableExecutor *executor, SableTask *task) {
    // Read now: once in the inbox, the task is another thread's to run.
    int64_t priority = __atomic_load_n(&task->priority, __ATOMIC_RELAXED);
    uintptr_t inbox = __atomic_load_n(&executor->inbox, __ATOMIC_RELAXED);
    for (;;) {
        if (inbox == 0) {
            if (__atomic_compare_exchange_n(&executor->inbox, &inbox, EXECUTOR_HELD, true, __ATOMIC_ACQUIRE, __ATOMIC_RELAXED)) {
                __atomic_store_n(&executor->holder, task, __ATOMIC_RELEASE);
                return true;
            }

            continue;
        }

        task->next_mail = (SableTask *)(inbox & ~(uintptr_t)EXECUTOR_HELD);
        if (__atomic_compare_exchange_n(&executor->inbox, &inbox, (uintptr_t)task | EXECUTOR_HELD, true, __ATOMIC_RELEASE, __ATOMIC_RELAXED)) {
            spin_lock(&tree_lock);
            SableTask *holder = __atomic_load_n(&executor->holder, __ATOMIC_ACQUIRE);
            if (holder != NULL && holder != task && __atomic_load_n(&holder->priority, __ATOMIC_RELAXED) < priority) {
                escalate(holder, priority);
                actor_escalations++;
            }

            spin_unlock(&tree_lock);
            return false;
        }
    }
}

// The holder lets the actor go: to the next waiting, by band and then in
// the order they came, who is queued holding it; or to no one.
static void executor_release(void *actor) {
    SableExecutor *executor = executor_of(actor);
    for (;;) {
        uintptr_t inbox = __atomic_load_n(&executor->inbox, __ATOMIC_ACQUIRE);
        if (inbox != EXECUTOR_HELD) {
            // Waiters came: all of them taken, the latest first, into the queue.
            SableTask *arrived = (SableTask *)(__atomic_exchange_n(&executor->inbox, EXECUTOR_HELD, __ATOMIC_ACQUIRE) & ~(uintptr_t)EXECUTOR_HELD);
            SableTask *in_order = NULL;
            while (arrived != NULL) {
                SableTask *after = arrived->next_mail;
                arrived->next_mail = in_order;
                in_order = arrived;
                arrived = after;
            }

            while (in_order != NULL) {
                SableTask *task = in_order;
                in_order = task->next_mail;
                int band = band_of(__atomic_load_n(&task->priority, __ATOMIC_RELAXED));
                SableTask **at = &executor->queue;
                while (*at != NULL && band_of(__atomic_load_n(&(*at)->priority, __ATOMIC_RELAXED)) <= band) {
                    at = &(*at)->next_mail;
                }

                task->next_mail = *at;
                *at = task;
            }
        }

        if (executor->queue != NULL) {
            break;
        }

        __atomic_store_n(&executor->holder, NULL, __ATOMIC_RELEASE);
        uintptr_t held = EXECUTOR_HELD;
        if (__atomic_compare_exchange_n(&executor->inbox, &held, 0, false, __ATOMIC_RELEASE, __ATOMIC_RELAXED)) {
            return;
        }
    }

    SableTask *next = executor->queue;
    executor->queue = next->next_mail;
    next->held = actor;
    __atomic_store_n(&executor->holder, next, __ATOMIC_RELEASE);
    schedule(next);
}

// ---- Running a task ------------------------------------------------------------

static void free_task(SableTask *task) {
    if (task->previous_alive != NULL) {
        task->previous_alive->next_alive = task->next_alive;
    } else {
        alive = task->next_alive;
    }

    if (task->next_alive != NULL) {
        task->next_alive->previous_alive = task->previous_alive;
    }

    slab_destroy(&task->frames);
    free(task);
    tasks_alive--;
}

// Off its parent's children: it has finished, or is dropped. Under tree_lock.
static void leave_parent(SableTask *task) {
    SableTask *parent = task->parent;
    if (parent == NULL) {
        return;
    }

    if (task->previous_sibling != NULL) {
        task->previous_sibling->next_sibling = task->next_sibling;
    } else {
        parent->children = task->next_sibling;
    }

    if (task->next_sibling != NULL) {
        task->next_sibling->previous_sibling = task->previous_sibling;
    }

    task->parent = task->next_sibling = task->previous_sibling = NULL;
    parent->live_children--;
}

// Finished, its children too: whoever waits for it may go on, its group
// hears of it, and a parent waiting only for it finishes as well. Under tree_lock.
static void complete(SableTask *task) {
    __atomic_store_n(&task->done, true, __ATOMIC_RELEASE);
    task->finishing = false;
    for (SableTask *waiter = task->waiters; waiter != NULL;) {
        SableTask *after = waiter->next_waiter;
        waiter->waiting_on = NULL;
        schedule(waiter);
        waiter = after;
    }

    task->waiters = NULL;

    SableGroup *group = task->group;
    if (group != NULL) {
        task->next_done = NULL;
        if (group->done_tail != NULL) {
            group->done_tail->next_done = task;
        } else {
            group->done_head = task;
        }

        group->done_tail = task;
        group->running--;
        if (group->waiter != NULL) {
            schedule(group->waiter);
            group->waiter = NULL;
        }
    }

    SableTask *parent = task->parent;
    leave_parent(task);
    if (task->released) {
        free_task(task);
    }

    if (parent != NULL && parent->finishing && parent->live_children == 0) {
        complete(parent);
    }
}

// Raised to at least this priority, with what it waits for and its
// children: a higher-priority task waits for it. One queued already moves
// to its new band now. Under tree_lock.
static void escalate(SableTask *task, int64_t priority) {
    while (task != NULL && !task->done && __atomic_load_n(&task->priority, __ATOMIC_RELAXED) < priority) {
        bool higher_band = band_of(priority) < band_of(task->priority);
        __atomic_store_n(&task->priority, priority, __ATOMIC_RELAXED);
        if (higher_band) {
            requeue(task);
        }

        for (SableTask *child = task->children; child != NULL; child = child->next_sibling) {
            escalate(child, priority);
        }

        task = task->waiting_on;
    }
}

// The task has suspended: it is put where it will be woken, now that no
// frame of it runs, and the actor it held is let go — after, so that a wait
// list it parks in under the actor misses nothing.
static void after_suspend(SableTask *task) {
    int32_t pending = task->pending;
    void *on = task->pending_on;
    void *release = task->held;
    task->pending = PENDING_NONE;
    task->held = NULL;

    switch (pending) {
        case PENDING_WAIT: {
            SableTask *target = on;
            spin_lock(&tree_lock);
            if (target->done) {
                schedule(task);
            } else {
                task->next_waiter = target->waiters;
                target->waiters = task;
                task->waiting_on = target;
                escalate(target, __atomic_load_n(&task->priority, __ATOMIC_RELAXED));
            }

            spin_unlock(&tree_lock);
            break;
        }

        case PENDING_GROUP: {
            SableGroup *group = on;
            spin_lock(&tree_lock);
            if (group->done_head != NULL || group->running == 0) {
                schedule(task);
            } else {
                group->waiter = task;
            }

            spin_unlock(&tree_lock);
            break;
        }

        case PENDING_SLEEP:
            mutex_lock(&timer_lock);
            if (__atomic_load_n(&task->cancelled, __ATOMIC_ACQUIRE)) {
                schedule(task);
            } else {
                sleepers_add(task);
            }

            mutex_unlock(&timer_lock);
            break;

        case PENDING_PARK: {
            SableWaitList *list = on;
            if (list == NULL) {
                // Until no other task can go on: woken as this one, and
                // every other, stops being active.
                spin_lock(&settle_lock);
                task->next_waiter = NULL;
                if (settle_tail != NULL) {
                    settle_tail->next_waiter = task;
                } else {
                    settle_head = task;
                }

                settle_tail = task;
                __atomic_fetch_add(&settling, 1, __ATOMIC_SEQ_CST);
                spin_unlock(&settle_lock);
                break;
            }

            spin_lock(&tree_lock);
            if (task->park_ticket >= 0 && list->ticket != task->park_ticket
                || task->park_cancellable && __atomic_load_n(&task->cancelled, __ATOMIC_RELAXED)) {
                // Woken since its ticket, or cancelled: it goes on at once.
                schedule(task);
            } else {
                task->next_waiter = list->head;
                list->head = task;
                if (task->park_cancellable) {
                    task->parked_in = list;
                }
            }

            task->park_ticket = -1;
            task->park_cancellable = false;
            spin_unlock(&tree_lock);
            break;
        }

        case PENDING_YIELD:
            schedule_at(task, true);
            break;

        case PENDING_IO:
            io_wait_after_suspend(task, on);
            break;

        case PENDING_BLOCKING:
            blocking_submit(task);
            break;

        default:
            // A hop: queued again, to take its actor as it runs.
            schedule(task);
            break;
    }

    if (release != NULL) {
        executor_release(release);
    }
}

// Resumes a task until it finishes or waits: its root frame made the first time.
static void step(SableTask *task) {
    SableTask *previous = current_task;
    switch_to(task);
    if (task->frame == NULL) {
        task->frame = task->start(task->context);
    }

    bool finished = task->resume(task->frame) & 1;
    if (finished) {
        // Its body is done, and its frames go; it is done once its children are.
        slab_pop(&task->frames, task->frame);
        task->frame = NULL;
    }

    switch_to(previous);
    if (!finished) {
        after_suspend(task);
        return;
    }

    void *release = task->held;
    task->held = NULL;
    if (release != NULL) {
        executor_release(release);
    }

    spin_lock(&tree_lock);
    if (task->live_children > 0) {
        task->finishing = true;
    } else {
        complete(task);
    }

    spin_unlock(&tree_lock);
}

// A task taken from a queue: on its actor, if it has one, then resumed —
// or, if another holds the actor, left in its inbox to be handed it.
// Whether it ran; it is no longer active either way.
static void run_task(SableTask *task) {
    if (task->actor != NULL && task->actor != MAIN_ACTOR && task->held != task->actor) {
        if (!executor_take_or_wait(executor_of(task->actor), task)) {
            active_done();
            return;
        }

        task->held = task->actor;
    }

    step(task);
    active_done();
}

// ---- Workers -----------------------------------------------------------------

// Parks until there may be work: a task queued, or the earliest sleeper due.
// On the idle list first, then a last look at the queues: a task queued
// after that finds the worker there and notifies it.
static void park(Worker *self) {
    // One parked worker waits in the reactor instead, for sockets and timers.
    if (poll_reactor(true)) {
        return;
    }

    mutex_lock(&self->lock);
    self->notified = false;
    spin_lock(&idle_lock);
    self->next_idle = idle_head;
    idle_head = self;
    self->idle = true;
    __atomic_fetch_add(&parked, 1, __ATOMIC_SEQ_CST);
    spin_unlock(&idle_lock);

    if (__atomic_load_n(&queued, __ATOMIC_SEQ_CST) == 0) {
        int64_t until = __atomic_load_n(&earliest_deadline, __ATOMIC_SEQ_CST);
        while (!self->notified) {
            condition_wait(&self->condition, &self->lock, until == INT64_MAX ? -1 : until);
            if (until != INT64_MAX && sable_time_monotonic() >= until) {
                break;
            }
        }
    }

    // Off the idle list, unless a waker took it off.
    spin_lock(&idle_lock);
    if (self->idle) {
        Worker **at = &idle_head;
        while (*at != self) {
            at = &(*at)->next_idle;
        }

        *at = self->next_idle;
        self->idle = false;
        __atomic_fetch_sub(&parked, 1, __ATOMIC_SEQ_CST);
    }

    spin_unlock(&idle_lock);
    mutex_unlock(&self->lock);
}

static void worker_main(void *argument) {
    Worker *self = argument;
    current_worker = self;
    for (;;) {
        wake_sleepers();
        SableTask *task = find_work(self);
        if (task != NULL) {
            // More is queued: another worker to take it, if none is looking.
            if (__atomic_load_n(&queued, __ATOMIC_RELAXED) > 0) {
                wake_worker();
            }

            // A worker kept busy looks at the reactor now and then, so
            // that sockets ready are heard of while no worker parks.
            if ((++polls & 63) == 0 && __atomic_load_n(&io_waiting, __ATOMIC_RELAXED) > 0) {
                poll_reactor(false);
            }

            run_task(task);
            continue;
        }

        // A short spin first, work often coming right after — by two
        // workers at most: more only contend for what is queued.
        bool found = false;
        if (__atomic_add_fetch(&searching, 1, __ATOMIC_SEQ_CST) <= 2) {
            found = spin_for(&queued, false);
        }

        __atomic_fetch_sub(&searching, 1, __ATOMIC_SEQ_CST);
        if (!found) {
            // What it freed goes back to the slabs, and from there to the system.
            cache_flush(current_thread());
            park(self);
        }
    }
}

// The pool, as the first task is spawned: SABLE_WORKERS threads, by
// default one per core; 0, none — the main thread runs every task, as it
// always does where there are no threads (WASI).
static void pool_start(void) {
    const char *setting = getenv("SABLE_WORKERS");
    int count = setting != NULL && setting[0] != '\0' ? atoi(setting) : processor_count();
    if (count < 0 || !PLATFORM_THREADS) {
        count = 0;
    }

    if (count > 0) {
        workers = calloc((size_t)count, sizeof(Worker));
        if (workers == NULL) {
            sable_trap("out of memory");
        }

        for (int i = 0; i < count; i++) {
            workers[i].index = i;
            mutex_init(&workers[i].lock);
            condition_init(&workers[i].condition);
            for (int band = 0; band < BANDS; band++) {
                workers[i].queues[band].buffer = deque_buffer(64, NULL);
            }
        }
    }

    reactor_start();
    __atomic_store_n(&worker_count, count, __ATOMIC_SEQ_CST);
    for (int i = 0; i < count; i++) {
        if (!thread_start(worker_main, &workers[i])) {
            sable_trap("cannot start the executor's threads");
        }
    }
}

// ---- Spawning and waiting ------------------------------------------------------

// The next task this thread spawns starts cancelled: a cancelled group's
// child, cancelled before any thread can run it.
static _Thread_local bool spawn_cancelled;

void sable_task_spawn_cancelled(void) {
    spawn_cancelled = true;
}

// A new task, queued: 'start(context)' makes its root frame when it first
// runs, 'resume' runs it, and 'drop' destroys it should it never finish. A
// child of the running task unless detached; its priority its parent's (or
// medium, detached) unless given, 0 or more. It runs on no actor, as
// Swift's child tasks do, whatever its parent's. The program holds the handle until it releases
// it. The context — the Task object, with the body in it — is shared: the
// spawner and the task both hold it.
void *sable_task_spawn(void *(*start)(void *), uint8_t (*resume)(void *), void (*drop)(void *), void *context,
    int64_t priority, bool detached) {
    if (__atomic_load_n(&worker_count, __ATOMIC_ACQUIRE) < 0) {
        static SpinLock starting;
        spin_lock(&starting);
        if (worker_count < 0) {
            pool_start();
        }

        spin_unlock(&starting);
    }

    sable_share(context);

    SableTask *task = malloc(sizeof(SableTask));
    if (task == NULL) {
        sable_trap("out of memory");
    }

    memset(task, 0, offsetof(SableTask, first_frames));
    task->start = start;
    task->resume = resume;
    task->drop = drop;
    task->context = context;
    task->sleeper = -1;
    task->inject_band = -1;
    slab_init(&task->frames, task->first_frames, sizeof task->first_frames);

    SableTask *parent = detached ? NULL : current_task;
    task->base_priority = priority >= 0 ? priority : parent != NULL ? parent->base_priority : PRIORITY_MEDIUM;
    task->priority = task->base_priority;
    task->cancelled = spawn_cancelled;
    spawn_cancelled = false;
    spin_lock(&tree_lock);
    if (parent != NULL) {
        task->parent = parent;
        task->cancelled |= __atomic_load_n(&parent->cancelled, __ATOMIC_RELAXED);
        task->next_sibling = parent->children;
        if (parent->children != NULL) {
            parent->children->previous_sibling = task;
        }

        parent->children = task;
        parent->live_children++;
    }

    task->next_alive = alive;
    if (alive != NULL) {
        alive->previous_alive = task;
    }

    alive = task;
    tasks_spawned++;
    if (++tasks_alive > tasks_most) {
        tasks_most = tasks_alive;
    }

    spin_unlock(&tree_lock);
    schedule(task);
    return task;
}

// 'await task.value': whether the running task must wait for it — if so,
// it is among the task's waiters once it has suspended, and queued once
// the task has finished.
bool sable_task_wait(void *handle) {
    SableTask *task = handle;
    if (__atomic_load_n(&task->done, __ATOMIC_ACQUIRE)) {
        return false;
    }

    current_task->pending = PENDING_WAIT;
    current_task->pending_on = task;
    return true;
}

// 'await Task.yield()': whether the running task must let others run — if
// so, it is queued behind every task queued now. With none queued, it goes on.
bool sable_task_yield(void) {
    wake_sleepers();
    if (__atomic_load_n(&queued, __ATOMIC_SEQ_CST) == 0 && !(on_main_thread && __atomic_load_n(&main_head, __ATOMIC_SEQ_CST) != NULL)) {
        return false;
    }

    current_task->pending = PENDING_YIELD;
    return true;
}

// 'await Task.sleep(nanoseconds:)': off the run queue for at least this
// long, while the other tasks run. A cancelled task does not sleep.
bool sable_task_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0 || __atomic_load_n(&current_task->cancelled, __ATOMIC_ACQUIRE)) {
        return sable_task_yield();
    }

    // A sleep past the clock's end is one that only a cancellation ends.
    if (__builtin_add_overflow(sable_time_monotonic(), nanoseconds, &current_task->deadline)) {
        current_task->deadline = INT64_MAX;
    }

    current_task->pending = PENDING_SLEEP;
    return true;
}

// A call into an actor ('actor' its executor), onto the main actor
// (MAIN_ACTOR), or back out to run anywhere (NULL): whether the running
// task must suspend to get there. It does not when it is there already,
// when the actor is free — taken now — or when leaving for anywhere from
// a worker. The actor it held is let go as it leaves it.
bool sable_actor_hop(void *actor) {
    SableTask *task = current_task;
    if (task->actor == actor) {
        return false;
    }

    void *previous = task->held;
    task->actor = actor;
    bool here = false;
    if (actor == NULL) {
        // Off the main thread, unless no other thread runs tasks.
        here = !on_main_thread || worker_count <= 0 || task == &main_task;
    } else if (actor == MAIN_ACTOR) {
        here = on_main_thread;
    } else if (executor_try_take(executor_of(actor), task)) {
        task->held = actor;
        here = true;
    }

    if (!here) {
        // Let go of once it has suspended.
        task->pending = PENDING_HOP;
        return true;
    }

    if (previous != NULL) {
        if (task->held == previous) {
            task->held = NULL;
        }

        executor_release(previous);
    }

    return false;
}

// Where the running task runs: an actor's executor, MAIN_ACTOR, or NULL —
// what a call into an actor hops back to.
void *sable_actor_current(void) {
    return current_task->actor;
}

// 'task.cancel()': marked; its handlers run, the latest first; woken now if
// it sleeps; and so on through its children, which have not finished.
// Under tree_lock: the handlers to run are collected, each context retained,
// and run once the lock is let go.
typedef struct HandlerList {
    SableHandler *items;
    int64_t count, capacity;
} HandlerList;

static void cancel(SableTask *task, HandlerList *handlers) {
    if (task->done || __atomic_load_n(&task->cancelled, __ATOMIC_RELAXED)) {
        return;
    }

    __atomic_store_n(&task->cancelled, true, __ATOMIC_RELEASE);
    for (SableHandler *handler = task->handlers; handler != NULL; handler = handler->next) {
        if (handlers->count == handlers->capacity) {
            handlers->capacity = handlers->capacity < 8 ? 8 : handlers->capacity * 2;
            handlers->items = realloc(handlers->items, (size_t)handlers->capacity * sizeof(SableHandler));
            if (handlers->items == NULL) {
                sable_trap("out of memory");
            }
        }

        sable_retain(handler->context);
        handlers->items[handlers->count++] = *handler;
    }

    mutex_lock(&timer_lock);
    if (task->sleeper >= 0) {
        __atomic_fetch_add(&active, 1, __ATOMIC_SEQ_CST);
        sleepers_remove(task);
        schedule(task);
        active_done();
    }

    mutex_unlock(&timer_lock);
    io_cancel(task, true);
    if (task->parked_in != NULL) {
        unpark(task);
        schedule(task);
    }

    for (SableTask *child = task->children; child != NULL; child = child->next_sibling) {
        cancel(child, handlers);
    }
}

void sable_task_cancel(void *handle) {
    if (handle == NULL) {
        return;
    }

    HandlerList handlers = { 0 };
    spin_lock(&tree_lock);
    cancel(handle, &handlers);
    spin_unlock(&tree_lock);
    for (int64_t i = 0; i < handlers.count; i++) {
        handlers.items[i].run(handlers.items[i].context);
        sable_release(handlers.items[i].context);
    }

    free(handlers.items);
}

// 'Task.isCancelled': whether the running task has been cancelled.
bool sable_task_is_cancelled(void) {
    return __atomic_load_n(&current_task->cancelled, __ATOMIC_ACQUIRE);
}

// 'Task.currentPriority': the running task's, as it was given.
int64_t sable_task_priority(void) {
    return current_task->base_priority;
}

// 'withTaskCancellationHandler': a handler for the running task, run as it
// is cancelled — at once, if it already is — until it is removed. Another
// thread may run it: its context is shared.
void sable_task_handler_push(void (*run)(void *), void *context) {
    SableHandler *handler = malloc(sizeof(SableHandler));
    if (handler == NULL) {
        sable_trap("out of memory");
    }

    sable_share(context);
    handler->run = run;
    handler->context = context;
    spin_lock(&tree_lock);
    handler->next = current_task->handlers;
    current_task->handlers = handler;
    bool cancelled = current_task->cancelled;
    spin_unlock(&tree_lock);
    if (cancelled) {
        run(context);
    }
}

// The latest handler removed, and the object it was given, which it held, released.
void sable_task_handler_pop(void) {
    spin_lock(&tree_lock);
    SableHandler *handler = current_task->handlers;
    current_task->handlers = handler->next;
    spin_unlock(&tree_lock);
    sable_release(handler->context);
    free(handler);
}

void sable_task_release(void *handle) {
    SableTask *task = handle;
    if (task == NULL) {
        return;
    }

    spin_lock(&tree_lock);
    if (task->done) {
        free_task(task);
    } else {
        task->released = true;
    }

    spin_unlock(&tree_lock);
}

// ---- Groups ------------------------------------------------------------------

void *sable_group_new(void) {
    SableGroup *group = calloc(1, sizeof(SableGroup));
    if (group == NULL) {
        sable_trap("out of memory");
    }

    return group;
}

// A task just spawned, a child of the group's: it hears when it finishes.
void sable_group_add(void *group_handle, void *task_handle) {
    SableGroup *group = group_handle;
    SableTask *task = task_handle;
    spin_lock(&tree_lock);
    if (task->done) {
        // Finished already, on another thread: as complete() would have.
        task->next_done = NULL;
        if (group->done_tail != NULL) {
            group->done_tail->next_done = task;
        } else {
            group->done_head = task;
        }

        group->done_tail = task;
    } else {
        task->group = group;
        group->running++;
    }

    spin_unlock(&tree_lock);
}

// 'await group.next()': whether the running task must wait for a child to
// finish — not if one has, or none is running.
bool sable_group_wait(void *group_handle) {
    SableGroup *group = group_handle;
    spin_lock(&tree_lock);
    bool ready = group->done_head != NULL || group->running == 0;
    spin_unlock(&tree_lock);
    if (ready) {
        return false;
    }

    current_task->pending = PENDING_GROUP;
    current_task->pending_on = group;
    return true;
}

// The child that finished first and has not been taken, or NULL.
void *sable_group_take(void *group_handle) {
    SableGroup *group = group_handle;
    spin_lock(&tree_lock);
    SableTask *task = group->done_head;
    if (task != NULL) {
        group->done_head = task->next_done;
        if (group->done_head == NULL) {
            group->done_tail = NULL;
        }

        task->group = NULL;
    }

    spin_unlock(&tree_lock);
    return task;
}

void sable_group_free(void *group_handle) {
    SableGroup *group = group_handle;
    spin_lock(&tree_lock);
    for (SableTask *task = group->done_head; task != NULL; task = task->next_done) {
        task->group = NULL;
    }

    spin_unlock(&tree_lock);
    free(group);
}

// ---- Wait lists ----------------------------------------------------------------

void *sable_waitlist_new(void) {
    SableWaitList *list = calloc(1, sizeof(SableWaitList));
    if (list == NULL) {
        sable_trap("out of memory");
    }

    return list;
}

void sable_waitlist_free(void *pointer) {
    SableWaitList *list = pointer;
    spin_lock(&tree_lock);
    for (SableTask *task = list->head; task != NULL; task = task->next_waiter) {
        task->parked_in = NULL;
    }

    spin_unlock(&tree_lock);
    free(list);
}

// The running task parks in the list until it is woken: always a
// suspension. No list is Task.settle()'s: until no other task can go on.
bool sable_task_park(void *list) {
    current_task->pending = PENDING_PARK;
    current_task->pending_on = list;
    current_task->park_ticket = -1;
    current_task->park_cancellable = false;
    return true;
}

// How many times the list has been woken: taken before a look at what a
// task waits for, so that a wake between that look and the park is not
// missed (WaitList.parkCancellably(since:)). Changed only under tree_lock,
// and read without it: the look that follows, under the state's own lock,
// is ordered after the read, and a wake after the look after it.
int64_t sable_waitlist_ticket(void *pointer) {
    SableWaitList *list = pointer;
    return __atomic_load_n(&list->ticket, __ATOMIC_ACQUIRE);
}

// The running task parks in the list until it is woken, or cancelled —
// unless it is cancelled already, or the list has been woken since the
// ticket (-1: no ticket). Decided as it suspends, under tree_lock, with
// which both a wake and a cancellation are made: neither is missed.
bool sable_task_park_cancellable(void *list, int64_t ticket) {
    if (__atomic_load_n(&current_task->cancelled, __ATOMIC_ACQUIRE)) {
        return false;
    }

    current_task->pending = PENDING_PARK;
    current_task->pending_on = list;
    current_task->park_ticket = ticket;
    current_task->park_cancellable = true;
    return true;
}

// Every task parked in the list, queued — in the order they parked.
void sable_waitlist_wake(void *pointer) {
    SableWaitList *list = pointer;
    spin_lock(&tree_lock);
    SableTask *parked_tasks = list->head, *in_order = NULL;
    list->head = NULL;
    __atomic_store_n(&list->ticket, list->ticket + 1, __ATOMIC_RELEASE);
    while (parked_tasks != NULL) {
        SableTask *after = parked_tasks->next_waiter;
        parked_tasks->parked_in = NULL;
        parked_tasks->next_waiter = in_order;
        in_order = parked_tasks;
        parked_tasks = after;
    }

    while (in_order != NULL) {
        SableTask *task = in_order;
        in_order = task->next_waiter;
        schedule(task);
    }

    spin_unlock(&tree_lock);
}

// ---- The main thread ---------------------------------------------------------

// The main thread runs what is its to run — the main task, tasks on the
// main actor, and, with no pool, every task — until the main task can go
// on ('until'), or, with none, until nothing more will happen.
static void main_loop(SableTask *until) {
    __atomic_store_n(&main_running, 0, __ATOMIC_SEQ_CST);
    wake_settler();
    for (;;) {
        SableTask *task = NULL;
        if (worker_count <= 0) {
            wake_sleepers();
            task = find_work(NULL);
        } else {
            task = main_take();
        }

        if (task != NULL) {
            if (task == until) {
                // Back to the entry, which resumes it, on its actor.
                if (task->actor != NULL && task->actor != MAIN_ACTOR && task->held != task->actor) {
                    if (!executor_take_or_wait(executor_of(task->actor), task)) {
                        active_done();
                        continue;
                    }

                    task->held = task->actor;
                }

                __atomic_store_n(&main_running, 1, __ATOMIC_SEQ_CST);
                active_done();
                return;
            }

            run_task(task);
            continue;
        }

        if (worker_count > 0 && !quiescent() && spin_for(&main_head, true)) {
            continue;
        }

        mutex_lock(&park_lock);
        __atomic_store_n(&main_waiting, 1, __ATOMIC_SEQ_CST);
        bool idle = worker_count <= 0 ? __atomic_load_n(&queued, __ATOMIC_SEQ_CST) == 0 : __atomic_load_n(&main_head, __ATOMIC_SEQ_CST) == NULL;
        if (idle && quiescent() && (until == NULL || __atomic_load_n(&background_waiting, __ATOMIC_SEQ_CST) == 0)) {
            __atomic_store_n(&main_waiting, 0, __ATOMIC_SEQ_CST);
            mutex_unlock(&park_lock);
            if (until != NULL) {
                sable_trap("every task is waiting: the program cannot go on");
            }

            return;
        }

        if (idle && worker_count <= 0 && __atomic_load_n(&reactor_on, __ATOMIC_ACQUIRE)) {
            // With no pool, the main thread is the one that waits in the reactor.
            __atomic_store_n(&main_waiting, 0, __ATOMIC_SEQ_CST);
            mutex_unlock(&park_lock);
            poll_reactor(true);
            continue;
        }

        if (idle) {
            int64_t deadline = __atomic_load_n(&earliest_deadline, __ATOMIC_SEQ_CST);
            condition_wait(&main_condition, &park_lock, worker_count <= 0 && deadline != INT64_MAX ? deadline : -1);
        }

        __atomic_store_n(&main_waiting, 0, __ATOMIC_SEQ_CST);
        mutex_unlock(&park_lock);
    }
}

// An async entry point waits: it has suspended, and is put where it will
// be woken; the main thread runs what is its until the main task can go on.
void sable_task_run_until_main(void) {
    after_suspend(&main_task);
    main_loop(&main_task);
}

// A task that can no longer go on: its frames destroyed, from the root, as
// their drop functions say, and given back. Its waiters are dropped too.
static void drop_task(SableTask *task) {
    io_cancel(task, false);
    spin_lock(&tree_lock);
    unpark(task);
    spin_unlock(&tree_lock);
    if (task->frame != NULL) {
        SableTask *previous = current_task;
        switch_to(task);
        task->drop(task->frame);
        slab_pop(&task->frames, task->frame);
        task->frame = NULL;
        switch_to(previous);
    }

    spin_lock(&tree_lock);
    tasks_dropped++;
    task->done = true;
    task->waiters = NULL;
    leave_parent(task);
    if (task->released) {
        free_task(task);
    }

    spin_unlock(&tree_lock);
}

static void signals_stop(void);

// As the entry point returns: every task still to run, run, the sleepers
// too; then every task still waiting, which nothing will ever wake, dropped.
void sable_tasks_drain(void) {
    if (worker_count < 0) {
        return;
    }

    main_loop(NULL);

    // A signal may have come as the last task stopped: no more are told of,
    // and what the last woke runs, before any task waiting is dropped.
    signals_stop();
    main_loop(NULL);

    spin_lock(&tree_lock);
    int64_t count = 0;
    for (SableTask *task = alive; task != NULL; task = task->next_alive) {
        count += !task->done;
    }

    if (count == 0) {
        spin_unlock(&tree_lock);
        cache_flush(current_thread());
        return;
    }

    // Dropping one may release others, but frees none still to drop.
    SableTask **stuck = malloc((size_t)count * sizeof *stuck);
    if (stuck == NULL) {
        sable_trap("out of memory");
    }

    int64_t n = 0;
    for (SableTask *task = alive; task != NULL; task = task->next_alive) {
        if (!task->done) {
            stuck[n++] = task;
        }
    }

    spin_unlock(&tree_lock);

    // The earliest spawned first: the list has the latest first.
    for (int64_t i = n - 1; i >= 0; i--) {
        drop_task(stuck[i]);
    }

    free(stuck);
}

// ---- Async backtraces ----------------------------------------------------------

// A trap's backtrace (traps.c) shows the thread's frames: the running task's
// async calls among them, each its function's resume function, polled from
// the task's root, then the executor's. What it cannot show is where the
// task came from: the task that spawned it is suspended, its calls on no
// thread's stack. They are in its slab, though — a frame for each call not
// yet finished, from the root up, each starting with its object header,
// whose count says its size (frames.c), then its resume function and its
// state (AsyncLowering's frame head) — so
// they are read from there: the running task's, then its parent's, and so on
// up to the program's own task. Read as the program stops, while the other
// threads run on: the frames of a task another thread resumes meanwhile may
// be seen part-way through a change, which a backtrace can live with.
enum { ASYNC_DEPTH = 64 };

// The frames in a slab, the oldest first, at most 'capacity': how many.
static int slab_frames(const FrameSlab *slab, char *first, void **frames, int capacity) {
    // Its chunks, the latest first, and where each region of frames ends.
    FrameChunk *chunks[64];
    int chunk_count = 0;
    for (FrameChunk *chunk = slab->chunk; chunk != NULL && chunk_count < 64; chunk = chunk->previous) {
        chunks[chunk_count++] = chunk;
    }

    int count = 0;
    for (int region = chunk_count; region >= 0 && count < capacity; region--) {
        char *at = region == chunk_count ? first : chunk_frames(chunks[region]);
        char *end = region == 0 ? slab->top : chunks[region - 1]->previous_top;
        while (at < end && count < capacity) {
            uint32_t size = ((const SableObject *)at)->strong;
            if (size < sizeof(SableObject) + 3 * sizeof(void *)) {
                break;
            }

            frames[count++] = at;
            at += frame_round(size);
        }
    }

    return count;
}

// A frame's function, by its resume function's name less '$resume'.
static void name_async_frame(void *frame, char *text, size_t size) {
    void *resume = *(void **)((char *)frame + sizeof(SableObject));
    name_frame((char *)resume + 1, text, size);
    char *suffix = strstr(text, "$resume");
    if (suffix != NULL) {
        *suffix = '\0';
    }
}

static void print_task_backtrace(void) {
    SableTask *task = current_task;
    int index = 0;
    for (int depth = 0; task != NULL && depth < 16; depth++) {
        void *frames[ASYNC_DEPTH];
        int count = slab_frames(&task->frames, task->first_frames, frames, ASYNC_DEPTH);
        if (count > 0 && index == 0) {
            fprintf(stderr, "async backtrace:\n");
        }

        if (count > 0 && depth > 0) {
            fprintf(stderr, "  in the task that spawned it, waiting:\n");
        }

        for (int i = count - 1; i >= 0; i--) {
            char text[600];
            name_async_frame(frames[i], text, sizeof text);
            int64_t state = *(int64_t *)((char *)frames[i] + sizeof(SableObject) + 2 * sizeof(void *));
            // The running task's innermost frame is where the trap is; the
            // others are at the await they were last suspended at.
            if (strstr(text, "$task.run") != NULL) {
                snprintf(text, sizeof text, "(the task's body)");
            }

            if ((depth == 0 && i == count - 1) || state <= 0) {
                fprintf(stderr, "  %2d  %s\n", index++, text);
            } else {
                fprintf(stderr, "  %2d  %s, at await %lld\n", index++, text, (long long)state);
            }
        }

        task = task->parent;
    }
}

static void report_tasks(void) {
    fprintf(stderr, "sable: tasks %lld, %lld at most at once, %lld dropped, %lld bytes each, %d workers, %lld actor holders escalated\n",
        (long long)tasks_spawned, (long long)tasks_most, (long long)tasks_dropped, (long long)sizeof(SableTask),
        worker_count < 0 ? 0 : worker_count, (long long)actor_escalations);
}
