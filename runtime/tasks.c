// The Sable runtime: tasks — the executor.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Tasks -----------------------------------------------------------------

// Concurrency (docs/02 §2.7): tasks are stackless (#276). The compiler makes
// each async function a coroutine (AsyncLowering): a frame, on its task's
// slab (frames.c), and a resume function that runs it until it finishes or
// must wait. A task is the root of such a chain: its body's start function,
// which makes the root frame, and its resume function, which the executor
// calls each time the task can go on. A task that must wait says so where
// it waits — on a task not yet finished, at Task.yield(), or asleep — having
// been put where it will be woken: among the finished task's waiters, at
// the back of the run queue, or among the sleepers.
//
// One executor, on the program's one thread, runs every task: it resumes
// the first queued, until it finishes or waits, then the next. Only the
// places a task waits switch tasks, so the interleaving is deterministic,
// reference counts need no atomics, and nothing needs a lock. A task holds
// no thread and no stack while it waits: its frames, and the task itself
// with its slab's first chunk, are all there is of it.
//
// The program's entry is a task of its own, the main task: what runs when
// no other does. An async entry point's frames are on its slab, and when it
// must wait the executor runs the others until it can go on
// (sable_task_run_until_main). The program ends once every task has
// (sable_tasks_drain).
//
// The sleepers are a heap by deadline, ties in the order they began to
// sleep: when nothing is queued, the executor waits for the earliest, so
// two tasks sleeping a second take a second, and a hundred thousand
// sleepers cost a heap entry each.

typedef struct SableTask {
    void *(*start)(void *context);   // makes the root frame
    uint8_t (*resume)(void *frame);  // runs it; its low bit: finished
    void *context;
    void *frame;                     // the root frame, once begun
    struct SableTask *next;          // in the run queue
    struct SableTask *waiters;       // tasks waiting for this one, through 'next_waiter'
    struct SableTask *next_waiter;
    int64_t deadline;                // asleep until this monotonic time
    int64_t sleeper;                 // its place among the sleepers, or -1
    uint64_t sequence;               // when it began to sleep, for ties
    bool done;
    bool released;                   // the program no longer holds it
    bool cancelled;                  // asked to stop: it asks Task.isCancelled(), and sleeps end at once
    FrameSlab frames;
    _Alignas(FRAME_ALIGN) char first_frames[FRAME_FIRST_CHUNK];
} SableTask;

static SableTask main_task;
static SableTask *current_task = &main_task;
static SableTask *queue_head, *queue_tail;

static SableTask **sleepers;
static int64_t sleeper_count, sleeper_capacity;
static uint64_t sleep_sequence;

// What the executor has done, for SABLE_STATS.
static int64_t tasks_spawned, tasks_alive, tasks_most;

static void tasks_start(void) {
    slab_init(&main_task.frames, main_task.first_frames, sizeof main_task.first_frames);
    main_task.sleeper = -1;
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
    slab_destroy(&task->frames);
    free(task);
    tasks_alive--;
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
        // Done: its frames go, and whoever waits for it may go on.
        slab_pop(&task->frames, task->frame);
        task->frame = NULL;
        task->done = true;
        for (SableTask *waiter = task->waiters; waiter != NULL;) {
            SableTask *after = waiter->next_waiter;
            enqueue(waiter);
            waiter = after;
        }

        task->waiters = NULL;
    }

    switch_to(previous);
    if (finished && task->released) {
        free_task(task);
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
// runs, and 'resume' runs it. The program holds the handle until it releases it.
void *sable_task_spawn(void *(*start)(void *), uint8_t (*resume)(void *), void *context) {
    SableTask *task = malloc(sizeof(SableTask));
    if (task == NULL) {
        sable_trap("out of memory");
    }

    memset(task, 0, offsetof(SableTask, first_frames));
    task->start = start;
    task->resume = resume;
    task->context = context;
    task->sleeper = -1;
    slab_init(&task->frames, task->first_frames, sizeof task->first_frames);
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

// 'task.cancel()': marked, and woken now if it sleeps — queued as a sleeper
// whose time has come would be.
void sable_task_cancel(void *handle) {
    SableTask *task = handle;
    if (task == NULL || task->done || task->cancelled) {
        return;
    }

    task->cancelled = true;
    if (task->sleeper >= 0) {
        sleepers_remove(task);
        enqueue(task);
    }
}

// 'Task.isCancelled()': whether the running task has been cancelled.
bool sable_task_is_cancelled(void) {
    return current_task->cancelled;
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

// An async entry point waits: the other tasks run until it can go on.
void sable_task_run_until_main(void) {
    run_tasks(&main_task);
}

// As the entry point returns: every task still to run, run, the sleepers too.
void sable_tasks_drain(void) {
    run_tasks(NULL);
}

static void report_tasks(void) {
    fprintf(stderr, "sable: tasks %lld, %lld at most at once, %lld bytes each\n",
        (long long)tasks_spawned, (long long)tasks_most, (long long)sizeof(SableTask));
}
