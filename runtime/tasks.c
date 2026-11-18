// The Sable runtime: tasks — the cooperative scheduler.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Tasks -----------------------------------------------------------------

// Concurrency (docs/02 §2.7): tasks scheduled cooperatively. Each runs on a
// thread, but one lock is held by whichever task is running, so exactly one
// runs at a time — the order is the scheduler's, the counts need no
// atomics, and @MainActor code, checked at compile time, needs nothing more.
// A task gives the lock up only where it waits: on a task not yet finished,
// at Task.yield(), or asleep in Task.sleep. Spawning queues a task; it runs
// when the running one next waits. The program's entry is a task too, and
// the program ends once every task has.
//
// A task is given a thread only when it first runs, and a thread whose task
// has finished runs the next task that has not started yet itself, or waits
// to be given one: threads are kept for tasks that are waiting part-way
// through, not for every task spawned. Ten thousand tasks that each run to
// the end take one thread between them.
//
// A sleeping task is off the run queue until its time comes. When nothing
// is queued, the thread switching away waits for the earliest sleeper: two
// tasks sleeping a second take a second, not two.

typedef struct SableTask {
    void (*body)(void *context);
    void *context;
    TaskCondition wake;
    bool started;  // given a thread: it has begun, and waits on 'wake' to go on
    bool done;
    bool released; // the program no longer holds it
    bool cancelled; // asked to stop: it asks Task.isCancelled(), and sleeps end at once
    bool sleeping;  // among the sleepers
    int64_t deadline;           // asleep until this monotonic time
    struct SableTask *next;     // in the run queue, or among the sleepers
    struct SableTask *waiters;  // tasks waiting for this one, through 'next_waiter'
    struct SableTask *next_waiter;
} SableTask;

// A thread that runs tasks: the one it runs, or none while it waits for one.
typedef struct SableWorker {
    TaskCondition wake;
    SableTask *task;
    struct SableWorker *next; // among the idle
} SableWorker;

static SableTask main_task;
static SableTask *current_task;
static SableTask *queue_head, *queue_tail;
static SableTask *sleepers; // by deadline, the earliest first
static SableWorker *idle_workers;

__attribute__((constructor)) static void sable_tasks_start(void) {
    lock_tasks();
    condition_init(&main_task.wake);
    main_task.started = true;
    current_task = &main_task;
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

// Every sleeper whose time has come, queued in the order of their deadlines.
static void wake_sleepers(void) {
    if (sleepers == NULL) {
        return;
    }

    int64_t now = sable_time_monotonic();
    while (sleepers != NULL && sleepers->deadline <= now) {
        SableTask *task = sleepers;
        sleepers = task->next;
        task->sleeping = false;
        enqueue(task);
    }
}

// Nothing is queued: waits for the earliest sleeper. The lock stays held,
// as no task could run meanwhile anyway.
static void wait_for_sleeper(void) {
    sable_time_sleep(sleepers->deadline - sable_time_monotonic());

    wake_sleepers();
}

// The task to run next: the first queued, once the sleepers due are.
static SableTask *next_task(void) {
    wake_sleepers();
    while (queue_head == NULL && sleepers != NULL) {
        wait_for_sleeper();
    }

    SableTask *next = dequeue();
    if (next == NULL) {
        sable_trap("every task is waiting: the program cannot go on");
    }

    return next;
}

static void free_task(SableTask *task) {
    condition_destroy(&task->wake);
    free(task);
}

static void worker_thread(void *argument);

// Makes 'next' the running task: its thread woken, or, for one that has not
// begun, an idle thread given it, or a new one started for it.
static void resume(SableTask *next) {
    current_task = next;
    if (next->started) {
        condition_signal(&next->wake);
        return;
    }

    next->started = true;
    SableWorker *worker = idle_workers;
    if (worker != NULL) {
        idle_workers = worker->next;
        worker->task = next;
        condition_signal(&worker->wake);
        return;
    }

    worker = calloc(1, sizeof(SableWorker));
    if (worker == NULL) {
        sable_trap("out of memory");
    }

    condition_init(&worker->wake);
    worker->task = next;
    if (!start_thread(worker_thread, worker)) {
        sable_trap("cannot start a task");
    }
}

// Hands the lock to the next task, and waits until a task hands it back.
static void switch_away(void) {
    SableTask *self = current_task;
    SableTask *next = next_task();
    if (next == self) {
        return;
    }

    resume(next);
    while (current_task != self) {
        condition_wait(&self->wake);
    }
}

// Runs tasks: the one it was started for, then, as each finishes, the next
// if it has not begun; otherwise it hands over and waits to be given one.
static void worker_thread(void *argument) {
    SableWorker *self = argument;
    watch_stack();
    lock_tasks();
    SableTask *task = self->task;
    for (;;) {
        while (current_task != task) {
            condition_wait(&task->wake);
        }

        task->body(task->context);

        // Done: whoever waits for it may go on.
        task->done = true;
        for (SableTask *waiter = task->waiters; waiter != NULL;) {
            SableTask *after = waiter->next_waiter;
            enqueue(waiter);
            waiter = after;
        }

        task->waiters = NULL;
        SableTask *next = next_task();
        if (task->released) {
            free_task(task);
        }

        if (!next->started) {
            next->started = true;
            current_task = task = next;
            continue;
        }

        resume(next);
        self->task = NULL;
        self->next = idle_workers;
        idle_workers = self;
        while (self->task == NULL) {
            condition_wait(&self->wake);
        }

        task = self->task;
    }
}

// A new task, queued to run 'body(context)'; the program holds the handle until it releases it.
void *sable_task_spawn(void (*body)(void *), void *context) {
    SableTask *task = calloc(1, sizeof(SableTask));
    if (task == NULL) {
        sable_trap("out of memory");
    }

    task->body = body;
    task->context = context;
    condition_init(&task->wake);
    enqueue(task);
    return task;
}

// 'await task.value': waits until it has finished.
void sable_task_wait(void *handle) {
    SableTask *task = handle;
    while (!task->done) {
        SableTask *self = current_task;
        self->next_waiter = task->waiters;
        task->waiters = self;
        switch_away();
    }
}

// 'await Task.yield()': lets every task queued run before this one goes on.
void sable_task_yield(void) {
    wake_sleepers();
    if (queue_head == NULL) {
        return;
    }

    enqueue(current_task);
    switch_away();
}

// 'await Task.sleep(nanoseconds:)': off the run queue for at least this
// long, while the other tasks run. A cancelled task does not sleep.
void sable_task_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0 || current_task->cancelled) {
        sable_task_yield();
        return;
    }

    SableTask *self = current_task;
    self->deadline = sable_time_monotonic() + nanoseconds;
    SableTask **at = &sleepers;
    while (*at != NULL && (*at)->deadline <= self->deadline) {
        at = &(*at)->next;
    }

    self->next = *at;
    *at = self;
    self->sleeping = true;
    switch_away();
}

// 'task.cancel()': marked, and woken now if it sleeps — queued as a sleeper
// whose time has come would be.
void sable_task_cancel(void *handle) {
    SableTask *task = handle;
    if (task == NULL || task->done || task->cancelled) {
        return;
    }

    task->cancelled = true;
    if (task->sleeping) {
        SableTask **at = &sleepers;
        while (*at != task) {
            at = &(*at)->next;
        }

        *at = task->next;
        task->sleeping = false;
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

// As the entry point returns: every task still to run, run, the sleepers too.
void sable_tasks_drain(void) {
    for (;;) {
        wake_sleepers();
        if (queue_head == NULL && sleepers == NULL) {
            return;
        }

        if (queue_head == NULL) {
            wait_for_sleeper();
            continue;
        }

        enqueue(current_task);
        switch_away();
    }
}
