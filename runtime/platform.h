// The Sable runtime's platform layer: what the rest of the runtime asks of
// the operating system. platform_posix.c gives it for macOS and Linux,
// platform_win32.c for Windows, platform_wasi.c for WebAssembly under WASI;
// sable_runtime.c includes the one for the system it is compiled for,
// before the parts that use it.
//
// Every name and string crosses as UTF-8, NUL-terminated; a string the
// layer gives back is in memory the caller frees, and NULL where there is
// none. Each layer also defines:
//
//   SABLE_AT_EXIT(f)    the start of the definition of 'static void f(void)',
//                       which runs as the program exits

#ifndef SABLE_RUNTIME_PLATFORM_H
#define SABLE_RUNTIME_PLATFORM_H

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#elif !defined(__wasi__)
#include <pthread.h>
#endif

// Whether the system runs more than one thread. WASI's does not: the
// executor has no pool (as with SABLE_WORKERS=0), and the blocking pool's
// work runs where it is asked for. (-DPLATFORM_THREADS=0 makes any
// system's runtime so, which is how that path is tried off WASI.)
#ifndef PLATFORM_THREADS
#ifdef __wasi__
#define PLATFORM_THREADS 0
#else
#define PLATFORM_THREADS 1
#endif
#endif

// ---- Start-up --------------------------------------------------------------

// What the system needs set up before any Sable code runs: the main
// thread's stack watched, the standard streams' modes. Called once, by
// sable_runtime_start.
static void platform_start(void);

// ---- Traps ---------------------------------------------------------------

// The most frames a backtrace holds.
enum { BACKTRACE_DEPTH = 64 };

// The return addresses of the calls that led to the caller, its caller's
// first, at most 'capacity' of them; how many there are.
static int capture_frames(void **frames, int capacity);

// A frame as text: 'symbol at file:line:column' where a debug build's line
// table places it (lines.c, or the PDB on Windows), 'symbol + offset' where
// only the symbol is known, the address otherwise.
static void name_frame(void *frame, char *text, size_t size);

// The program's own DWARF line table (lines.c reads it): .debug_line, and
// the sections its names are in, in memory for as long as the program
// runs; how far the program was moved from the addresses it gives as it
// was loaded; and where the program itself starts in memory, as dladdr
// gives an image's start. False where there is none: a build without -g,
// a .dSYM that is not this build's, Windows and WASI.
typedef struct LineSections {
    const uint8_t *line, *line_str, *str;
    size_t line_size, line_str_size, str_size;
    intptr_t slide;
    const void *image;
} LineSections;

static bool line_sections(LineSections *sections);

// Ends the program as a trap does: as an abort is reported, status 134 in
// a shell, on every platform.
_Noreturn static void stop_trapped(void);

// Whether a debugger is attached to the process now (Debugger.isAttached):
// what Debugger.break() asks before it stops for one.
bool sable_debugger_attached(void);

// ---- Memory --------------------------------------------------------------

// 'size' bytes of zeroed memory straight from the system, starting at a
// multiple of 'size' (a power of two, a multiple of the page); NULL when
// there is none. unmap_slab gives it back to the system.
static void *map_slab(size_t size);
static void unmap_slab(void *slab, size_t size);

// The most memory the process has held at once, in bytes, as the system
// counts it (its peak resident set); 0 where the system does not say.
static int64_t peak_memory(void);

// ---- Time ----------------------------------------------------------------

// Nanoseconds from some fixed point, never going back; since 1970; and a
// wait of at least so many.
int64_t sable_time_monotonic(void);
int64_t sable_time_wall_clock(void);
void sable_time_sleep(int64_t nanoseconds);

// ---- Threads -------------------------------------------------------------

// The executor's pool (tasks.c) is a few threads, one per core, that wait
// for work on a condition. A mutex and a condition are the system's own:
// pthread's here, a slim reader/writer lock and a condition variable on
// Windows, and on WASI, with its one thread, a flag each. Each is set up
// once, by its init, before any thread uses it.
#ifdef _WIN32
typedef SRWLOCK PlatformMutex;
typedef CONDITION_VARIABLE PlatformCondition;
#elif defined(__wasi__)
typedef struct { int held; } PlatformMutex;
typedef struct { int signalled; } PlatformCondition;
#else
typedef pthread_mutex_t PlatformMutex;
typedef pthread_cond_t PlatformCondition;
#endif

static void mutex_init(PlatformMutex *mutex);
static void mutex_lock(PlatformMutex *mutex);
static void mutex_unlock(PlatformMutex *mutex);
static void condition_init(PlatformCondition *condition);
// Waits, the mutex held, until signalled — or, given a time in
// nanoseconds of sable_time_monotonic, until then at the latest (a
// negative 'until' waits for a signal alone). May wake for no reason.
static void condition_wait(PlatformCondition *condition, PlatformMutex *mutex, int64_t until);
static void condition_signal(PlatformCondition *condition);
static void condition_broadcast(PlatformCondition *condition);

// A thread of its own running 'run(argument)', never joined, with a stack
// as large as the main thread's and watched for overflow as it is; false
// when the system will not make one.
static bool thread_start(void (*run)(void *argument), void *argument);
// The processors the program may run on, at least one.
static int processor_count(void);
// Lets another thread run: a spin that has waited long enough.
static void thread_yield(void);

// ---- The I/O reactor -------------------------------------------------------

// Where the executor's parked thread waits (tasks.c): for a socket to be
// ready, for a timer, or to be woken. kqueue on macOS and the BSDs, epoll
// on Linux, an I/O completion port on Windows, told of sockets' readiness
// by ProcessSocketNotifications, and none on WASI, where reactor_open
// fails — the same shape on every system: a socket is added once, with a
// key, and is reported, edge-triggered, as it becomes readable or
// writable, until it is removed — and as it is added, for what it is ready
// for already.
//
// A socket is a file descriptor, or Windows' SOCKET.
typedef intptr_t PlatformSocket;

enum { REACTOR_READABLE = 1, REACTOR_WRITABLE = 2 };

typedef struct ReactorEvent {
    void *key;  // as the socket was added with
    int events; // REACTOR_*
} ReactorEvent;

// The reactor made; false when the system will not make one.
static bool reactor_open(void);
// The socket watched for both directions from now on, reported with 'key'.
static bool reactor_add(PlatformSocket socket, void *key);
// No longer watched: called before the socket is closed.
static void reactor_remove(PlatformSocket socket);
// Waits until a socket added is ready, reactor_wake is called, or — given a
// time in nanoseconds of sable_time_monotonic, not negative — until then at
// the latest. The sockets ready, at most 'capacity'; 0 for a wake or a time.
static int reactor_wait(ReactorEvent *events, int capacity, int64_t until);
// The thread in reactor_wait, or the next to call it, returns at once.
static void reactor_wake(void);

// ---- Sockets -----------------------------------------------------------------

// TCP, nonblocking, for Sable.IO's TcpListener and TcpStream (net.c). A
// call that would wait gives IO_WOULD_BLOCK, and the caller waits for the
// reactor to say the socket is ready; one that fails, IO_FAILED.
enum { IO_WOULD_BLOCK = -1, IO_FAILED = -2 };

// Listening on the host's address (a name or a numeric address) and port
// (0: any free one), the port bound in 'port'; IO_FAILED otherwise.
static PlatformSocket socket_listen(const char *host, int port, int *bound);
// A connection taken from the listener, nonblocking; or IO_WOULD_BLOCK, IO_FAILED.
static PlatformSocket socket_accept(PlatformSocket listener);
// A connection begun to the host and port: writable once made, when
// socket_connected says how it went. IO_FAILED when it cannot begin.
static PlatformSocket socket_connect(const char *host, int port);
// 0 once the connection is made, IO_FAILED if it could not be.
static int socket_connected(PlatformSocket socket);
// Bytes read (0 at the end), or written; or IO_WOULD_BLOCK, IO_FAILED.
static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size);
static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size);
// No more is written: the other end reads to its end.
static void socket_shutdown_write(PlatformSocket socket);
static void socket_close(PlatformSocket socket);

// ---- Arguments, files and the environment -------------------------------

// The program's arguments, from what main() was given; how many in 'count'.
static char **platform_arguments(int argc, char **argv, int *count);

static FILE *open_file(const char *name, const char *mode);
// Adds the bytes at the end of the file, making it if there is none, in one
// write other processes appending to it at once cannot split or overwrite:
// O_APPEND on POSIX, FILE_APPEND_DATA on Windows (whose C runtime's "a"
// seeks to the end and then writes, so two processes can write over each
// other). False if the file could not be opened or the write fell short.
static bool append_file(const char *name, const char *bytes, size_t length);
static bool file_exists(const char *name);
static bool is_directory(const char *name);
// A file, or an empty directory.
static bool remove_file(const char *name);
static bool make_directory(const char *name);
// The names in a directory, but '.' and '..', in no order; their number in
// 'count'. NULL when it cannot be read.
static char **list_directory(const char *name, size_t *count);
static char *environment_variable(const char *name);

// ---- Processes -----------------------------------------------------------

// Runs argv[0], found on PATH as the system finds a program, with the
// arguments after it, to its end: 'input' written to it, what it writes to
// standard output and error added to the buffers, and how it ended put in
// 'status'. False when there is no such program, or it cannot be started.
static bool run_program(char **argv, const char *input, int64_t input_length, Buffer *output, Buffer *errors, int64_t *status);

static char *current_directory(void);
static bool change_directory(const char *name);
static int64_t process_id(void);
// The path the program can be started by again, from its arguments or the system.
static char *executable_path(int argc, char **argv);

// ---- Randomness ----------------------------------------------------------

// 'length' bytes (at most 256) from the system's secure random source, for
// the hash seed; false when the system gives none.
static bool random_bytes(void *buffer, size_t length);

#endif
