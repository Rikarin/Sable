// The Sable runtime's platform layer for POSIX systems — macOS and Linux:
// what platform.h says each platform gives the rest.

#include <dirent.h>
#include <dlfcn.h>
#include <execinfo.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <spawn.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

// A destructor: run as the program exits, after main returns or exit is called.
#define SABLE_AT_EXIT(name) __attribute__((destructor)) static void name(void)

// ---- Traps -------------------------------------------------------------------

// With SABLE_BACKTRACE set, a trap says where it happened: a frame per line,
// as 'sable.Module.function + offset' where the symbol is known. Off by
// default, so that a trap's message is the whole of its output. Each return
// address is looked up one byte back: after a call that never returns, the
// address is past the end of the caller, inside whatever comes next.
__attribute__((noinline)) static void print_backtrace(void) {
    if (getenv("SABLE_BACKTRACE") == NULL) {
        return;
    }

    void *frames[64];
    int count = backtrace(frames, 64);
    fprintf(stderr, "backtrace:\n");
    // The first two frames are this function and sable_trap.
    for (int i = 2; i < count; i++) {
        const char *at = (const char *)frames[i] - 1;
        Dl_info info;
        if (dladdr(at, &info) != 0 && info.dli_sname != NULL) {
            fprintf(stderr, "  %2d  %s + %ld\n", i - 2, info.dli_sname, (long)(at + 1 - (const char *)info.dli_saddr));
        } else {
            fprintf(stderr, "  %2d  %p\n", i - 2, frames[i]);
        }
    }
}

_Noreturn static void stop_trapped(void) {
    abort();
}

// ---- Stack overflow ------------------------------------------------------

// Recursion that runs off the end of a thread's stack touches the guard page
// below it, and the system sends SIGSEGV (SIGBUS on some). A handler can
// only run on a stack that is still there, so each thread is given a small
// one of its own (sigaltstack). The handler tells an overflow from any other
// bad access by where it was: at the low end of the thread's stack, which
// each thread notes as it starts.
static _Thread_local char *stack_low;

enum { SIGNAL_STACK = 64 * 1024, GUARD_SLACK = 64 * 1024, FRAME_SLACK = 1 << 20 };

// The lowest address of the calling thread's stack.
static char *thread_stack_low(void) {
#if defined(__APPLE__)
    pthread_t self = pthread_self();
    return (char *)pthread_get_stackaddr_np(self) - pthread_get_stacksize_np(self);
#else
    pthread_attr_t attributes;
    void *low = NULL;
    size_t size = 0;
    if (pthread_getattr_np(pthread_self(), &attributes) == 0) {
        pthread_attr_getstack(&attributes, &low, &size);
        pthread_attr_destroy(&attributes);
    }

    return low;
#endif
}

static void on_fault(int signal_number, siginfo_t *info, void *context) {
    (void)context;
    char *address = info->si_addr;

    // Just below the stack — a frame larger than the guard page reaches
    // further — or in its last few pages.
    if (stack_low != NULL && address >= stack_low - FRAME_SLACK && address < stack_low + GUARD_SLACK) {
        // What the program printed before is its output, as before any trap.
        fflush(stdout);
        static const char message[] = "fatal error: stack overflow: calls nested too deeply\n";
        ssize_t written = write(STDERR_FILENO, message, sizeof message - 1);
        (void)written;
        signal(SIGABRT, SIG_DFL);
        abort();
    }

    // Not an overflow: what the signal does without a handler, as the
    // faulting instruction runs again.
    signal(signal_number, SIG_DFL);
}

// Gives the calling thread a stack for the handler, and notes where its own
// stack ends. A thread that runs tasks keeps it for as long as it lives.
static void *watch_stack(void) {
    stack_low = thread_stack_low();
    stack_t alternate = { 0 };
    alternate.ss_sp = malloc(SIGNAL_STACK);
    alternate.ss_size = SIGNAL_STACK;
    if (alternate.ss_sp != NULL) {
        sigaltstack(&alternate, NULL);
    }

    return alternate.ss_sp;
}

__attribute__((constructor)) static void sable_watch_main_stack(void) {
    watch_stack();
    struct sigaction action = { 0 };
    action.sa_sigaction = on_fault;
    action.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&action.sa_mask);
    sigaction(SIGSEGV, &action, NULL);
    sigaction(SIGBUS, &action, NULL);
}

// ---- Tasks' threads -------------------------------------------------------

struct TaskCondition {
    pthread_cond_t condition;
};

static pthread_mutex_t task_lock = PTHREAD_MUTEX_INITIALIZER;

static void lock_tasks(void) {
    pthread_mutex_lock(&task_lock);
}

static void condition_init(TaskCondition *condition) {
    pthread_cond_init(&condition->condition, NULL);
}

static void condition_destroy(TaskCondition *condition) {
    pthread_cond_destroy(&condition->condition);
}

static void condition_signal(TaskCondition *condition) {
    pthread_cond_signal(&condition->condition);
}

static void condition_wait(TaskCondition *condition) {
    pthread_cond_wait(&condition->condition, &task_lock);
}

typedef struct {
    void (*body)(void *argument);
    void *argument;
} ThreadStart;

static void *thread_entry(void *start) {
    ThreadStart what = *(ThreadStart *)start;
    free(start);
    what.body(what.argument);
    return NULL;
}

static bool start_thread(void (*body)(void *argument), void *argument) {
    ThreadStart *start = malloc(sizeof(ThreadStart));
    if (start == NULL) {
        return false;
    }

    start->body = body;
    start->argument = argument;
    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    pthread_attr_setstacksize(&attributes, 8u << 20);
    pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
    pthread_t thread;
    bool started = pthread_create(&thread, &attributes, thread_entry, start) == 0;
    pthread_attr_destroy(&attributes);
    if (!started) {
        free(start);
    }

    return started;
}

// ---- Time (Sable.Time's floor) ------------------------------------------

int64_t sable_time_monotonic(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

int64_t sable_time_wall_clock(void) {
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

// At least as long as asked: nanosleep resumes after a signal.
void sable_time_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0) {
        return;
    }

    struct timespec wait = { (time_t)(nanoseconds / 1000000000), (long)(nanoseconds % 1000000000) };
    while (nanosleep(&wait, &wait) != 0 && errno == EINTR) {
    }
}

// ---- Arguments, files and the environment ----------------------------------

static char **platform_arguments(int argc, char **argv, int *count) {
    *count = argc;
    return argv;
}

static FILE *open_file(const char *name, const char *mode) {
    return fopen(name, mode);
}

static bool file_exists(const char *name) {
    struct stat info;
    return stat(name, &info) == 0;
}

static bool is_directory(const char *name) {
    struct stat info;
    return stat(name, &info) == 0 && S_ISDIR(info.st_mode);
}

static bool remove_file(const char *name) {
    return remove(name) == 0;
}

static bool make_directory(const char *name) {
    return mkdir(name, 0777) == 0;
}

static char **list_directory(const char *name, size_t *count) {
    DIR *directory = opendir(name);
    if (directory == NULL) {
        return NULL;
    }

    size_t capacity = 16;
    char **names = malloc(capacity * sizeof(char *));
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }

        if (*count == capacity) {
            capacity *= 2;
            names = realloc(names, capacity * sizeof(char *));
        }

        names[(*count)++] = strdup(entry->d_name);
    }

    closedir(directory);
    return names;
}

static char *environment_variable(const char *name) {
    const char *value = getenv(name);
    return value == NULL ? NULL : strdup(value);
}

// ---- Processes (Sable.Process's floor) ----------------------------------

// The program's path: the name itself when it holds a '/', else the first
// executable of that name on PATH; NULL when there is none. Looked up here
// rather than left to posix_spawnp, which does not report a failed exec
// everywhere (under emulation, the child just exits 127).
static char *find_program(const char *name) {
    if (strchr(name, '/') != NULL) {
        return access(name, X_OK) == 0 ? strdup(name) : NULL;
    }

    const char *path = getenv("PATH");
    if (path == NULL || *name == '\0') {
        return NULL;
    }

    size_t name_length = strlen(name);
    while (*path != '\0') {
        const char *end = strchr(path, ':');
        size_t length = end == NULL ? strlen(path) : (size_t)(end - path);
        char *candidate = malloc(length + name_length + 2);
        if (length == 0) {
            memcpy(candidate, ".", 1);
            length = 1;
        } else {
            memcpy(candidate, path, length);
        }

        candidate[length] = '/';
        memcpy(candidate + length + 1, name, name_length + 1);
        struct stat info;
        if (access(candidate, X_OK) == 0 && stat(candidate, &info) == 0 && S_ISREG(info.st_mode)) {
            return candidate;
        }

        free(candidate);
        if (end == NULL) {
            break;
        }

        path = end + 1;
    }

    return NULL;
}

// Runs the program to its end: the input written to it and its output and
// errors read as they come, all three at once, so that neither side waits
// on a full pipe.
static bool run_program(char **argv, const char *input, int64_t input_length, Buffer *output, Buffer *errors, int64_t *status) {
    char *found = find_program(argv[0]);
    if (found == NULL) {
        return false;
    }

    int in[2], out[2], err[2];
    bool piped = pipe(in) == 0;
    piped = piped && pipe(out) == 0;
    piped = piped && pipe(err) == 0;

    pid_t child = 0;
    int spawned = -1;
    if (piped) {
        posix_spawn_file_actions_t actions;
        posix_spawn_file_actions_init(&actions);
        posix_spawn_file_actions_adddup2(&actions, in[0], 0);
        posix_spawn_file_actions_adddup2(&actions, out[1], 1);
        posix_spawn_file_actions_adddup2(&actions, err[1], 2);
        int ends[] = { in[0], in[1], out[0], out[1], err[0], err[1] };
        for (int i = 0; i < 6; i++) {
            posix_spawn_file_actions_addclose(&actions, ends[i]);
        }

        fflush(stdout);
        fflush(stderr);
        spawned = posix_spawn(&child, found, &actions, NULL, argv, environ);
        posix_spawn_file_actions_destroy(&actions);
        close(in[0]);
        close(out[1]);
        close(err[1]);
    }

    free(found);

    if (spawned != 0) {
        if (piped) {
            close(in[1]);
            close(out[0]);
            close(err[0]);
        }

        return false;
    }

    int64_t written = 0;
    int writing = in[1];
    if (input_length == 0) {
        close(writing);
        writing = -1;
    }

    int reading_out = out[0], reading_err = err[0];
    char chunk[4096];
    while (reading_out >= 0 || reading_err >= 0) {
        struct pollfd watched[3];
        int n = 0;
        if (writing >= 0) {
            watched[n++] = (struct pollfd){ writing, POLLOUT, 0 };
        }

        if (reading_out >= 0) {
            watched[n++] = (struct pollfd){ reading_out, POLLIN, 0 };
        }

        if (reading_err >= 0) {
            watched[n++] = (struct pollfd){ reading_err, POLLIN, 0 };
        }

        if (poll(watched, (nfds_t)n, -1) < 0) {
            if (errno == EINTR) {
                continue;
            }

            break;
        }

        for (int i = 0; i < n; i++) {
            if (watched[i].revents == 0) {
                continue;
            }

            int fd = watched[i].fd;
            if (fd == writing) {
                ssize_t sent = write(writing, input + written, (size_t)(input_length - written));
                if (sent > 0) {
                    written += sent;
                }

                // Done, or it stopped reading: either way, its input ends.
                if (sent <= 0 || written == input_length) {
                    close(writing);
                    writing = -1;
                }

                continue;
            }

            ssize_t got = read(fd, chunk, sizeof chunk);
            if (got > 0) {
                buffer_add(fd == reading_out ? output : errors, chunk, (size_t)got);
            } else if (got == 0 || errno != EINTR) {
                close(fd);
                if (fd == reading_out) {
                    reading_out = -1;
                } else {
                    reading_err = -1;
                }
            }
        }
    }

    if (writing >= 0) {
        close(writing);
    }

    int ended = 0;
    while (waitpid(child, &ended, 0) < 0 && errno == EINTR) {
    }

    // A signal's end as the shell reports it: 128 and the signal.
    *status = WIFEXITED(ended) ? WEXITSTATUS(ended) : 128 + WTERMSIG(ended);
    return true;
}

static char *current_directory(void) {
    char path[4096];
    return getcwd(path, sizeof path) == NULL ? NULL : strdup(path);
}

static bool change_directory(const char *name) {
    return chdir(name) == 0;
}

static int64_t process_id(void) {
    return (int64_t)getpid();
}

// The path this program was started by: argv[0], made absolute when it
// names a file. A name without a '/' was found on PATH, and is given as it
// is, for process_run to find the same way.
static char *executable_path(int argc, char **argv) {
    if (argc < 1 || argv[0] == NULL) {
        return NULL;
    }

    if (strchr(argv[0], '/') != NULL) {
        char *resolved = realpath(argv[0], NULL);
        if (resolved != NULL) {
            return resolved;
        }
    }

    return strdup(argv[0]);
}
