// The Sable runtime's platform layer for WebAssembly under WASI (preview 1),
// with wasi-libc: what platform.h says each platform gives the rest.
//
// A WASI module has one thread (PLATFORM_THREADS is 0): the executor runs
// every task on the main thread, as SABLE_WORKERS=0 does elsewhere, and
// work for the blocking pool runs where it is asked for. There is no
// reactor: WASI preview 1 opens no socket of its own, so Sable.IO's TCP
// fails to listen or connect, and a sleeping task's wait is a sleep.
// Nor are there processes, signals or a stack to watch: a trap names no
// frames, and the link puts the stack first in memory, so that running
// off its end is an out-of-bounds access, which the engine stops.

#include <dirent.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include <wasi/api.h>

// A destructor: run as the program exits, after main returns or exit is called.
#define SABLE_AT_EXIT(name) __attribute__((destructor)) static void name(void)

// ---- Traps -------------------------------------------------------------------

// WebAssembly cannot walk its own stack: the engine names the frames of a
// trap it stops, not the program.
static int capture_frames(void **frames, int capacity) {
    (void)frames;
    (void)capacity;
    return 0;
}

static void name_frame(void *frame, char *text, size_t size) {
    snprintf(text, size, "%p", frame);
}

// The engine runs the program; nothing debugs it from inside, as a
// debugger traces a process (Debugger.isAttached).
bool sable_debugger_attached(void) {
    return false;
}

// A trap ends the module with an 'unreachable', as abort() does: the
// engine stops it, and wasmtime exits as POSIX reports an abort, status
// 134 (3 on Windows), after the trap's own report. An exit with that
// status would not do: WASI's proc_exit takes a status below 126 only,
// and wasmtime takes any other for an error of its own, status 1.
_Noreturn static void stop_trapped(void) {
    fflush(stderr);
    __builtin_trap();
}

static void platform_start(void) {
}

// ---- Memory ----------------------------------------------------------------

// From the C library's heap, aligned to its size: linear memory grows and
// never shrinks, so there is nothing to map or give back but to it.
static void *map_slab(size_t size) {
    void *slab = aligned_alloc(size, size);
    if (slab != NULL) {
        memset(slab, 0, size);
    }

    return slab;
}

static void unmap_slab(void *slab, size_t size) {
    (void)size;
    free(slab);
}

// Linear memory never shrinks: its size now is the most there has been.
static int64_t peak_memory(void) {
    return (int64_t)__builtin_wasm_memory_size(0) * 65536;
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

void sable_time_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0) {
        return;
    }

    struct timespec wait = { (time_t)(nanoseconds / 1000000000), (long)(nanoseconds % 1000000000) };
    while (nanosleep(&wait, &wait) != 0 && errno == EINTR) {
    }
}

// ---- Threads ---------------------------------------------------------------

// One thread: a mutex is never held by another, and a condition never
// signalled by one. A wait with a time is a sleep until then; one without
// would never end, since no other thread is there to signal it.
static void mutex_init(PlatformMutex *mutex) {
    mutex->held = 0;
}

static void mutex_lock(PlatformMutex *mutex) {
    mutex->held = 1;
}

static void mutex_unlock(PlatformMutex *mutex) {
    mutex->held = 0;
}

static bool mutex_try_lock(PlatformMutex *mutex) {
    if (mutex->held) {
        return false;
    }
    mutex->held = 1;
    return true;
}

static void condition_init(PlatformCondition *condition) {
    condition->signalled = 0;
}

static void condition_wait(PlatformCondition *condition, PlatformMutex *mutex, int64_t until) {
    (void)mutex;
    if (condition->signalled) {
        condition->signalled = 0;
        return;
    }

    if (until < 0) {
        sable_trap("every task is waiting: the program cannot go on");
    }

    sable_time_sleep(until - sable_time_monotonic());
}

static void condition_signal(PlatformCondition *condition) {
    condition->signalled = 1;
}

static void condition_broadcast(PlatformCondition *condition) {
    condition->signalled = 1;
}

static bool thread_start(void (*run)(void *argument), void *argument) {
    (void)run;
    (void)argument;
    return false;
}

static int processor_count(void) {
    return 1;
}

static void thread_yield(void) {
}

// ---- The I/O reactor -------------------------------------------------------

// None: no socket is ever added (socket_listen and socket_connect fail).
static bool reactor_open(void) {
    return false;
}

static bool reactor_add(PlatformSocket socket, void *key) {
    (void)socket;
    (void)key;
    return false;
}

static void reactor_remove(PlatformSocket socket) {
    (void)socket;
}

static int reactor_wait(ReactorEvent *events, int capacity, int64_t until) {
    (void)events;
    (void)capacity;
    if (until > 0) {
        sable_time_sleep(until - sable_time_monotonic());
    }

    return 0;
}

static void reactor_wake(void) {
}

// ---- Sockets -----------------------------------------------------------------

// WASI preview 1 can only accept on a socket the engine opened for the
// program; it cannot open one, nor resolve a name.
static PlatformSocket socket_listen(const char *host, int port, int *bound) {
    (void)host;
    (void)port;
    (void)bound;
    return IO_FAILED;
}

static PlatformSocket socket_accept(PlatformSocket listener) {
    (void)listener;
    return IO_FAILED;
}

static PlatformSocket socket_connect(const char *host, int port) {
    (void)host;
    (void)port;
    return IO_FAILED;
}

static int socket_connected(PlatformSocket socket) {
    (void)socket;
    return IO_FAILED;
}

static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size) {
    (void)socket;
    (void)bytes;
    (void)size;
    return IO_FAILED;
}

static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size) {
    (void)socket;
    (void)bytes;
    (void)size;
    return IO_FAILED;
}

static void socket_shutdown_write(PlatformSocket socket) {
    (void)socket;
}

static void socket_close(PlatformSocket socket) {
    close((int)socket);
}

static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length) {
    (void)name, (void)name_length, (void)bytes, (void)length;
    return 1;
}

// ---- Arguments, files and the environment ----------------------------------

// The engine gives the module only the directories it is told to
// ('wasmtime run --dir'); a path elsewhere is not found.
static char **platform_arguments(int argc, char **argv, int *count) {
    *count = argc;
    return argv;
}

static FILE *open_file(const char *name, const char *mode) {
    return fopen(name, mode);
}

// One process: nothing else appends at once.
static bool append_file(const char *name, const char *bytes, size_t length) {
    FILE *file = fopen(name, "ab");
    if (file == NULL) {
        return false;
    }

    size_t written = fwrite(bytes, 1, length, file);
    return fclose(file) == 0 && written == length;
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

static char *current_directory(void) {
    char path[4096];
    return getcwd(path, sizeof path) == NULL ? NULL : strdup(path);
}

static bool change_directory(const char *name) {
    return chdir(name) == 0;
}

// A module is no process of the system's, and has no id: 1, as the only one.
static int64_t process_id(void) {
    return 1;
}

static char *executable_path(int argc, char **argv) {
    return argc < 1 || argv[0] == NULL ? NULL : strdup(argv[0]);
}

// ---- Randomness ----------------------------------------------------------

static bool random_bytes(void *buffer, size_t length) {
    return __wasi_random_get(buffer, length) == 0;
}

// ---- File handles ---------------------------------------------------------

#include "platform_files_posix.c"

// ---- Starting programs, and signals ----------------------------------------

// A WASI module cannot start a program, has no pipes, and is sent no signal.
static bool process_start(const ProcessSpec *spec, PlatformProcess *process, PlatformPipe pipes[3], ProcessError *error) {
    (void)spec;
    (void)process;
    pipes[0] = pipes[1] = pipes[2] = PIPE_NONE;
    error->kind = PROCESS_ERROR_UNSUPPORTED;
    error->code = 0;
    return false;
}

static void process_wait(const PlatformProcess *process) {
    (void)process;
}

static void process_reap(PlatformProcess *process, int64_t *code, int64_t *signal) {
    (void)process;
    *code = -1;
    *signal = 0;
}

static bool process_signal(const PlatformProcess *process, int signal) {
    (void)process;
    (void)signal;
    return false;
}

static bool process_kill_tree(const PlatformProcess *process) {
    (void)process;
    return false;
}

static int64_t pipe_read(PlatformPipe pipe, void *bytes, int64_t size) {
    (void)pipe;
    (void)bytes;
    (void)size;
    return IO_FAILED;
}

static int64_t pipe_write(PlatformPipe pipe, const void *bytes, int64_t size) {
    (void)pipe;
    (void)bytes;
    (void)size;
    return IO_FAILED;
}

static void pipe_close(PlatformPipe pipe) {
    (void)pipe;
}

static bool pipe_watch(PlatformPipe pipe, void *key) {
    (void)pipe;
    (void)key;
    return false;
}

static void pipe_unwatch(PlatformPipe pipe) {
    (void)pipe;
}

static void pipes_wait(const PlatformPipe pipes[3]) {
    (void)pipes;
}

static bool signal_catch(int signal) {
    (void)signal;
    return false;
}

static void signal_let_go(int signal) {
    (void)signal;
}

static bool signal_raise(int signal) {
    (void)signal;
    return false;
}
