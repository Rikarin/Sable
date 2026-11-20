// The Sable runtime: tasks — the executor.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Tasks -----------------------------------------------------------------

// Concurrency (docs/02 §2.7): tasks are stackless (#276). The compiler makes
// each async function a coroutine (AsyncLowering): a frame, on its task's
// slab (frames.c), a resume function that runs it until it finishes or must
// wait, and a drop function that destroys what it holds part-way. A task is
// the root of such a chain: its body's start function, which makes the root
// frame, and its resume and drop functions. A task that must wait says so
// where it waits — on a task not yet finished, on a group's next child, at
// Task.yield(), or asleep — having been put where it will be woken: among
// the finished task's waiters, as its group's waiter, at the back of the run
// queue, or among the sleepers.
//
// One executor, on the program's one thread, runs every task: it resumes
// the first queued, until it finishes or waits, then the next. Only the
// places a task waits switch tasks, so the interleaving is deterministic,
// reference counts need no atomics, and nothing needs a lock. A task holds
// no thread and no stack while it waits: its frames, and the task itself
// with its slab's first chunk, are all there is of it.
//
// Tasks are structured (SYNTAX.md §19). A task spawned from another is its
// child: it starts with its parent's priority, it is cancelled when its
// parent is, and its parent, once its own body has finished, finishes only
// when every child has. A detached task has no parent. Cancelling is
// cooperative: a cancelled task is marked, the cancellation handlers it has
// installed run there and then, a sleep it is in ends at once, and so on
// down through its children; the task itself goes on until it asks.
//
// The program's entry is a task of its own, the main task: what runs when
// no other does. An async entry point's frames are on its slab, and when it
// must wait the executor runs the others until it can go on
// (sable_task_run_until_main). The program ends once every task has
// finished or can no longer go on (sable_tasks_drain): a task still waiting
// then — on something no task will ever give — is dropped, its frames
// destroyed by their drop functions, so what they held is given back.
//
// The sleepers are a heap by deadline, ties in the order they began to
// sleep: when nothing is queued, the executor waits for the earliest, so
// two tasks sleeping a second take a second, and a hundred thousand
// sleepers cost a heap entry each.

typedef struct SableHandler {
    void (*run)(void *context);   // a cancellation handler: run as the task is cancelled
    void *context;
    struct SableHandler *next;    // the one installed before it
} SableHandler;

typedef struct SableGroup SableGroup;

typedef struct SableTask {
    void *(*start)(void *context);   // makes the root frame
    uint8_t (*resume)(void *frame);  // runs it; its low bit: finished
    void (*drop)(void *frame);       // destroys what it holds, part-way
    void *context;
    void *frame;                     // the root frame, once begun
    struct SableTask *next;          // in the run queue
    struct SableTask *waiters;       // tasks waiting for this one, through 'next_waiter'
    struct SableTask *next_waiter;
    struct SableTask *parent;        // NULL for a detached task
    struct SableTask *children;      // children not yet finished, through 'next_sibling'
    struct SableTask *next_sibling, *previous_sibling;
    struct SableTask *next_alive, *previous_alive; // every task not yet freed
    SableGroup *group;               // the group it was spawned in, until taken from it
    struct SableTask *next_done;     // among its group's finished children
    SableHandler *handlers;          // its cancellation handlers, the latest first
    int64_t deadline;                // asleep until this monotonic time
    int64_t sleeper;                 // its place among the sleepers, or -1
    uint64_t sequence;               // when it began to sleep, for ties
    int64_t priority;
    int64_t live_children;           // children not yet finished
    bool done;
    bool finishing;                  // its body has finished, its children not yet
    bool released;                   // the program no longer holds it
    bool cancelled;                  // asked to stop: it asks Task.isCancelled, and sleeps end at once
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

// TaskPriority.medium: the main task's, and a detached task's unless it says.
enum { PRIORITY_MEDIUM = 21 };

static SableTask main_task;
static SableTask *current_task = &main_task;
static SableTask *queue_head, *queue_tail;
static SableTask *alive;

static SableTask **sleepers;
static int64_t sleeper_count, sleeper_capacity;
static uint64_t sleep_sequence;

// What the executor has done, for SABLE_STATS.
static int64_t tasks_spawned, tasks_alive, tasks_most, tasks_dropped;

static void tasks_start(void) {
    slab_init(&main_task.frames, main_task.first_frames, sizeof main_task.first_frames);
    main_task.sleeper = -1;
    main_task.priority = PRIORITY_MEDIUM;
    current_frames = &main_task.frames;
}

static void switch_to(SableTask *task) {
    current_task = task;
    current_frames = &task->frames;
}

static void enqueue(SableTask *task) {
    task->next = NULL;
    if (queue_tail != NULL) {
        queue_tail->next = task;
    } else {
        queue_head = task;
    }

    queue_tail = task;
}

static SableTask *dequeue(void) {
    SableTask *task = queue_head;
    if (task != NULL) {
        queue_head = task->next;
        if (queue_head == NULL) {
            queue_tail = NULL;
        }
    }

    return task;
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
    sleeper_place(sleeper_count++, task);
    sleepers_up(sleeper_count - 1);
}

static void sleepers_remove(SableTask *task) {
    int64_t index = task->sleeper;
    task->sleeper = -1;
    SableTask *last = sleepers[--sleeper_count];
    if (index == sleeper_count) {
        return;
    }

    sleeper_place(index, last);
    sleepers_up(index);
    sleepers_down(last->sleeper);
}

// Every sleeper whose time has come, queued in the order of their deadlines.
static void wake_sleepers(void) {
    if (sleeper_count == 0) {
        return;
    }

    int64_t now = sable_time_monotonic();
    while (sleeper_count > 0 && sleepers[0]->deadline <= now) {
        SableTask *task = sleepers[0];
        sleepers_remove(task);
        enqueue(task);
    }

    if (sleeper_count == 0) {
        free(sleepers);
        sleepers = NULL;
        sleeper_capacity = 0;
    }
}

// Nothing is queued: waits for the earliest sleeper.
static void wait_for_sleeper(void) {
    sable_time_sleep(sleepers[0]->deadline - sable_time_monotonic());
    wake_sleepers();
}

// ---- The executor ------------------------------------------------------------

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

// Off its parent's children: it has finished, or is dropped.
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
// hears of it, and a parent waiting only for it finishes as well.
static void complete(SableTask *task) {
    task->done = true;
    task->finishing = false;
    for (SableTask *waiter = task->waiters; waiter != NULL;) {
        SableTask *after = waiter->next_waiter;
        enqueue(waiter);
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
            enqueue(group->waiter);
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
    if (finished) {
        if (task->live_children > 0) {
            task->finishing = true;
        } else {
            complete(task);
        }
    }
}

// Resumes each task queued, in turn, until none can go on — or, given a
// task, until that one can.
static void run_tasks(SableTask *until) {
    for (;;) {
        wake_sleepers();
        SableTask *task = dequeue();
        if (task == NULL) {
            if (sleeper_count > 0) {
                wait_for_sleeper();
                continue;
            }

            if (until == NULL) {
                return;
            }

            sable_trap("every task is waiting: the program cannot go on");
        }

        if (task == until) {
            return;
        }

        step(task);
    }
}

// A new task, queued: 'start(context)' makes its root frame when it first
// runs, 'resume' runs it, and 'drop' destroys it should it never finish. A
// child of the running task unless detached; its priority its parent's (or
// medium, detached) unless given, 0 or more. The program holds the handle
// until it releases it.
void *sable_task_spawn(void *(*start)(void *), uint8_t (*resume)(void *), void (*drop)(void *), void *context,
    int64_t priority, bool detached) {
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
    slab_init(&task->frames, task->first_frames, sizeof task->first_frames);

    SableTask *parent = detached ? NULL : current_task;
    task->priority = priority >= 0 ? priority : parent != NULL ? parent->priority : PRIORITY_MEDIUM;
    if (parent != NULL) {
        task->parent = parent;
        task->cancelled = parent->cancelled;
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
    enqueue(task);
    tasks_spawned++;
    if (++tasks_alive > tasks_most) {
        tasks_most = tasks_alive;
    }

    return task;
}

// 'await task.value': whether the running task must wait for it — if so,
// it is among the task's waiters, and queued once the task has finished.
bool sable_task_wait(void *handle) {
    SableTask *task = handle;
    if (task->done) {
        return false;
    }

    current_task->next_waiter = task->waiters;
    task->waiters = current_task;
    return true;
}

// 'await Task.yield()': whether the running task must let others run — if
// so, it is queued behind every task queued now. With none queued, it goes on.
bool sable_task_yield(void) {
    wake_sleepers();
    if (queue_head == NULL) {
        return false;
    }

    enqueue(current_task);
    return true;
}

// 'await Task.sleep(nanoseconds:)': off the run queue for at least this
// long, while the other tasks run. A cancelled task does not sleep.
bool sable_task_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0 || current_task->cancelled) {
        return sable_task_yield();
    }

    current_task->deadline = sable_time_monotonic() + nanoseconds;
    sleepers_add(current_task);
    return true;
}

// 'task.cancel()': marked; its handlers run, the latest first; woken now if
// it sleeps — queued as a sleeper whose time has come would be; and so on
// through its children, which have not finished.
static void cancel(SableTask *task) {
    if (task->done || task->cancelled) {
        return;
    }

    task->cancelled = true;
    for (SableHandler *handler = task->handlers; handler != NULL; handler = handler->next) {
        handler->run(handler->context);
    }

    if (task->sleeper >= 0) {
        sleepers_remove(task);
        enqueue(task);
    }

    for (SableTask *child = task->children; child != NULL; child = child->next_sibling) {
        cancel(child);
    }
}

void sable_task_cancel(void *handle) {
    if (handle != NULL) {
        cancel(handle);
    }
}

// 'Task.isCancelled': whether the running task has been cancelled.
bool sable_task_is_cancelled(void) {
    return current_task->cancelled;
}

// 'Task.currentPriority': the running task's.
int64_t sable_task_priority(void) {
    return current_task->priority;
}

// 'withTaskCancellationHandler': a handler for the running task, run as it
// is cancelled — at once, if it already is — until it is removed.
void sable_task_handler_push(void (*run)(void *), void *context) {
    SableHandler *handler = malloc(sizeof(SableHandler));
    if (handler == NULL) {
        sable_trap("out of memory");
    }

    handler->run = run;
    handler->context = context;
    handler->next = current_task->handlers;
    current_task->handlers = handler;
    if (current_task->cancelled) {
        run(context);
    }
}

// The latest handler removed, and the object it was given, which it held, released.
void sable_task_handler_pop(void) {
    SableHandler *handler = current_task->handlers;
    current_task->handlers = handler->next;
    sable_release(handler->context);
    free(handler);
}

void sable_task_release(void *handle) {
    SableTask *task = handle;
    if (task == NULL) {
        return;
    }

    if (task->done) {
        free_task(task);
    } else {
        task->released = true;
    }
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
    task->group = group;
    group->running++;
}

// 'await group.next()': whether the running task must wait for a child to
// finish — not if one has, or none is running.
bool sable_group_wait(void *group_handle) {
    SableGroup *group = group_handle;
    if (group->done_head != NULL || group->running == 0) {
        return false;
    }

    group->waiter = current_task;
    return true;
}

// The child that finished first and has not been taken, or NULL.
void *sable_group_take(void *group_handle) {
    SableGroup *group = group_handle;
    SableTask *task = group->done_head;
    if (task != NULL) {
        group->done_head = task->next_done;
        if (group->done_head == NULL) {
            group->done_tail = NULL;
        }

        task->group = NULL;
    }

    return task;
}

void sable_group_free(void *group_handle) {
    SableGroup *group = group_handle;
    for (SableTask *task = group->done_head; task != NULL; task = task->next_done) {
        task->group = NULL;
    }

    free(group);
}

// ---- The program's end -------------------------------------------------------

// An async entry point waits: the other tasks run until it can go on.
void sable_task_run_until_main(void) {
    run_tasks(&main_task);
}

// A task that can no longer go on: its frames destroyed, from the root, as
// their drop functions say, and given back. Its waiters are dropped too.
static void drop_task(SableTask *task) {
    if (task->frame != NULL) {
        SableTask *previous = current_task;
        switch_to(task);
        task->drop(task->frame);
        slab_pop(&task->frames, task->frame);
        task->frame = NULL;
        switch_to(previous);
    }

    tasks_dropped++;
    task->done = true;
    task->waiters = NULL;
    leave_parent(task);
    if (task->released) {
        free_task(task);
    }
}

// As the entry point returns: every task still to run, run, the sleepers
// too; then every task still waiting, which nothing will ever wake, dropped.
void sable_tasks_drain(void) {
    run_tasks(NULL);

    int64_t count = 0;
    for (SableTask *task = alive; task != NULL; task = task->next_alive) {
        count += !task->done;
    }

    if (count == 0) {
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

    // The earliest spawned first: the list has the latest first.
    for (int64_t i = n - 1; i >= 0; i--) {
        drop_task(stuck[i]);
    }

    free(stuck);
}

static void report_tasks(void) {
    fprintf(stderr, "sable: tasks %lld, %lld at most at once, %lld dropped, %lld bytes each\n",
        (long long)tasks_spawned, (long long)tasks_most, (long long)tasks_dropped, (long long)sizeof(SableTask));
}
