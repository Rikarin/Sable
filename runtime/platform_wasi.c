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
    (void)symbol_demangle; // no symbols to read back
    snprintf(text, size, "%p", frame);
}

// The engine runs the program; nothing debugs it from inside, as a
// debugger traces a process (Debugger.isAttached).
bool sable_debugger_attached(void) {
    return false;
}

static void each_thread_frames(void (*told)(void **frames, int count)) {
    (void)told;
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

static int64_t processor_time(bool children) {
    (void)children;
    return -1;
}

static int64_t hardware_counter(bool cycles) {
    (void)cycles;
    return -1;
}

// ---- Time (Sable.Time's floor) ------------------------------------------

int64_t sable_time_monotonic(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

// WASI has one monotonic clock, and does not say whether it stops in a sleep.
int64_t sable_time_suspending(void) {
    return sable_time_monotonic();
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

static bool mutex_looks_free(PlatformMutex *mutex) {
    return !mutex->held;
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

static bool thread_start_sized(void (*run)(void *argument), void *argument, size_t stack) {
    (void)stack;
    return thread_start(run, argument);
}

static void thread_set_name(const char *name) {
    (void)name;
}

// Unsupported: one thread, which the engine runs as it will.
static bool thread_set_priority(int priority) {
    (void)priority;
    return false;
}

static int thread_priority(void) {
    return 0;
}

static bool thread_set_affinity(const int32_t *processors, int32_t count) {
    (void)processors;
    (void)count;
    return false;
}

static int32_t thread_affinity(int32_t **processors) {
    *processors = NULL;
    return 0;
}

static bool topology_read(Topology *topology) {
    (void)topology;
    return false;
}

static int processor_count(void) {
    return 1;
}

static void thread_yield(void) {
}

// One thread, the runtime's own: none to wait for the end of.
static void platform_at_thread_exit(void *value) {
    (void)value;
}

// ---- Libraries ---------------------------------------------------------------

// A module is whole as it is linked: none is loaded as it runs.
static void *library_open(const char *path, int flags, char **message) {
    (void)path;
    (void)flags;
    *message = strdup("libraries are not loaded under WASI: unsupported");
    return NULL;
}

static bool library_symbol(void *library, const char *name, void **address, char **message) {
    (void)library;
    (void)name;
    *address = NULL;
    *message = strdup("libraries are not loaded under WASI: unsupported");
    return false;
}

static void library_close(void *library) {
    (void)library;
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
static int socket_fail_as(int kind) {
    net_last_failure.kind = kind;
    net_last_failure.code = 0;
    return IO_FAILED;
}

static int socket_unsupported(void) {
    net_last_failure.kind = NET_ERROR_UNSUPPORTED;
    net_last_failure.code = 0;
    return IO_FAILED;
}

static PlatformSocket socket_listen(const PlatformAddress *address, int backlog, int flags) {
    (void)address;
    (void)backlog;
    (void)flags;
    return socket_unsupported();
}

static PlatformSocket socket_accept(PlatformSocket listener, PlatformAddress *peer) {
    (void)listener;
    (void)peer;
    return socket_unsupported();
}

static PlatformSocket socket_connect(const PlatformAddress *address) {
    (void)address;
    return socket_unsupported();
}

static int socket_connected(PlatformSocket socket) {
    (void)socket;
    return socket_unsupported();
}

static PlatformSocket socket_datagram(const PlatformAddress *address, int flags) {
    (void)address;
    (void)flags;
    return socket_unsupported();
}

static int socket_connect_datagram(PlatformSocket socket, const PlatformAddress *address) {
    (void)socket;
    (void)address;
    return socket_unsupported();
}

static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size) {
    (void)socket;
    (void)bytes;
    (void)size;
    return socket_unsupported();
}

static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size) {
    (void)socket;
    (void)bytes;
    (void)size;
    return socket_unsupported();
}

static int64_t socket_send_to(PlatformSocket socket, const void *bytes, int64_t size, const PlatformAddress *address) {
    (void)socket;
    (void)bytes;
    (void)size;
    (void)address;
    return socket_unsupported();
}

static int64_t socket_receive(PlatformSocket socket, void *bytes, int64_t size, PlatformAddress *from, bool *truncated) {
    (void)socket;
    (void)bytes;
    (void)size;
    (void)from;
    (void)truncated;
    return socket_unsupported();
}

static bool socket_name(PlatformSocket socket, bool peer, PlatformAddress *address) {
    (void)socket;
    (void)peer;
    (void)address;
    socket_unsupported();
    return false;
}

static bool socket_set_option(PlatformSocket socket, int option, int64_t value) {
    (void)socket;
    (void)option;
    (void)value;
    socket_unsupported();
    return false;
}

static bool socket_get_option(PlatformSocket socket, int option, int64_t *value) {
    (void)socket;
    (void)option;
    (void)value;
    socket_unsupported();
    return false;
}

static bool socket_membership(PlatformSocket socket, bool join, const PlatformAddress *group, uint32_t index) {
    (void)socket;
    (void)join;
    (void)group;
    (void)index;
    socket_unsupported();
    return false;
}

static void socket_shutdown(PlatformSocket socket, bool writing) {
    (void)socket;
    (void)writing;
}

static void socket_close(PlatformSocket socket) {
    close((int)socket);
}

static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length) {
    (void)name, (void)name_length, (void)bytes, (void)length;
    return 1;
}

static int host_resolve(const char *host, int family, PlatformAddress **addresses, int *code) {
    (void)host;
    (void)family;
    *addresses = NULL;
    *code = NET_ERROR_UNSUPPORTED;
    return -1;
}

static int interfaces_list(PlatformInterface **interfaces) {
    *interfaces = NULL;
    return socket_unsupported();
}

static uint32_t interface_index(const char *name) {
    (void)name;
    return 0;
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
    // A host may refuse more than 64 KiB at once (a browser's does).
    uint8_t *next = buffer;
    while (length > 0) {
        size_t part = length < 65536 ? length : 65536;
        __wasi_errno_t error = __wasi_random_get(next, part);
        if (error == __WASI_ERRNO_INTR || error == __WASI_ERRNO_AGAIN) {
            continue;
        }

        if (error != 0) {
            return false;
        }

        next += part;
        length -= part;
    }

    return true;
}

// ---- File handles ---------------------------------------------------------

#include "platform_files_posix.c"
#include "platform_fs_posix.c"

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

// ---- Service managers ------------------------------------------------------

static int service_control(int call) {
    (void)call;
    return 3;
}

// ---- The terminal ----------------------------------------------------------

// A module is not told whether its streams are a terminal's, and can change
// nothing of one.
static bool terminal_is(int stream) {
    (void)stream;
    return false;
}

static bool terminal_styles(int stream) {
    (void)stream;
    return false;
}

static bool terminal_size(int stream, int *columns, int *rows) {
    (void)stream;
    (void)columns;
    (void)rows;
    return false;
}

static int terminal_mode(int mode) {
    (void)mode;
    return -1;
}

static void terminal_show(int shown) {
    (void)shown;
}

static void terminal_restore(void) {
}

__attribute__((unused)) static void terminal_watch(void) {
}

static int terminal_read(int64_t milliseconds) {
    (void)milliseconds;
    return TERMINAL_ENDED;
}

// Nor has it pseudoterminals.
struct Pty {
    int unused;
};

static Pty *pty_open(void) {
    return NULL;
}

static const char *pty_name(const Pty *pty) {
    (void)pty;
    return "";
}

static void pty_close(Pty *pty) {
    (void)pty;
}

static int pty_read(Pty *pty, int64_t milliseconds) {
    (void)pty;
    (void)milliseconds;
    return TERMINAL_ENDED;
}

static bool pty_write(Pty *pty, int byte) {
    (void)pty;
    (void)byte;
    return false;
}

static bool pty_resize(Pty *pty, int columns, int rows) {
    (void)pty;
    (void)columns;
    (void)rows;
    return false;
}

static int pty_mode(const Pty *pty) {
    (void)pty;
    return -1;
}

static bool pty_attach(const char *name, bool controlling) {
    (void)name;
    (void)controlling;
    return false;
}

// ---- Pipes, local streams, shared memory and serial ports --------------------

// A WASI module has none of these, nor raw sockets, changes or statistics:
// every call fails as unsupported.
static int ipc_unsupported(void) {
    return socket_unsupported();
}

static bool os_pipe(int children, intptr_t ends[2]) {
    (void)children;
    (void)ends;
    ipc_unsupported();
    return false;
}

static void handle_close(PlatformHandle handle) {
    (void)handle;
}

static bool fifo_make(const char *path, int permissions) {
    (void)path;
    (void)permissions;
    ipc_unsupported();
    return false;
}

static PlatformPipe fifo_open(const char *path, bool writes) {
    (void)path;
    (void)writes;
    return ipc_unsupported();
}

static char *local_directory(bool any) {
    (void)any;
    ipc_unsupported();
    return NULL;
}

static bool local_share(const char *path, const char *group) {
    (void)path;
    (void)group;
    ipc_unsupported();
    return false;
}

static PlatformPipe local_listen(const char *name, int flags, const char *group, bool first) {
    (void)name;
    (void)flags;
    (void)group;
    (void)first;
    return ipc_unsupported();
}

static int local_accept(PlatformPipe pipe) {
    (void)pipe;
    return ipc_unsupported();
}

static PlatformPipe local_writer(PlatformPipe pipe) {
    (void)pipe;
    return ipc_unsupported();
}

static PlatformPipe local_connect(const char *name, int flags) {
    (void)name;
    (void)flags;
    return ipc_unsupported();
}

static int64_t local_peer_process(PlatformPipe pipe, bool server) {
    (void)pipe;
    (void)server;
    return -1;
}

static PlatformHandle shared_open(const char *name, int64_t size, bool create, int64_t *actual) {
    (void)name;
    (void)size;
    (void)create;
    (void)actual;
    return ipc_unsupported();
}

static bool shared_remove(const char *name) {
    (void)name;
    ipc_unsupported();
    return false;
}

static PlatformHandle semaphore_open(const char *name, int64_t count, bool create) {
    (void)name;
    (void)count;
    (void)create;
    return ipc_unsupported();
}

static int semaphore_try(PlatformHandle semaphore) {
    (void)semaphore;
    ipc_unsupported();
    return -1;
}

static bool semaphore_release(PlatformHandle semaphore, int64_t count) {
    (void)semaphore;
    (void)count;
    ipc_unsupported();
    return false;
}

static void semaphore_close(PlatformHandle semaphore) {
    (void)semaphore;
}

static bool semaphore_remove(const char *name) {
    (void)name;
    ipc_unsupported();
    return false;
}

static PlatformHandle lock_try(const char *path) {
    (void)path;
    return ipc_unsupported();
}

static bool serial_open(const char *name, const SerialSettings *settings, PlatformPipe ends[2]) {
    (void)name;
    (void)settings;
    (void)ends;
    ipc_unsupported();
    return false;
}

static bool serial_configure(PlatformPipe port, const SerialSettings *settings) {
    (void)port;
    (void)settings;
    ipc_unsupported();
    return false;
}

static int serial_lines(PlatformPipe port) {
    (void)port;
    ipc_unsupported();
    return -1;
}

static bool serial_set_line(PlatformPipe port, int line, bool on) {
    (void)port;
    (void)line;
    (void)on;
    ipc_unsupported();
    return false;
}

static bool serial_break(PlatformPipe port, bool on) {
    (void)port;
    (void)on;
    ipc_unsupported();
    return false;
}

static bool serial_discard(PlatformPipe port, bool input, bool output) {
    (void)port;
    (void)input;
    (void)output;
    ipc_unsupported();
    return false;
}

static int64_t serial_queued(PlatformPipe port, bool output) {
    (void)port;
    (void)output;
    ipc_unsupported();
    return -1;
}

static char *serial_names(void) {
    return NULL;
}

// ---- Ping, raw sockets, changes and statistics ------------------------------------

static PlatformSocket socket_raw(int family, int protocol, bool raw) {
    (void)family;
    (void)protocol;
    (void)raw;
    return ipc_unsupported();
}

static int64_t socket_icmp_error(PlatformSocket socket, int *type, int *code, PlatformAddress *from, void *bytes, int64_t size) {
    (void)socket;
    (void)type;
    (void)code;
    (void)from;
    (void)bytes;
    (void)size;
    return IO_WOULD_BLOCK;
}

static bool ping_echo(const PlatformAddress *to, int timeout, int hops, bool dont_fragment, const void *bytes, int64_t size, int64_t out[2], PlatformAddress *from) {
    (void)to;
    (void)timeout;
    (void)hops;
    (void)dont_fragment;
    (void)bytes;
    (void)size;
    (void)out;
    (void)from;
    ipc_unsupported();
    return false;
}

static PlatformSocket changes_open(void) {
    return ipc_unsupported();
}

static void changes_drain(PlatformSocket socket) {
    (void)socket;
}

static void *changes_watch(int64_t event) {
    (void)event;
    return NULL;
}

static void changes_unwatch(void *watch) {
    (void)watch;
}

static int connections_list(int protocol, PlatformConnection **rows) {
    (void)protocol;
    *rows = NULL;
    return ipc_unsupported() == IO_FAILED ? -1 : 0;
}

static bool protocol_counters(int protocol, int64_t counters[8]) {
    (void)protocol;
    (void)counters;
    ipc_unsupported();
    return false;
}

// ---- Watching files ----------------------------------------------------------

static void *watch_open(void *watch, const char *name, bool recursive, FileError *error) {
    (void)watch;
    (void)name;
    (void)recursive;
    error->kind = FILE_ERROR_UNSUPPORTED;
    error->code = ENOTSUP;
    return NULL;
}

static PlatformSocket watch_descriptor(void *state) {
    (void)state;
    return -1;
}

static bool watch_read(void *state) {
    (void)state;
    return false;
}

static void watch_close(void *state) {
    (void)state;
}
