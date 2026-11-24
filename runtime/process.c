// The Sable runtime: processes as Sable.Process sees them.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Starting a program -----------------------------------------------------

// Why this thread's last start failed (platform.h's PROCESS_ERROR_).
static _Thread_local ProcessError process_last_failure;

int64_t sable_process_error(void) {
    return process_last_failure.kind;
}

int64_t sable_process_error_code(void) {
    return process_last_failure.code;
}

// The strings as a NULL-ended list of C strings, the first of them 'first'
// when there is one.
static char **c_strings(const SableString *first, const SableArray *rest) {
    int64_t count = rest == NULL ? 0 : rest->count;
    char **all = malloc((size_t)(count + 2) * sizeof(char *));
    if (all == NULL) {
        sable_trap("out of memory");
    }

    int64_t n = 0;
    if (first != NULL) {
        all[n++] = c_string(first);
    }

    for (int64_t i = 0; i < count; i++) {
        all[n++] = c_string(((SableString **)rest->elements)[i]);
    }

    all[n] = NULL;
    return all;
}

static void free_strings(char **all) {
    for (char **at = all; *at != NULL; at++) {
        free(*at);
    }

    free(all);
}

// ---- A program run to its end -------------------------------------------------

static _Thread_local int64_t process_last_status;

// Plain memory, not a Sable string: the runtime keeps it past the program's
// own objects, and the leak check counts only those.
static _Thread_local char *process_last_errors;
static _Thread_local size_t process_last_errors_length;
static _Thread_local int64_t process_last_signal;

int64_t sable_process_status(void) {
    return process_last_status;
}

// The signal that ended the last program run, or 0.
int64_t sable_process_status_signal(void) {
    return process_last_signal;
}

SableString *sable_process_error_output(void) {
    return make_text(process_last_errors == NULL ? "" : process_last_errors, (int64_t)process_last_errors_length);
}

// Runs the program, found on PATH, to its end: the input written to it and
// its output and errors read as they come, all three at once, so that
// neither side waits on a full pipe. Gives what it wrote to standard
// output; how it ended and its errors are kept for the calls above.
SableString *sable_process_run(const SableString *program, const SableArray *arguments, const SableString *input) {
    char **argv = c_strings(program, arguments);
    ProcessSpec spec = { .argv = argv, .stdio = { STDIO_PIPED, STDIO_PIPED, STDIO_PIPED } };
    PlatformProcess child;
    PlatformPipe pipes[3];
    bool started = process_start(&spec, &child, pipes, &process_last_failure);
    free_strings(argv);
    if (!started) {
        io_last_failed = true;
        return make_string("", 0);
    }

    Buffer gathered[3] = { { 0 } };
    int64_t written = 0;
    char *chunk = malloc(65536);
    if (chunk == NULL) {
        sable_trap("out of memory");
    }

    while (pipes[0] != PIPE_NONE || pipes[1] != PIPE_NONE || pipes[2] != PIPE_NONE) {
        bool moved = false;
        if (pipes[0] != PIPE_NONE) {
            int64_t put = written == input->length ? 0 : pipe_write(pipes[0], input->bytes + written, input->length - written);
            if (put > 0) {
                written += put;
                moved = true;
            }

            // Done, or it stopped reading: either way, its input ends.
            if (put == IO_FAILED || written == input->length) {
                pipe_close(pipes[0]);
                pipes[0] = PIPE_NONE;
                moved = true;
            }
        }

        for (int i = 1; i < 3; i++) {
            if (pipes[i] == PIPE_NONE) {
                continue;
            }

            int64_t got = pipe_read(pipes[i], chunk, 65536);
            if (got > 0) {
                buffer_add(&gathered[i], chunk, (size_t)got);
                moved = true;
            } else if (got != IO_WOULD_BLOCK) {
                pipe_close(pipes[i]);
                pipes[i] = PIPE_NONE;
                moved = true;
            }
        }

        if (!moved) {
            pipes_wait(pipes);
        }
    }

    free(chunk);
    process_wait(&child);
    process_reap(&child, &process_last_status, &process_last_signal);
    free(process_last_errors);
    process_last_errors = gathered[2].bytes;
    process_last_errors_length = gathered[2].length;
    SableString *result = make_text(gathered[1].bytes == NULL ? "" : gathered[1].bytes, (int64_t)gathered[1].length);
    free(gathered[1].bytes);
    io_last_failed = false;
    return result;
}

// ---- Children -----------------------------------------------------------------

// A program started and left running (Sable.Process's Child). A thread of
// its own waits for it and reaps it, whether or not the program asks how
// it ended, so that no child stays a zombie. It is signalled and reaped
// under the lock: a signal never goes to an identifier that has been let
// go, which might by then be another process's.
typedef struct ChildProcess {
    PlatformMutex lock;
    PlatformProcess process;
    int64_t references; // the Child's, and the thread's
    bool ended;
    int64_t code, signal;
    int64_t *events; // told once it has ended
    int64_t event_count, event_capacity;
    int64_t pipes[3]; // the sources' handles, or 0
} ChildProcess;

static void child_release(ChildProcess *child) {
    if (__atomic_sub_fetch(&child->references, 1, __ATOMIC_ACQ_REL) == 0) {
        free(child->events);
        free(child);
    }
}

static void child_main(void *argument) {
    ChildProcess *child = argument;
    process_wait(&child->process);
    mutex_lock(&child->lock);
    process_reap(&child->process, &child->code, &child->signal);
    child->ended = true;
    int64_t *events = child->events;
    int64_t count = child->event_count;
    child->events = NULL;
    child->event_count = child->event_capacity = 0;
    mutex_unlock(&child->lock);
    for (int64_t i = 0; i < count; i++) {
        io_event(events[i]);
    }

    free(events);
    child_release(child);
}

// The program started: its handle, or 0, with sable_process_error saying
// why not. Two bits of 'flags' for each stream, what it is (STDIO_); the
// next, whether the environment is the one given rather than this
// program's. An empty directory is this program's.
int64_t sable_process_spawn(const SableString *program, const SableArray *arguments, const SableArray *environment, int64_t flags,
    const SableString *directory, const SableArray *files) {
    ProcessSpec spec = { .argv = c_strings(program, arguments) };
    char **variables = (flags & 64) != 0 ? c_strings(NULL, environment) : NULL;
    char *start = directory->length == 0 ? NULL : c_string(directory);
    char **names = c_strings(NULL, files);
    spec.environment = variables;
    spec.directory = start;
    for (int i = 0; i < 3; i++) {
        spec.stdio[i] = (int)(flags >> (2 * i)) & 3;
        spec.files[i] = names[i];
    }

    ChildProcess *child = calloc(1, sizeof *child);
    if (child == NULL) {
        sable_trap("out of memory");
    }

    PlatformPipe pipes[3];
    bool started = process_start(&spec, &child->process, pipes, &process_last_failure);
    free_strings(spec.argv);
    free_strings(names);
    free(start);
    if (variables != NULL) {
        free_strings(variables);
    }

    if (!started) {
        free(child);
        return 0;
    }

    mutex_init(&child->lock);
    child->references = 2;
    for (int i = 0; i < 3; i++) {
        child->pipes[i] = pipes[i] == PIPE_NONE ? 0 : io_source_open(pipes[i], SOURCE_PIPE, REACTOR_READABLE | REACTOR_WRITABLE);
    }

    bool watched = true;
    for (int i = 0; i < 3; i++) {
        watched = watched && (pipes[i] == PIPE_NONE || child->pipes[i] != 0);
    }

    if (!watched || !thread_start(child_main, child)) {
        // Nothing to hear of it by: it is ended, as if it never began.
        process_signal(&child->process, 9);
        process_wait(&child->process);
        process_reap(&child->process, &child->code, &child->signal);
        for (int i = 0; i < 3; i++) {
            io_source_close(child->pipes[i]);
        }

        free(child);
        process_last_failure = (ProcessError){ PROCESS_ERROR_OTHER, 0 };
        return 0;
    }

    return (int64_t)(intptr_t)child;
}

static ChildProcess *child_of(int64_t handle) {
    return (ChildProcess *)(intptr_t)handle;
}

// This program's end of a stream, as a source's handle: 0 for one not piped.
int64_t sable_process_pipe(int64_t handle, int64_t which) {
    return child_of(handle)->pipes[which];
}

int64_t sable_process_child_id(int64_t handle) {
    return child_of(handle)->process.id;
}

bool sable_process_ended(int64_t handle) {
    ChildProcess *child = child_of(handle);
    mutex_lock(&child->lock);
    bool ended = child->ended;
    mutex_unlock(&child->lock);
    return ended;
}

// How it ended, once it has: its exit code (128 and the signal, for one a
// signal ended), or the signal, or 0.
int64_t sable_process_exit_code(int64_t handle, bool signal) {
    ChildProcess *child = child_of(handle);
    mutex_lock(&child->lock);
    int64_t result = signal ? child->signal : child->code;
    mutex_unlock(&child->lock);
    return result;
}

// An event of its ending, for one task to wait for, and the caller to
// close: 0 when it has ended.
int64_t sable_process_exit_event(int64_t handle) {
    ChildProcess *child = child_of(handle);
    mutex_lock(&child->lock);
    int64_t event = child->ended ? 0 : io_event_open(false);
    if (event != 0) {
        // One closed since names nothing, and its place is free again.
        int64_t n = 0;
        for (int64_t i = 0; i < child->event_count; i++) {
            if (source_of(child->events[i]) != NULL) {
                child->events[n++] = child->events[i];
            }
        }

        child->event_count = n;
        if (child->event_count == child->event_capacity) {
            child->event_capacity = child->event_capacity == 0 ? 4 : child->event_capacity * 2;
            child->events = realloc(child->events, (size_t)child->event_capacity * sizeof(int64_t));
            if (child->events == NULL) {
                sable_trap("out of memory");
            }
        }

        child->events[child->event_count++] = event;
    }

    mutex_unlock(&child->lock);
    return event;
}

// The signal sent, or (-1) it and all it started killed: 0 done, 1 it has
// ended already, 2 it could not be.
int64_t sable_process_signal(int64_t handle, int64_t signal) {
    ChildProcess *child = child_of(handle);
    mutex_lock(&child->lock);
    int64_t result = child->ended ? 1
                     : (signal == -1 ? process_kill_tree(&child->process) : process_signal(&child->process, (int)signal)) ? 0
                                                                                                                       : 2;
    mutex_unlock(&child->lock);
    return result;
}

// The Child let go of: the process runs on, and is reaped all the same.
void sable_process_release(int64_t handle) {
    child_release(child_of(handle));
}

// ---- Signals -----------------------------------------------------------------

// Who listens for signals (Sable.Process's Signals): each for some, as bits,
// with those that came and were not taken, and an event of one coming. A
// signal is caught while anyone listens for it, and does what it did before
// once no one does.
typedef struct SignalWatch {
    uint64_t wanted, came;
    int64_t event;
    struct SignalWatch *next;
} SignalWatch;

static SpinLock signals_lock;
static SignalWatch *signal_watches;
static bool signals_stopped;

static uint64_t signals_wanted(void) {
    uint64_t wanted = 0;
    for (SignalWatch *watch = signal_watches; watch != NULL; watch = watch->next) {
        wanted |= watch->wanted;
    }

    return wanted;
}

static void signal_arrived(int signal) {
    if (signal < 0 || signal > 63) {
        return;
    }

    spin_lock(&signals_lock);
    for (SignalWatch *watch = signal_watches; watch != NULL && !signals_stopped; watch = watch->next) {
        if (watch->wanted & (1ull << signal)) {
            watch->came |= 1ull << signal;
            io_event(watch->event);
        }
    }

    spin_unlock(&signals_lock);
}

// The program ends: no task is told of a signal from now on.
static void signals_stop(void) {
    spin_lock(&signals_lock);
    signals_stopped = true;
    spin_unlock(&signals_lock);
}

// Listening for the signals, as bits: a handle, or 0 when one cannot be
// caught.
int64_t sable_signal_watch(int64_t wanted) {
    SignalWatch *watch = calloc(1, sizeof *watch);
    if (watch == NULL) {
        sable_trap("out of memory");
    }

    watch->wanted = (uint64_t)wanted;
    watch->event = io_event_open(true);
    spin_lock(&signals_lock);
    uint64_t others = signals_wanted();
    bool caught = watch->event != 0;
    for (int signal = 0; signal < 64 && caught; signal++) {
        if (watch->wanted & (1ull << signal)) {
            caught = signal_catch(signal);
        }
    }

    if (caught) {
        watch->next = signal_watches;
        signal_watches = watch;
    } else {
        for (int signal = 0; signal < 64; signal++) {
            if ((watch->wanted & ~others) & (1ull << signal)) {
                signal_let_go(signal);
            }
        }
    }

    spin_unlock(&signals_lock);
    if (!caught) {
        io_source_close(watch->event);
        free(watch);
        return 0;
    }

    return (int64_t)(intptr_t)watch;
}

// The event of a signal coming, to wait for.
int64_t sable_signal_event(int64_t handle) {
    return ((SignalWatch *)(intptr_t)handle)->event;
}

// A signal that came, taken, or 0: the next will be told of again.
int64_t sable_signal_take(int64_t handle) {
    SignalWatch *watch = (SignalWatch *)(intptr_t)handle;
    int64_t signal = 0;
    spin_lock(&signals_lock);
    if (watch->came != 0) {
        signal = __builtin_ctzll(watch->came);
        watch->came &= watch->came - 1;
    }

    if (watch->came == 0) {
        io_event_reset(watch->event);
    }

    spin_unlock(&signals_lock);
    return signal;
}

// No longer listening: a task waiting wakes.
void sable_signal_unwatch(int64_t handle) {
    SignalWatch *watch = (SignalWatch *)(intptr_t)handle;
    spin_lock(&signals_lock);
    SignalWatch **at = &signal_watches;
    while (*at != watch) {
        at = &(*at)->next;
    }

    *at = watch->next;
    uint64_t others = signals_wanted();
    for (int signal = 0; signal < 64; signal++) {
        if ((watch->wanted & ~others) & (1ull << signal)) {
            signal_let_go(signal);
        }
    }

    spin_unlock(&signals_lock);
    io_source_close(watch->event);
    free(watch);
}

bool sable_signal_raise(int64_t signal) {
    return signal_raise((int)signal);
}

// ---- This process ---------------------------------------------------------------

// The program ended here, as main returning the status would end it, but
// for what is still alive, which is no leak.
_Noreturn void sable_process_exit(int64_t status) {
    __atomic_store_n(&process_exiting, true, __ATOMIC_RELEASE);
    exit((int)(status & 0xFFFFFFFF));
}

// And as a trap ends it, with nothing said and nothing done at exit.
_Noreturn void sable_process_abort(void) {
    stop_trapped();
}

SableString *sable_process_current_directory(void) {
    char *path = current_directory();
    if (path == NULL) {
        return make_string("", 0);
    }

    SableString *result = make_ctext(path);
    free(path);
    return result;
}

bool sable_process_change_directory(const SableString *path) {
    char *name = c_string(path);
    bool changed = change_directory(name);
    free(name);
    return changed;
}

int64_t sable_process_id(void) {
    return process_id();
}

// The path of this program's executable, so that the program can start
// itself again (the test harness does, a process per test): as the
// platform knows it (executable_path).
SableString *sable_process_executable_path(void) {
    char *path = executable_path(saved_argc, saved_argv);
    if (path == NULL) {
        return make_string("", 0);
    }

    SableString *result = make_ctext(path);
    free(path);
    return result;
}
