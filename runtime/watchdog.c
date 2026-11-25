// The Sable runtime: the watchdog — a time limit on the program, kept by a
// thread of its own. Part of sable_runtime.c, which includes every part in
// order (see there).

// ---- The watchdog ------------------------------------------------------------

// What a test's time limit is (Sable.Testing): past it, the message and where
// every thread and task is are written to standard error, and the task that
// asked is cancelled; the program is given 'grace' longer to end by itself,
// its teardown done, and is then ended with status 124, as timeout(1) ends
// what it runs. A thread that computes,
// waits for a lock or is in a call that blocks is past its limit like any
// other, as none of them keeps this thread from running.
//
// Under a debugger time is spent at breakpoints: while one is attached the
// limit does not hold.

enum {
    WATCHDOG_STATUS = 124,
    // Tasks shown, at most.
    WATCHDOG_TASKS = 32,
};

static PlatformMutex watchdog_mutex;
static PlatformCondition watchdog_changed;
static bool watchdog_made;
static int64_t watchdog_until; // 0 while there is no limit
static int64_t watchdog_grace;
static char *watchdog_message;
static bool watchdog_told;
static SableTask *watchdog_task; // whose limit it is
static bool watchdog_cancelling;

// Each task that is suspended, by the calls it is in. The program runs on
// meanwhile, as it does for a trap's backtrace.
static void print_waiting_tasks(void) {
    int shown = 0;
    // The program's own is in no list.
    void *own[ASYNC_DEPTH];
    int calls = slab_frames(&main_task.frames, main_task.first_frames, own, ASYNC_DEPTH);
    if (calls > 0) {
        fprintf(stderr, "task %d:\n", ++shown);
        print_async_calls(own, calls, 0, false);
    }

    for (int i = 0; i < ALIVE_LISTS && shown <= WATCHDOG_TASKS; i++) {
        AliveList *list = &alive_lists[i];
        spin_lock(&list->lock);
        for (SableTask *task = list->head; task != NULL && shown <= WATCHDOG_TASKS; task = task->next_alive) {
            void *frames[ASYNC_DEPTH];
            if (__atomic_load_n(&task->done, __ATOMIC_ACQUIRE)) {
                continue;
            }

            int count = slab_frames(&task->frames, task->first_frames, frames, ASYNC_DEPTH);
            if (count == 0) {
                continue;
            }

            if (++shown > WATCHDOG_TASKS) {
                fprintf(stderr, "more tasks are not shown\n");
            } else {
                fprintf(stderr, "task %d:\n", shown);
                print_async_calls(frames, count, 0, false);
            }
        }

        spin_unlock(&list->lock);
    }
}

// A thread's frames, the innermost first, if it is in the program's code: the
// pool's own threads, waiting for work, are counted and not shown.
static int watchdog_shown, watchdog_idle;

static void print_thread_frames(void **frames, int count) {
    static char lines[BACKTRACE_DEPTH][600];
    bool program = false;
    for (int i = 0; i < count; i++) {
        name_frame(frames[i], lines[i], sizeof lines[i]);
        program = program || strncmp(lines[i], "sable.", 6) == 0;
    }

    if (!program) {
        watchdog_idle++;
        return;
    }

    fprintf(stderr, "thread %d:\n", ++watchdog_shown);
    for (int i = 0; i < count; i++) {
        fprintf(stderr, "  %2d  %s\n", i, lines[i]);
    }
}

static void print_stacks(void) {
    watchdog_shown = watchdog_idle = 0;
    each_thread_frames(print_thread_frames);
    if (watchdog_idle > 0) {
        fprintf(stderr, "%d more thread%s in none of the program's code\n", watchdog_idle, watchdog_idle == 1 ? " is" : "s are");
    }

    print_waiting_tasks();
    fflush(stderr);
}

static void watchdog_main(void *argument) {
    (void)argument;
    mutex_lock(&watchdog_mutex);
    for (;;) {
        if (watchdog_until == 0) {
            condition_wait(&watchdog_changed, &watchdog_mutex, -1);
            continue;
        }

        int64_t now = sable_time_monotonic();
        if (now < watchdog_until) {
            condition_wait(&watchdog_changed, &watchdog_mutex, watchdog_until);
            continue;
        }

        if (sable_debugger_attached()) {
            // Looked at again, in case it leaves.
            watchdog_until = now + 1000000000;
            continue;
        }

        if (watchdog_told) {
            // Not the streams' own flush, which would wait for a thread that
            // is writing.
            coverage_write();
            _Exit(WATCHDOG_STATUS);
        }

        watchdog_told = true;
        watchdog_until = now + watchdog_grace;
        SableTask *task = watchdog_task;
        watchdog_cancelling = true;
        fputs(watchdog_message, stderr);
        fflush(stderr);
        mutex_unlock(&watchdog_mutex);
        print_stacks();
        sable_task_cancel(task);
        mutex_lock(&watchdog_mutex);
        watchdog_cancelling = false;
        condition_broadcast(&watchdog_changed);
        if (watchdog_until != 0) {
            watchdog_until = sable_time_monotonic() + watchdog_grace;
        }
    }
}

// The program is to end within so many nanoseconds, and 'grace' more once the
// message has been written: 0 for no limit, the one before forgotten, and the
// running task, whose it was, cancelled no more. The task lives until then.
// Where there are no threads there is no limit.
void sable_watchdog(int64_t nanoseconds, int64_t grace, const SableString *message) {
    static SpinLock making;
    spin_lock(&making);
    if (!watchdog_made && nanoseconds > 0) {
        mutex_init(&watchdog_mutex);
        condition_init(&watchdog_changed);
        watchdog_made = thread_start(watchdog_main, NULL);
    }

    bool usable = watchdog_made;
    spin_unlock(&making);
    if (!usable) {
        return;
    }

    char *copy = malloc((size_t)message->length + 1);
    if (copy == NULL) {
        sable_trap("out of memory");
    }

    memcpy(copy, message->bytes, (size_t)message->length);
    copy[message->length] = '\0';
    mutex_lock(&watchdog_mutex);
    while (watchdog_cancelling) {
        condition_wait(&watchdog_changed, &watchdog_mutex, -1);
    }

    if (watchdog_told && watchdog_task == current_task) {
        __atomic_store_n(&current_task->cancelled, false, __ATOMIC_SEQ_CST);
    }

    watchdog_task = current_task;
    watchdog_until = nanoseconds <= 0 ? 0 : sable_time_monotonic() + nanoseconds;
    watchdog_grace = grace;
    watchdog_told = false;
    free(watchdog_message);
    watchdog_message = copy;
    condition_broadcast(&watchdog_changed);
    mutex_unlock(&watchdog_mutex);
}
