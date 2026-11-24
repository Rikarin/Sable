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
// Takes the mutex if no one holds it: whether it did.
static bool mutex_try_lock(PlatformMutex *mutex);
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

// One datagram sent to a Unix socket, by its path, or (Linux) by its abstract
// name, '@' first: 0 sent, 1 the system has none such, 2 it could not be sent.
static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length);

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
// The variable set to the value, or removed when the value is NULL: false
// when the system refused (a name it does not take).
static bool set_environment_variable(const char *name, const char *value);
// Every variable as "NAME=value", in no order; their number in 'count'.
// The caller frees each and the list.
static char **environment_entries(size_t *count);

// ---- The system ----------------------------------------------------------
//
// What Platform and Environment report about the machine and its user. A
// text the system does not give is NULL; the caller frees one it does.

static char *host_name(void);
// The name of the user the process runs as.
static char *user_name(void);
// HOME (USERPROFILE on Windows), else the user database's.
static char *home_directory(void);
// TMPDIR (Windows: GetTempPathW), else the system's usual.
static char *temporary_directory(void);
// The operating system's version as it writes it: macOS's product version
// ("15.2"), Linux's kernel release ("6.8.0-45-generic"), Windows's
// "major.minor.build" ("10.0.22631").
static char *os_version(void);
static int64_t page_size(void);
// The machine's physical memory in bytes; 0 when it is not known.
static int64_t physical_memory(void);

// ---- File handles -----------------------------------------------------------
//
// A file opened by handle (Sable.IO's FileHandle): a descriptor on POSIX
// and WASI, a HANDLE on Windows, as an integer; -1 for none. Each call that
// fails says why in the FileError it is given: one of IoError's kinds, and
// the system's own code (errno, or GetLastError).

typedef int64_t PlatformFile;

// What a file is opened to do, OR-ed together (Sable.IO's OpenOptions).
enum {
    FILE_READ = 1,
    FILE_WRITE = 2,
    FILE_APPEND = 4,
    FILE_CREATE = 8,
    FILE_CREATE_NEW = 16,
    FILE_TRUNCATE = 32,
};

// Why a file call failed: IoError's cases, by number.
enum {
    FILE_ERROR_NONE = 0,
    FILE_ERROR_NOT_FOUND = 1,
    FILE_ERROR_PERMISSION = 2,
    FILE_ERROR_EXISTS = 3,
    FILE_ERROR_IS_DIRECTORY = 4,
    FILE_ERROR_INTERRUPTED = 5,
    FILE_ERROR_INVALID = 6,
    FILE_ERROR_OTHER = 7,
};

typedef struct FileError {
    int kind;
    int64_t code;
} FileError;

typedef struct FileMetadata {
    int64_t size;
    // Nanoseconds since 1970, UTC.
    int64_t modified;
    // 0 a file, 1 a directory, 2 anything else.
    int kind;
} FileMetadata;

// Opened as the flags say, a file made with the permissions (POSIX's mode
// bits; Windows takes none); -1 if it cannot be. A directory is not opened.
static PlatformFile file_open(const char *name, int flags, int permissions, FileError *error);
// Up to 'most' bytes read into 'into', from the position (offset < 0) or
// from the offset: how many; 0 at the end; -1 on failure.
static int64_t file_read(PlatformFile file, void *into, int64_t most, int64_t offset, FileError *error);
// Some of the bytes written, at the position or at the offset: how many;
// -1 on failure.
static int64_t file_write(PlatformFile file, const void *from, int64_t count, int64_t offset, FileError *error);
// The position moved from the start (0), itself (1) or the end (2): where
// it is now; -1 on failure.
static int64_t file_seek(PlatformFile file, int64_t offset, int whence, FileError *error);
// What is written made to reach the disk.
static bool file_sync(PlatformFile file, FileError *error);
static bool file_set_length(PlatformFile file, int64_t length, FileError *error);
static bool file_metadata(PlatformFile file, FileMetadata *metadata, FileError *error);
static bool file_close(PlatformFile file, FileError *error);
// Standard input (0), output (1) or error (2), as a file.
static PlatformFile file_standard(int which);
// The whole file copied, at the system's fastest, a file at 'to' replaced.
static bool file_copy(const char *from, const char *to, FileError *error);
// The path made absolute, every link and '.' and '..' resolved: realpath,
// or the final path of a handle on Windows. NULL, with why, when it is not
// there; the caller frees it.
static char *file_canonical(const char *name, FileError *error);

// ---- Mapped files ----------------------------------------------------------

// How a file is mapped: to read; to read and write it, writes reaching the
// file; or to read and write a private copy, writes reaching nothing else.
enum { FILE_MAP_READ_ONLY = 0, FILE_MAP_READ_WRITE = 1, FILE_MAP_COPY_ON_WRITE = 2 };
// What the program will do with a mapping, for the system to read ahead or
// let go (madvise): as usual, in order, anywhere, soon, not again soon.
enum { FILE_ADVISE_NORMAL = 0, FILE_ADVISE_SEQUENTIAL = 1, FILE_ADVISE_RANDOM = 2, FILE_ADVISE_WILL_NEED = 3, FILE_ADVISE_DONT_NEED = 4 };

// The file's first 'length' bytes (more than zero) mapped as the mode says;
// NULL, with why, when they cannot be. The mapping outlives the file's
// handle.
static void *file_map(PlatformFile file, int64_t length, int mode, FileError *error);
static void file_unmap(void *base, int64_t length);
// What was written to the bytes from 'at' on, written to the file now.
static bool file_map_flush(void *base, int64_t at, int64_t length, FileError *error);
static void file_map_advise(void *base, int64_t length, int advice);

// ---- Processes -----------------------------------------------------------

// Starting another program (process.c): platform_process_posix.c gives
// this for macOS and Linux, platform_process_win32.c for Windows; WASI has
// no processes.

// What each of a child's standard streams is: this program's own, nothing,
// a pipe to this program, or a file.
enum { STDIO_INHERIT = 0, STDIO_NULL = 1, STDIO_PIPED = 2, STDIO_FILE = 3 };

// Why a program could not be started, by number: Sable.Process's
// ProcessError.
enum {
    PROCESS_ERROR_NONE = 0,
    PROCESS_ERROR_NOT_FOUND = 1,
    PROCESS_ERROR_PERMISSION = 2,
    PROCESS_ERROR_DIRECTORY = 3,
    PROCESS_ERROR_NOT_EXECUTABLE = 4,
    PROCESS_ERROR_FILE = 5, // a stream's file; 'code' is which stream, 0 to 2
    PROCESS_ERROR_UNSUPPORTED = 6,
    PROCESS_ERROR_OTHER = 7,
};

typedef struct ProcessError {
    int kind;
    int64_t code; // the system's own: errno, or GetLastError
} ProcessError;

// What to start, and how.
typedef struct ProcessSpec {
    char **argv;           // the program, found on this program's PATH, and its arguments
    char **environment;    // "NAME=value" each, all the child has; NULL: this program's
    const char *directory; // where it starts; NULL: where this program is
    int stdio[3];
    const char *files[3];  // for STDIO_FILE: read (0), or made and emptied (1, 2)
} ProcessSpec;

typedef struct PlatformProcess {
    int64_t id;
#ifdef _WIN32
    HANDLE process;
    HANDLE job; // the child and what it starts; NULL if it could not be put in one
#endif
} PlatformProcess;

// An end of a pipe, this program's: nonblocking, and no child's to inherit.
// PIPE_NONE for none.
typedef intptr_t PlatformPipe;
#define PIPE_NONE ((PlatformPipe)-1)

// The program started, this program's ends of its piped streams in 'pipes'
// (PIPE_NONE for one not piped). Of this program's descriptors it has its
// three streams and no others; its signals are as a program's start out.
static bool process_start(const ProcessSpec *spec, PlatformProcess *process, PlatformPipe pipes[3], ProcessError *error);
// Waits for the process to end, and leaves it unreaped: its identifier
// stays its own until process_reap.
static void process_wait(const PlatformProcess *process);
// How the ended process ended — its exit code, and the signal that ended
// it, or 0 — and the system's record of it let go. Once.
static void process_reap(PlatformProcess *process, int64_t *code, int64_t *signal);
// A process not yet reaped sent a signal: whether it could be.
static bool process_signal(const PlatformProcess *process, int signal);
// A process not yet reaped killed, with every process it started and they did.
static bool process_kill_tree(const PlatformProcess *process);

// Bytes read (0 at the end) or written; or IO_WOULD_BLOCK, IO_FAILED. A
// write to a pipe no one reads fails, and raises no signal. A write that
// would block is tried again with the same bytes.
static int64_t pipe_read(PlatformPipe pipe, void *bytes, int64_t size);
static int64_t pipe_write(PlatformPipe pipe, const void *bytes, int64_t size);
static void pipe_close(PlatformPipe pipe);
// Readiness reported by the reactor with 'key' until unwatched, before it
// is closed.
static bool pipe_watch(PlatformPipe pipe, void *key);
static void pipe_unwatch(PlatformPipe pipe);
// Waits until one of the pipes, none of them watched, may be ready: the
// first to write, the other two to read; each PIPE_NONE for none.
static void pipes_wait(const PlatformPipe pipes[3]);

// What is ready, said by another thread than the one in reactor_wait: a
// pipe of Windows's. tasks.c gives it.
static void reactor_ready(void *key, int events);

// ---- Signals ---------------------------------------------------------------

// The signal caught from now on, process.c's signal_arrived called with it
// on a thread of the runtime's own — never in a handler; or left to do what
// it did before. False where the system will not (SIGKILL) or the runtime
// may not (SIGSEGV, which it watches).
static bool signal_catch(int signal);
static void signal_let_go(int signal);
static void signal_arrived(int signal);
// The signal sent to this process.
static bool signal_raise(int signal);

static char *current_directory(void);
static bool change_directory(const char *name);
static int64_t process_id(void);
// The path the program can be started by again, from its arguments or the system.
static char *executable_path(int argc, char **argv);

// ---- Randomness ----------------------------------------------------------

// 'length' bytes, however many, from the system's secure random source;
// false, the buffer's contents then not to be used, when it gives fewer.
static bool random_bytes(void *buffer, size_t length);

#endif
