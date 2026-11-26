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

// Has 'told' given the return addresses of the calls each thread of the
// program but the calling one is in, the innermost first, a thread at a time,
// while the others run on; nothing where a system does not let a thread look
// at another (WASI). 'told' is not called with a thread stopped.
static void each_thread_frames(void (*told)(void **frames, int count));

// ---- Memory --------------------------------------------------------------

// 'size' bytes of zeroed memory straight from the system, starting at a
// multiple of 'size' (a power of two, a multiple of the page); NULL when
// there is none. unmap_slab gives it back to the system.
static void *map_slab(size_t size);
static void unmap_slab(void *slab, size_t size);

// The most memory the process has held at once, in bytes, as the system
// counts it (its peak resident set); 0 where the system does not say.
static int64_t peak_memory(void);

// The processor time this process has used, or those it has waited for, in
// nanoseconds; and the instructions it has run or the cycles it took, by the
// processor's own counters. -1 where the system does not say.
static int64_t processor_time(bool children);
static int64_t hardware_counter(bool cycles);

// ---- Time ----------------------------------------------------------------

// Nanoseconds from some fixed point, never going back; the same, but not
// counting while the machine sleeps; since 1970; and a wait of at least so
// many.
int64_t sable_time_monotonic(void);
int64_t sable_time_suspending(void);
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
// Whether it seems held by no thread, as a hint: true where that is not known.
static bool mutex_looks_free(PlatformMutex *mutex);
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
// The same, with a stack of about so many bytes: no fewer than the system's
// least.
static bool thread_start_sized(void (*run)(void *argument), void *argument, size_t stack);
enum { THREAD_STACK = 8 * 1024 * 1024 };
// The calling thread's, for a debugger to show, as much of it as there is room
// for; and how soon it runs, from -2 to 2, where the system allows it.
static void thread_set_name(const char *name);
static void thread_set_priority(int priority);
// Has the calling thread, one the runtime did not start, give 'value' to
// foreign_thread_end (foreign.c) as it ends.
static void platform_at_thread_exit(void *value);
// The processors the program may run on, at least one: no more than it is
// bound to, or, on Linux, than its control group may have the time of.
static int processor_count(void);
// Lets another thread run: a spin that has waited long enough.
static void thread_yield(void);

// ---- Libraries (foreign.c) ---------------------------------------------------

// The program's own symbols for a path of NULL. 'flags': LIBRARY_GLOBAL, its
// symbols are given to libraries loaded after it. NULL, or false, with why in
// 'message', where one fails. A symbol there may still be at NULL.
enum { LIBRARY_GLOBAL = 1 };

static void *library_open(const char *path, int flags, char **message);
static bool library_symbol(void *library, const char *name, void **address, char **message);
static void library_close(void *library);

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

// Streams and datagrams over IP and Unix domain sockets, nonblocking, for
// Sable.Net (net.c). A call that would wait gives IO_WOULD_BLOCK, and the
// caller waits for the reactor to say the socket is ready; one that fails,
// IO_FAILED, net_last_failure saying why.
enum { IO_WOULD_BLOCK = -1, IO_FAILED = -2 };

// How a call failed, as IoError tells failures apart (Sable.Net's
// SocketError.sb has the same numbers), and the system's own code.
enum {
    NET_ERROR_OTHER = 0,
    NET_ERROR_DISCONNECTED = 1,
    NET_ERROR_REFUSED = 2,
    NET_ERROR_TIMED_OUT = 3,
    NET_ERROR_UNREACHABLE = 4,
    NET_ERROR_IN_USE = 5,
    NET_ERROR_PERMISSION = 6,
    NET_ERROR_TRY_LATER = 7, // out of descriptors, memory or room: no readiness says when there is some
    NET_ERROR_INVALID = 8,
    NET_ERROR_TOO_LONG = 9,
    NET_ERROR_UNSUPPORTED = 10,
    NET_ERROR_NOT_FOUND = 11,
    NET_ERROR_CLOSED = 12,
    NET_ERROR_EXISTS = 13, // a name made new is another's already
};

typedef struct NetFailure {
    int kind;
    int64_t code;
} NetFailure;

static _Thread_local NetFailure net_last_failure;

enum { NET_NONE = 0, NET_UNIX = 1, NET_IPV4 = 4, NET_IPV6 = 6 };

// An IP address and port, an IPv4 address the low 32 bits of 'low'; or a
// Unix domain socket's name, with no byte after it.
typedef struct PlatformAddress {
    int family;
    int port;
    uint32_t scope;
    uint64_t high, low;
    size_t path_length;
    char path[256];
} PlatformAddress;

// How a socket is bound. REUSE_ADDRESS is several datagram sockets' on one
// port, for multicast; a listener can always take a port a connection of an
// earlier one is still closing on, and never one another listens on.
// REUSE_PORT has several sockets share a port, which the system divides
// connections and datagrams among.
enum { NET_REUSE_ADDRESS = 1, NET_REUSE_PORT = 2, NET_DUAL_STACK = 4, NET_REPLACE = 8 };

static PlatformSocket socket_listen(const PlatformAddress *address, int backlog, int flags);
// A connection taken from the listener, nonblocking; or IO_WOULD_BLOCK, IO_FAILED.
static PlatformSocket socket_accept(PlatformSocket listener, PlatformAddress *peer);
// A connection begun: writable once made, when socket_connected says how it
// went. IO_FAILED when it cannot begin.
static PlatformSocket socket_connect(const PlatformAddress *address);
// 0 once the connection is made, IO_WOULD_BLOCK before, IO_FAILED if it
// could not be.
static int socket_connected(PlatformSocket socket);
// A datagram socket bound to the address; unbound, for a Unix one with no name.
static PlatformSocket socket_datagram(const PlatformAddress *address, int flags);
// It sends to the address, and takes only what is from it.
static int socket_connect_datagram(PlatformSocket socket, const PlatformAddress *address);
// Bytes read (0 at the end), or written; or IO_WOULD_BLOCK, IO_FAILED.
static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size);
static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size);
// A datagram sent, to the one it is connected to for a family of NET_NONE;
// or received, no more of it than 'size'.
static int64_t socket_send_to(PlatformSocket socket, const void *bytes, int64_t size, const PlatformAddress *address);
static int64_t socket_receive(PlatformSocket socket, void *bytes, int64_t size, PlatformAddress *from, bool *truncated);
// Its own address, or its peer's.
static bool socket_name(PlatformSocket socket, bool peer, PlatformAddress *address);

enum {
    NET_OPTION_NO_DELAY = 1,
    NET_OPTION_KEEP_ALIVE = 2,
    NET_OPTION_KEEP_IDLE = 3,     // seconds
    NET_OPTION_KEEP_INTERVAL = 4, // seconds
    NET_OPTION_KEEP_COUNT = 5,
    NET_OPTION_RECEIVE_BUFFER = 6,
    NET_OPTION_SEND_BUFFER = 7,
    NET_OPTION_LINGER = 8, // seconds; -1, none
    NET_OPTION_HOPS = 9,
    NET_OPTION_BROADCAST = 10,
    NET_OPTION_MULTICAST_LOOP = 11,
    NET_OPTION_MULTICAST_HOPS = 12,
    NET_OPTION_MULTICAST_INTERFACE = 13,
    NET_OPTION_IPV6_ONLY = 14,
    NET_OPTION_DONT_FRAGMENT = 15, // not broken up on the way: too long for a link, dropped
    NET_OPTION_HEADER_INCLUDED = 16, // a raw IPv4 socket's sends have their own header
    NET_OPTION_PEER_PROCESS = 17,  // a Unix domain socket's peer's process, read only; -1 unknown
    NET_OPTION_PEER_USER = 18,     // and its user
};

static bool socket_set_option(PlatformSocket socket, int option, int64_t value);
static bool socket_get_option(PlatformSocket socket, int option, int64_t *value);
// In the multicast group, on the interface of that index (0: the system's
// choice), or out of it.
static bool socket_membership(PlatformSocket socket, bool join, const PlatformAddress *group, uint32_t index);
// No more is written: the other end reads to its end. Or no more is read.
static void socket_shutdown(PlatformSocket socket, bool writing);
static void socket_close(PlatformSocket socket);

// One datagram sent to a Unix socket, by its path, or (Linux) by its abstract
// name, '@' first: 0 sent, 1 the system has none such, 2 it could not be sent.
static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length);

// The host's addresses, in the order the system would try them, of one
// family or (NET_NONE) any, in memory the caller frees: how many, or -1 with
// a NET_ERROR_ in 'code'. It may wait, for as long as the system does.
static int host_resolve(const char *host, int family, PlatformAddress **addresses, int *code);

enum {
    NET_INTERFACE_UP = 1,
    NET_INTERFACE_RUNNING = 2,
    NET_INTERFACE_LOOPBACK = 4,
    NET_INTERFACE_POINT_TO_POINT = 8,
    NET_INTERFACE_MULTICAST = 16,
    NET_INTERFACE_BROADCAST = 32,
};

// An interface, and one of its addresses, or none.
typedef struct PlatformInterface {
    char name[256];
    uint32_t index;
    int flags;
    int64_t mtu;
    int64_t hardware; // its 48 bits, or -1
    int prefix;       // the network's, of the address
    PlatformAddress address;
} PlatformInterface;

// A row for each interface's address and each interface, in memory the
// caller frees: how many, or -1.
static int interfaces_list(PlatformInterface **interfaces);
// The index of the interface of that name; 0 for none.
static uint32_t interface_index(const char *name);

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
// The locale the system is set to, as it writes it ("en_US", "cs-CZ"): macOS's
// and Windows's region setting. NULL on other systems, where the environment
// alone (LC_ALL, LANG) says.
static char *user_locale(void);
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
    FILE_ERROR_NOT_EMPTY = 8,
    FILE_ERROR_NOT_DIRECTORY = 9,
    FILE_ERROR_CROSSES = 10,
    FILE_ERROR_UNSUPPORTED = 11,
};

typedef struct FileError {
    int kind;
    int64_t code;
} FileError;

// What something is: Sable.IO's FileKind, by number.
enum {
    FILE_KIND_FILE = 0,
    FILE_KIND_DIRECTORY = 1,
    FILE_KIND_LINK = 2,
    FILE_KIND_FIFO = 3,
    FILE_KIND_SOCKET = 4,
    FILE_KIND_CHARACTER = 5,
    FILE_KIND_BLOCK = 6,
    FILE_KIND_OTHER = 7,
};

// Given to the library as it is, an array of ten integers (Sable.IO's
// FileMetadata reads it). A time is seconds since 1970, UTC, and the
// nanoseconds into that second.
typedef struct FileMetadata {
    int64_t size;
    int64_t kind;
    // POSIX's mode bits; on Windows, what a read-only attribute leaves.
    int64_t permissions;
    int64_t modified[2];
    int64_t accessed[2];
    int64_t created[2];
    int64_t has_created;
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
// A descriptor of the C library's closed, or made another of; -1 for failure.
static bool descriptor_close(int descriptor, FileError *error);
static int descriptor_duplicate(int descriptor, FileError *error);
// Standard input (0), output (1) or error (2), as a file.
static PlatformFile file_standard(int which);
// The whole file copied, its permissions with it, at the system's fastest;
// a file at 'to' written over, or an error. One copied to itself is an error.
static bool file_copy(const char *from, const char *to, bool replace, FileError *error);
// The path made absolute, every link and '.' and '..' resolved: realpath,
// or the final path of a handle on Windows. NULL, with why, when it is not
// there; the caller frees it.
static char *file_canonical(const char *name, FileError *error);

// ---- The file system ---------------------------------------------------------
//
// By path (Sable.IO's File, Directory, Temp): each call that fails says why
// as a file handle's does.

enum { FILE_TIME_MODIFIED = 0, FILE_TIME_ACCESSED = 1 };
// A hard link; or a symbolic one, to a file or to a directory, which
// Windows tells apart.
enum { FILE_LINK_HARD = 0, FILE_LINK_FILE = 1, FILE_LINK_DIRECTORY = 2 };

// What is at the path, or what a link there leads to.
static bool fs_metadata(const char *name, bool follow, FileMetadata *metadata, FileError *error);
// The mode bits; on Windows a file no one may write is made read-only.
static bool fs_set_permissions(const char *name, int permissions, FileError *error);
static bool fs_set_time(const char *name, int which, int64_t seconds, int64_t nanoseconds, FileError *error);
// Made, with the mode bits less the umask; an error if anything is there.
static bool fs_make_directory(const char *name, int permissions, FileError *error);
// A file or a link itself, or an empty directory.
static bool fs_remove(const char *name, bool directory, FileError *error);
// The directory and all in it. Nothing outside it is ever removed: no link
// in it is followed, whatever another program puts there meanwhile. A path
// whose last part is no name ('', '/', '.', '..') and a root are refused.
static bool fs_remove_tree(const char *name, FileError *error);
// Renamed in one step, what is at 'to' replaced, or an error.
static bool fs_rename(const char *from, const char *to, bool replace, FileError *error);
static bool fs_link(const char *target, const char *name, int kind, FileError *error);
// Where the symbolic link leads, as it says it; the caller frees it.
static char *fs_read_link(const char *name, FileError *error);

// A directory's entries, one at a time and in no order, '.' and '..' left
// out: the next one's name, which the caller frees, and its kind, -1 where
// the directory does not say; NULL at the end, or with why.
static void *directory_open(const char *name, FileError *error);
static char *directory_next(void *directory, int *kind, FileError *error);
static void directory_close(void *directory);

// ---- Watching ----------------------------------------------------------------

// What happened to a path being watched: Sable.IO's FileEvent, by number.
enum { WATCH_CREATED = 1, WATCH_MODIFIED = 2, WATCH_REMOVED = 3, WATCH_RESCAN = 4 };

// Told by the platform of what happened, the path inside the one watched
// ("" for it itself): watch.c gives it, any thread calls it.
static void watch_report(void *watch, int kind, const char *path, size_t length);

// The path watched, what happens there reported to 'watch' (FSEvents on
// macOS, inotify on Linux, ReadDirectoryChangesW on Windows) until it is
// closed, after which nothing more is. NULL with why.
static void *watch_open(void *watch, const char *name, bool recursive, FileError *error);
// What the reactor is to watch, for watch_read to have more (inotify), or
// -1 where it is reported as it happens.
static PlatformSocket watch_descriptor(void *state);
// What has happened, reported; false when there is no more now.
static bool watch_read(void *state);
static void watch_close(void *state);

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
// a handle of this program's (ipc.c's child end of a pipe), given as it is.
enum { STDIO_INHERIT = 0, STDIO_NULL = 1, STDIO_PIPED = 2, STDIO_FILE = 3, STDIO_HANDLE = 4 };

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
    intptr_t handles[3];   // for STDIO_HANDLE: a descriptor, or a HANDLE, this program keeps
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
// That an event (tasks.c's io_event_open) has happened, said from any
// thread: Windows' change notifications call it. tasks.c gives it.
static void io_event(int64_t handle);

// ---- Pipes, local streams, shared memory and serial ports ------------------

// What Sable.IO's OsPipe, Sable.IO.Ipc and Sable.IO.Ports are over (ipc.c):
// platform_ipc_posix.c gives it for macOS and Linux, platform_ipc_win32.c
// for Windows; on WASI every call fails as unsupported. A call that fails
// gives false, -1 or IO_FAILED, and says why in net_last_failure.

// A handle given as it is: a descriptor, or a HANDLE; -1 for none.
typedef intptr_t PlatformHandle;

// A pipe: each end this program's (a PlatformPipe, nonblocking, watched as
// a child's stream is) or, by its bit in 'children' (1 the reader, 2 the
// writer), a blocking handle for a child to be given, inherited by none.
static bool os_pipe(int children, intptr_t ends[2]);
static void handle_close(PlatformHandle handle);

// A FIFO made at the path, of the permissions (POSIX), and an end of one
// opened, nonblocking: IO_WOULD_BLOCK for a writer while no reader has it.
static bool fifo_make(const char *path, int permissions);
static PlatformPipe fifo_open(const char *path, bool writes);

// The directory a user's local sockets and locks are in: made, of this
// user's alone and checked to be, or (any) one every user may reach. In
// memory the caller frees; NULL when there is none.
static char *local_directory(bool any);

// A named pipe (Windows): 1 for this user alone, 2 messages kept whole.
enum { LOCAL_CURRENT_USER = 1, LOCAL_MESSAGES = 2 };
// An instance of the pipe of that name to accept a client by; 'first'
// fails if there is one already, another program's perhaps.
static PlatformPipe local_listen(const char *name, int flags, bool first);
// 0 once a client is connected to the instance, IO_WOULD_BLOCK before.
static int local_accept(PlatformPipe pipe);
// The pipe's other direction, to write by: a source of its own.
static PlatformPipe local_writer(PlatformPipe pipe);
// A client's end; IO_WOULD_BLOCK while every instance is taken.
static PlatformPipe local_connect(const char *name, int flags);
// The process at the other end, or -1.
static int64_t local_peer_process(PlatformPipe pipe, bool server);

// Shared memory of a name: made (failing NET_ERROR_EXISTS if it is there),
// or opened, at least 'size' bytes; its handle, mapped by file_map as
// FILE_MAP_SHARED, and how big it is in 'actual'. Removed, its name is
// free; what is mapped stays until it is let go.
enum { FILE_MAP_SHARED = 3 };
static PlatformHandle shared_open(const char *name, int64_t size, bool create, int64_t *actual);
static bool shared_remove(const char *name);

// A semaphore of a name, made with the count or opened; one taken without
// waiting (1, or 0 when there is none to take, -1 failing), and given back.
static PlatformHandle semaphore_open(const char *name, int64_t count, bool create);
static int semaphore_try(PlatformHandle semaphore);
static bool semaphore_release(PlatformHandle semaphore, int64_t count);
static void semaphore_close(PlatformHandle semaphore);
static bool semaphore_remove(const char *name);

// The file at the path, made if need be, held by this process alone until
// it is closed or the process ends: its handle; IO_WOULD_BLOCK while
// another holds it.
static PlatformHandle lock_try(const char *path);

// A serial port opened (POSIX ends[1] is PIPE_NONE: one end both ways) and
// set as the settings say.
typedef struct SerialSettings {
    int64_t baud;
    int data_bits;
    int parity;    // none, odd, even, mark, space
    int stop_bits; // 1, 2; 3 for one and a half
    int flow;      // none, RTS/CTS, XON/XOFF
} SerialSettings;

enum { SERIAL_DTR = 1, SERIAL_RTS = 2, SERIAL_CTS = 4, SERIAL_DSR = 8, SERIAL_RING = 16, SERIAL_CARRIER = 32 };

static bool serial_open(const char *name, const SerialSettings *settings, PlatformPipe ends[2]);
static bool serial_configure(PlatformPipe port, const SerialSettings *settings);
// The control lines (SERIAL_*), or -1.
static int serial_lines(PlatformPipe port);
static bool serial_set_line(PlatformPipe port, int line, bool on);
static bool serial_break(PlatformPipe port, bool on);
static bool serial_discard(PlatformPipe port, bool input, bool output);
// Bytes waiting to be read, or (output) to go; -1 failing.
static int64_t serial_queued(PlatformPipe port, bool output);
// The ports there are, '\n' after each, in memory the caller frees (Windows;
// elsewhere the library lists /dev).
static char *serial_names(void);

// ---- Ping, raw sockets, changes and statistics (net.c) -----------------------

// A socket of datagrams (an unprivileged ICMP one) or raw, of the protocol.
static PlatformSocket socket_raw(int family, int protocol, bool raw);
// An ICMP error the system has for the socket, taken (Linux's error queue):
// its type and code, who sent it, and the bytes of the echo it answers.
static int64_t socket_icmp_error(PlatformSocket socket, int *type, int *code, PlatformAddress *from, void *bytes, int64_t size);

// An echo sent and its reply waited for (Windows: IcmpSendEcho2); the
// status, IcmpSendEcho's, in out[0], the round trip in milliseconds in
// out[1], and who answered in 'from'; false when it could not be sent.
static bool ping_echo(const PlatformAddress *to, int timeout, int hops, bool dont_fragment, const void *bytes, int64_t size, int64_t out[2], PlatformAddress *from);

// Changes to the interfaces and their addresses, as they come: a socket the
// reactor watches (POSIX), whose messages are drained and only said to have
// come; or (Windows) the system's callbacks, which tell the event.
static PlatformSocket changes_open(void);
static void changes_drain(PlatformSocket socket);
static void *changes_watch(int64_t event);
static void changes_unwatch(void *watch);

// A TCP connection or listener, or a UDP socket, of the system's.
typedef struct PlatformConnection {
    PlatformAddress local, remote;
    int state; // TCP's, as Sable.Net's TcpState numbers them
    int64_t process; // -1 unknown
} PlatformConnection;

// Every one of the protocol's (6 TCP, 17 UDP), in memory the caller frees:
// how many, or -1. macOS lists only this process's.
static int connections_list(int protocol, PlatformConnection **rows);
// The protocol's counters (NetworkStatistics): false where there are none.
static bool protocol_counters(int protocol, int64_t counters[8]);

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
