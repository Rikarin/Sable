// The Sable runtime's platform layer for Windows, with the Microsoft C
// runtime: what platform.h says each platform gives the rest.
//
// Every name Windows takes or gives is UTF-16, and the runtime's are UTF-8:
// they are converted here, at the boundary, so that a path, an argument or
// a variable holds any character on Windows as it does elsewhere.

#include <winsock2.h>
#include <ws2tcpip.h>
#include <bcrypt.h>
#include <dbghelp.h>
#include <fcntl.h>
#include <io.h>
#include <shellapi.h>
#include <psapi.h>

#pragma comment(lib, "advapi32")
#pragma comment(lib, "bcrypt")
#pragma comment(lib, "dbghelp")
#pragma comment(lib, "shell32")
#pragma comment(lib, "ws2_32")

// Run as the program exits: given to atexit as the program starts. (A
// destructor is run by the C runtime only for a DLL.)
#define SABLE_AT_EXIT(name)                                                    \
    static void name(void);                                                    \
    __attribute__((constructor)) static void name##_at_exit(void) {            \
        atexit(name);                                                          \
    }                                                                          \
    static void name(void)

// ---- UTF-8 and UTF-16 ----------------------------------------------------

// In memory the caller frees.
static wchar_t *to_wide(const char *bytes) {
    int units = MultiByteToWideChar(CP_UTF8, 0, bytes, -1, NULL, 0);
    wchar_t *wide = malloc((size_t)(units > 0 ? units : 1) * sizeof(wchar_t));
    if (wide == NULL) {
        sable_trap("out of memory");
    }

    if (units <= 0) {
        wide[0] = L'\0';
    } else {
        MultiByteToWideChar(CP_UTF8, 0, bytes, -1, wide, units);
    }

    return wide;
}

static char *from_wide(const wchar_t *wide) {
    int length = WideCharToMultiByte(CP_UTF8, 0, wide, -1, NULL, 0, NULL, NULL);
    char *bytes = malloc(length > 0 ? (size_t)length : 1);
    if (bytes == NULL) {
        sable_trap("out of memory");
    }

    if (length <= 0) {
        bytes[0] = '\0';
    } else {
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, bytes, length, NULL, NULL);
    }

    return bytes;
}

// Standard streams pass bytes through as they are, as on POSIX: no '\n'
// written as "\r\n", none read back. A console shows them as UTF-8.
static void streams_start(void) {
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
    SetConsoleOutputCP(CP_UTF8);
}

// ---- Traps -------------------------------------------------------------------

// The return addresses of the calls that led here, this function's caller's
// first. Windows walks the stack by the unwind tables every function has.
__attribute__((noinline)) static int capture_frames(void **frames, int capacity) {
    return (int)CaptureStackBackTrace(1, (DWORD)capacity, frames, NULL);
}

// A frame as 'sable.Module.function at file:line' where the PDB has its
// line (a debug build's, described in CodeView), 'sable.Module.function +
// offset' where it has only the symbol, and its address otherwise, named
// from the program's PDB, which the link always writes. The return address
// is looked up one byte back, as on POSIX. DbgHelp is not safe to call
// from two threads at once, so the lookups take turns.
static void name_frame(void *frame, char *text, size_t size) {
    static bool started, symbols;
    static SRWLOCK lock = SRWLOCK_INIT;
    AcquireSRWLockExclusive(&lock);
    HANDLE process = GetCurrentProcess();
    if (!started) {
        started = true;
        SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_LOAD_LINES);
        symbols = SymInitialize(process, NULL, TRUE) != FALSE;
    }

    union {
        SYMBOL_INFO info;
        char space[sizeof(SYMBOL_INFO) + 512];
    } symbol;
    memset(&symbol, 0, sizeof symbol);
    symbol.info.SizeOfStruct = sizeof(SYMBOL_INFO);
    symbol.info.MaxNameLen = 512;
    DWORD64 offset = 0;
    const char *at = (const char *)frame - 1;
    IMAGEHLP_LINE64 line;
    memset(&line, 0, sizeof line);
    line.SizeOfStruct = sizeof line;
    DWORD displacement = 0;
    if (symbols && SymFromAddr(process, (DWORD64)(uintptr_t)at, &offset, &symbol.info)) {
        if (SymGetLineFromAddr64(process, (DWORD64)(uintptr_t)at, &displacement, &line) && line.FileName != NULL && line.LineNumber != 0) {
            // A file's absolute path by its name, as the trap's own place
            // gives it; a relative one (the library's) as it is.
            const char *file = line.FileName;
            if (file[0] == '\\' || file[0] == '/' || (file[0] != '\0' && file[1] == ':')) {
                for (const char *c = line.FileName; *c != '\0'; c++) {
                    if (*c == '\\' || *c == '/') {
                        file = c + 1;
                    }
                }
            }

            snprintf(text, size, "%s at %s:%lu", symbol.info.Name, file, (unsigned long)line.LineNumber);
        } else {
            snprintf(text, size, "%s + %lld", symbol.info.Name, (long long)offset + 1);
        }
    } else {
        snprintf(text, size, "%p", frame);
    }

    ReleaseSRWLockExclusive(&lock);
}

// ---- The debugger --------------------------------------------------------

// Whether a debugger is attached to the process now (Debugger.isAttached).
bool sable_debugger_attached(void) {
    return IsDebuggerPresent() != FALSE;
}

// abort() would end the program with status 3, after offering it to
// Windows Error Reporting; a trap ends it as POSIX reports an abort.
_Noreturn static void stop_trapped(void) {
    fflush(stderr);
    _exit(134);
}

// ---- Stack overflow ------------------------------------------------------

// Recursion that runs off the end of a thread's stack raises
// EXCEPTION_STACK_OVERFLOW on that thread, with only the stack Windows
// keeps back for handling it: each thread asks for enough of that to write
// the message, and a vectored handler, which runs before any other, writes
// it and ends the program as a trap does.
static LONG CALLBACK on_exception(EXCEPTION_POINTERS *exception) {
    if (exception->ExceptionRecord->ExceptionCode != EXCEPTION_STACK_OVERFLOW) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // What the program printed before is its output, as before any trap.
    fflush(stdout);
    static const char message[] = "fatal error: stack overflow: calls nested too deeply\n";
    DWORD written;
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, sizeof message - 1, &written, NULL);
    TerminateProcess(GetCurrentProcess(), 134);
    return EXCEPTION_CONTINUE_SEARCH;
}

static void *watch_stack(void) {
    ULONG guarantee = 64 * 1024;
    SetThreadStackGuarantee(&guarantee);
    return NULL;
}

static void platform_start(void) {
    streams_start();
    watch_stack();
    AddVectoredExceptionHandler(1, on_exception);
}

// ---- Memory ----------------------------------------------------------------

// What VirtualAlloc gives starts on its allocation granularity, 64 KiB: a
// slab no larger is aligned to its size as it comes. A larger one is found
// by reserving twice the size, giving it back, and taking the aligned part
// of it — which another thread may take first, so it is tried again.
static void *map_slab(size_t size) {
    for (int attempt = 0; attempt < 16; attempt++) {
        char *reserved = VirtualAlloc(NULL, size * 2, MEM_RESERVE, PAGE_NOACCESS);
        if (reserved == NULL) {
            return NULL;
        }

        uintptr_t start = ((uintptr_t)reserved + size - 1) & ~(uintptr_t)(size - 1);
        VirtualFree(reserved, 0, MEM_RELEASE);
        void *slab = VirtualAlloc((void *)start, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (slab != NULL) {
            return slab;
        }
    }

    return NULL;
}

static void unmap_slab(void *slab, size_t size) {
    (void)size;
    VirtualFree(slab, 0, MEM_RELEASE);
}

// Kernel32's own name for it, which needs no psapi library to link.
static int64_t peak_memory(void) {
    PROCESS_MEMORY_COUNTERS counters;
    if (!K32GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof counters)) {
        return 0;
    }

    return (int64_t)counters.PeakWorkingSetSize;
}

// ---- Time (Sable.Time's floor) ------------------------------------------

int64_t sable_time_monotonic(void) {
    LARGE_INTEGER counter, frequency;
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    // In two parts, so that the product cannot overflow.
    int64_t seconds = counter.QuadPart / frequency.QuadPart;
    int64_t rest = counter.QuadPart % frequency.QuadPart;
    return seconds * 1000000000 + rest * 1000000000 / frequency.QuadPart;
}

// Windows counts 100 ns intervals from 1601; Unix time is from 1970.
int64_t sable_time_wall_clock(void) {
    FILETIME now;
    GetSystemTimePreciseAsFileTime(&now);
    int64_t ticks = (int64_t)(((uint64_t)now.dwHighDateTime << 32) | now.dwLowDateTime);
    return (ticks - 116444736000000000LL) * 100;
}

// At least as long as asked: Sleep counts in milliseconds and may wake a
// little early, so it sleeps again until the clock says the time is up.
void sable_time_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0) {
        return;
    }

    int64_t until = sable_time_monotonic() + nanoseconds;
    for (int64_t left = nanoseconds; left > 0; left = until - sable_time_monotonic()) {
        Sleep((DWORD)((left + 999999) / 1000000));
    }
}

// ---- Threads ---------------------------------------------------------------

static void mutex_init(PlatformMutex *mutex) {
    InitializeSRWLock(mutex);
}

static void mutex_lock(PlatformMutex *mutex) {
    AcquireSRWLockExclusive(mutex);
}

static void mutex_unlock(PlatformMutex *mutex) {
    ReleaseSRWLockExclusive(mutex);
}

static bool mutex_try_lock(PlatformMutex *mutex) {
    return TryAcquireSRWLockExclusive(mutex) != 0;
}

static void condition_init(PlatformCondition *condition) {
    InitializeConditionVariable(condition);
}

static void condition_wait(PlatformCondition *condition, PlatformMutex *mutex, int64_t until) {
    DWORD milliseconds = INFINITE;
    if (until >= 0) {
        int64_t left = until - sable_time_monotonic();
        milliseconds = left <= 0 ? 0 : (DWORD)((left + 999999) / 1000000);
    }

    SleepConditionVariableSRW(condition, mutex, milliseconds, 0);
}

static void condition_signal(PlatformCondition *condition) {
    WakeConditionVariable(condition);
}

static void condition_broadcast(PlatformCondition *condition) {
    WakeAllConditionVariable(condition);
}

typedef struct ThreadStart {
    void (*run)(void *argument);
    void *argument;
} ThreadStart;

static DWORD WINAPI thread_main(void *pointer) {
    ThreadStart start = *(ThreadStart *)pointer;
    free(pointer);
    watch_stack();
    start.run(start.argument);
    return 0;
}

static bool thread_start(void (*run)(void *argument), void *argument) {
    ThreadStart *start = malloc(sizeof *start);
    if (start == NULL) {
        return false;
    }

    start->run = run;
    start->argument = argument;
    // 8 MiB, as the main thread is linked with (PlatformLinkFlags).
    HANDLE thread = CreateThread(NULL, 8 * 1024 * 1024, thread_main, start, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (thread == NULL) {
        free(start);
        return false;
    }

    CloseHandle(thread);
    return true;
}

static int processor_count(void) {
    DWORD count = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    return count > 0 ? (int)count : 1;
}

static void thread_yield(void) {
    SwitchToThread();
}

// ---- The I/O reactor -------------------------------------------------------

// An I/O completion port. A socket's readiness comes to it as a completion
// through ProcessSocketNotifications (Windows 10 21H1 and later), edge-
// triggered and persistent, as kqueue's and epoll's come; a wake is a
// completion posted with no key.
static HANDLE reactor_port;

// Winsock, started once, before the first socket call of any kind: a
// socket is made and its address resolved before the reactor is opened to
// watch it, and without WSAStartup every call fails (WSANOTINITIALISED).
static INIT_ONCE sockets_once = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK sockets_start_once(PINIT_ONCE once, PVOID parameter, PVOID *context) {
    (void)once;
    (void)parameter;
    (void)context;
    WSADATA data;
    return WSAStartup(MAKEWORD(2, 2), &data) == 0;
}

static bool sockets_start(void) {
    return InitOnceExecuteOnce(&sockets_once, sockets_start_once, NULL, NULL) != FALSE;
}

static bool reactor_open(void) {
    if (!sockets_start()) {
        return false;
    }

    reactor_port = CreateIoCompletionPort(INVALID_HANDLE_VALUE, NULL, 0, 0);
    return reactor_port != NULL;
}

static bool reactor_notify(PlatformSocket socket, void *key, UINT8 operation) {
    SOCK_NOTIFY_REGISTRATION registration = { 0 };
    registration.socket = (SOCKET)socket;
    registration.completionKey = key;
    registration.eventFilter = SOCK_NOTIFY_REGISTER_EVENT_IN | SOCK_NOTIFY_REGISTER_EVENT_OUT | SOCK_NOTIFY_REGISTER_EVENT_HANGUP;
    registration.operation = operation;
    registration.triggerFlags = SOCK_NOTIFY_TRIGGER_EDGE | SOCK_NOTIFY_TRIGGER_PERSISTENT;
    return ProcessSocketNotifications(reactor_port, 1, &registration, 0, 0, NULL, NULL) == ERROR_SUCCESS
           && registration.registrationResult == ERROR_SUCCESS;
}

// What reactor_add posts for a socket ready as it is added, told from the
// notifications ProcessSocketNotifications posts by this OVERLAPPED: its
// events are REACTOR_*, in the bytes transferred.
static OVERLAPPED reactor_already_ready;

// epoll and kqueue report a socket that is ready already as it is added;
// an edge-triggered registration here is not documented to, and reports
// the socket's changes from when it is made. A connection to loopback is
// made before connect's caller has registered the socket, so its task —
// waiting to be told it is writable, with no call of its own to try first —
// would wait for an edge that has been and gone. So it is asked here, after
// the registration, what the socket is ready for already; an edge between
// the two is reported twice, which costs a call that finds it would block.
static bool reactor_add(PlatformSocket socket, void *key) {
    if (!reactor_notify(socket, key, SOCK_NOTIFY_OP_ENABLE)) {
        return false;
    }

    WSAPOLLFD now = { .fd = (SOCKET)socket, .events = POLLRDNORM | POLLWRNORM };
    if (WSAPoll(&now, 1, 0) == 1) {
        DWORD events = (now.revents & (POLLRDNORM | POLLHUP | POLLERR) ? REACTOR_READABLE : 0)
                       | (now.revents & (POLLWRNORM | POLLHUP | POLLERR) ? REACTOR_WRITABLE : 0);
        if (events != 0) {
            PostQueuedCompletionStatus(reactor_port, events, (ULONG_PTR)key, &reactor_already_ready);
        }
    }

    return true;
}

static void reactor_remove(PlatformSocket socket) {
    reactor_notify(socket, NULL, SOCK_NOTIFY_OP_REMOVE);
}

static int reactor_wait(ReactorEvent *events, int capacity, int64_t until) {
    OVERLAPPED_ENTRY ready[64];
    if (capacity > 64) {
        capacity = 64;
    }

    DWORD milliseconds = INFINITE;
    if (until >= 0) {
        int64_t left = until - sable_time_monotonic();
        milliseconds = left <= 0 ? 0 : (DWORD)((left + 999999) / 1000000);
    }

    ULONG count = 0;
    if (!GetQueuedCompletionStatusEx(reactor_port, ready, (ULONG)capacity, &count, milliseconds, FALSE)) {
        return 0;
    }

    int n = 0;
    for (ULONG i = 0; i < count; i++) {
        if (ready[i].lpCompletionKey == 0) {
            continue;
        }

        if (ready[i].lpOverlapped == &reactor_already_ready) {
            events[n].key = (void *)ready[i].lpCompletionKey;
            events[n].events = (int)ready[i].dwNumberOfBytesTransferred;
            n++;
            continue;
        }

        UINT32 what = SocketNotificationRetrieveEvents(&ready[i]);
        if (what & SOCK_NOTIFY_EVENT_REMOVE) {
            continue;
        }

        events[n].key = (void *)ready[i].lpCompletionKey;
        events[n].events = (what & (SOCK_NOTIFY_EVENT_IN | SOCK_NOTIFY_EVENT_HANGUP | SOCK_NOTIFY_EVENT_ERR) ? REACTOR_READABLE : 0)
                           | (what & (SOCK_NOTIFY_EVENT_OUT | SOCK_NOTIFY_EVENT_HANGUP | SOCK_NOTIFY_EVENT_ERR) ? REACTOR_WRITABLE : 0);
        n++;
    }

    return n;
}

static void reactor_wake(void) {
    PostQueuedCompletionStatus(reactor_port, 0, 0, NULL);
}

// ---- Sockets -----------------------------------------------------------------

static bool socket_prepare(SOCKET socket) {
    u_long one = 1;
    return ioctlsocket(socket, FIONBIO, &one) == 0;
}

static int64_t socket_status(void) {
    int error = WSAGetLastError();
    return error == WSAEWOULDBLOCK ? IO_WOULD_BLOCK : IO_FAILED;
}

static struct addrinfo *socket_resolve(const char *host, int port, bool passive) {
    if (!sockets_start()) {
        return NULL;
    }

    char service[16];
    snprintf(service, sizeof service, "%d", port);
    struct addrinfo hints = { 0 };
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = passive ? AI_PASSIVE : 0;
    struct addrinfo *found = NULL;
    return getaddrinfo(host[0] == '\0' ? NULL : host, service, &hints, &found) == 0 ? found : NULL;
}

static PlatformSocket socket_listen(const char *host, int port, int *bound) {
    struct addrinfo *found = socket_resolve(host, port, true);
    SOCKET handle = INVALID_SOCKET;
    for (struct addrinfo *at = found; at != NULL && handle == INVALID_SOCKET; at = at->ai_next) {
        handle = socket(at->ai_family, at->ai_socktype, at->ai_protocol);
        if (handle == INVALID_SOCKET) {
            continue;
        }

        if (!socket_prepare(handle) || bind(handle, at->ai_addr, (int)at->ai_addrlen) != 0 || listen(handle, SOMAXCONN) != 0) {
            closesocket(handle);
            handle = INVALID_SOCKET;
        }
    }

    if (found != NULL) {
        freeaddrinfo(found);
    }

    if (handle == INVALID_SOCKET) {
        return IO_FAILED;
    }

    struct sockaddr_storage address;
    int length = sizeof address;
    getsockname(handle, (struct sockaddr *)&address, &length);
    *bound = address.ss_family == AF_INET6 ? ntohs(((struct sockaddr_in6 *)&address)->sin6_port)
                                           : ntohs(((struct sockaddr_in *)&address)->sin_port);
    return (PlatformSocket)handle;
}

static PlatformSocket socket_accept(PlatformSocket listener) {
    SOCKET handle = accept((SOCKET)listener, NULL, NULL);
    if (handle == INVALID_SOCKET) {
        return (PlatformSocket)socket_status();
    }

    BOOL one = TRUE;
    if (!socket_prepare(handle)) {
        closesocket(handle);
        return IO_FAILED;
    }

    setsockopt(handle, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof one);
    return (PlatformSocket)handle;
}

static PlatformSocket socket_connect(const char *host, int port) {
    struct addrinfo *found = socket_resolve(host, port, false);
    SOCKET handle = INVALID_SOCKET;
    for (struct addrinfo *at = found; at != NULL && handle == INVALID_SOCKET; at = at->ai_next) {
        handle = socket(at->ai_family, at->ai_socktype, at->ai_protocol);
        if (handle == INVALID_SOCKET) {
            continue;
        }

        if (!socket_prepare(handle) || (connect(handle, at->ai_addr, (int)at->ai_addrlen) != 0 && WSAGetLastError() != WSAEWOULDBLOCK)) {
            closesocket(handle);
            handle = INVALID_SOCKET;
        }
    }

    if (found != NULL) {
        freeaddrinfo(found);
    }

    if (handle == INVALID_SOCKET) {
        return IO_FAILED;
    }

    BOOL one = TRUE;
    setsockopt(handle, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof one);
    return (PlatformSocket)handle;
}

static int socket_connected(PlatformSocket socket) {
    int error = 0;
    int length = sizeof error;
    if (getsockopt((SOCKET)socket, SOL_SOCKET, SO_ERROR, (char *)&error, &length) != 0 || error != 0) {
        return IO_FAILED;
    }

    struct sockaddr_storage peer;
    int peer_length = sizeof peer;
    return getpeername((SOCKET)socket, (struct sockaddr *)&peer, &peer_length) == 0 ? 0 : IO_FAILED;
}

static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size) {
    int got = recv((SOCKET)socket, bytes, size > INT32_MAX ? INT32_MAX : (int)size, 0);
    return got >= 0 ? got : socket_status();
}

static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size) {
    int sent = send((SOCKET)socket, bytes, size > INT32_MAX ? INT32_MAX : (int)size, 0);
    return sent >= 0 ? sent : socket_status();
}

static void socket_shutdown_write(PlatformSocket socket) {
    shutdown((SOCKET)socket, SD_SEND);
}

static void socket_close(PlatformSocket socket) {
    closesocket((SOCKET)socket);
}

// Windows' Unix sockets are streams alone.
static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length) {
    (void)name, (void)name_length, (void)bytes, (void)length;
    return 1;
}

// ---- Arguments, files and the environment ----------------------------------

// main() is given its arguments in the ANSI code page, which cannot hold
// every name: they are the command line's own, as UTF-16, made UTF-8 once.
static char **platform_arguments(int argc, char **argv, int *count) {
    (void)argc;
    (void)argv;
    int found = 0;
    wchar_t **wide = CommandLineToArgvW(GetCommandLineW(), &found);
    *count = wide == NULL ? 0 : found;
    char **arguments = calloc((size_t)*count + 1, sizeof(char *));
    for (int i = 0; i < *count; i++) {
        arguments[i] = from_wide(wide[i]);
    }

    LocalFree(wide);
    return arguments;
}

static FILE *open_file(const char *name, const char *mode) {
    wchar_t *wide_name = to_wide(name);
    wchar_t *wide_mode = to_wide(mode);
    FILE *file = _wfopen(wide_name, wide_mode);
    free(wide_name);
    free(wide_mode);
    return file;
}

static bool append_file(const char *name, const char *bytes, size_t length) {
    wchar_t *wide = to_wide(name);
    // With FILE_APPEND_DATA and no FILE_WRITE_DATA, every write goes at the
    // end of the file as it is then, however many processes write at once.
    HANDLE file = CreateFileW(wide, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    free(wide);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    DWORD written = 0;
    BOOL ok = length <= MAXDWORD && WriteFile(file, bytes, (DWORD)length, &written, NULL);
    CloseHandle(file);
    return ok && written == length;
}

static DWORD attributes_of(const char *name) {
    wchar_t *wide = to_wide(name);
    DWORD attributes = GetFileAttributesW(wide);
    free(wide);
    return attributes;
}

static bool file_exists(const char *name) {
    return attributes_of(name) != INVALID_FILE_ATTRIBUTES;
}

static bool is_directory(const char *name) {
    DWORD attributes = attributes_of(name);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

static bool remove_file(const char *name) {
    wchar_t *wide = to_wide(name);
    bool removed = DeleteFileW(wide) || RemoveDirectoryW(wide);
    free(wide);
    return removed;
}

static bool make_directory(const char *name) {
    wchar_t *wide = to_wide(name);
    bool made = CreateDirectoryW(wide, NULL) != FALSE;
    free(wide);
    return made;
}

static char **list_directory(const char *name, size_t *count) {
    size_t length = strlen(name);
    char *pattern = malloc(length + 3);
    if (pattern == NULL) {
        sable_trap("out of memory");
    }

    memcpy(pattern, name, length);
    if (length > 0 && pattern[length - 1] != '/' && pattern[length - 1] != '\\') {
        pattern[length++] = '\\';
    }

    pattern[length++] = '*';
    pattern[length] = '\0';
    wchar_t *wide = to_wide(pattern);
    free(pattern);
    WIN32_FIND_DATAW found;
    HANDLE search = FindFirstFileW(wide, &found);
    free(wide);
    // A directory has '.' at least: nothing at all is no directory.
    if (search == INVALID_HANDLE_VALUE) {
        return NULL;
    }

    size_t capacity = 16;
    char **names = malloc(capacity * sizeof(char *));
    do {
        if (wcscmp(found.cFileName, L".") == 0 || wcscmp(found.cFileName, L"..") == 0) {
            continue;
        }

        if (*count == capacity) {
            capacity *= 2;
            names = realloc(names, capacity * sizeof(char *));
        }

        names[(*count)++] = from_wide(found.cFileName);
    } while (FindNextFileW(search, &found));

    FindClose(search);
    return names;
}

static char *environment_variable(const char *name) {
    wchar_t *wide = to_wide(name);
    DWORD size = GetEnvironmentVariableW(wide, NULL, 0);
    wchar_t *value = size == 0 ? NULL : malloc((size_t)size * sizeof(wchar_t));
    if (value != NULL && GetEnvironmentVariableW(wide, value, size) >= size) {
        free(value);
        value = NULL;
    }

    free(wide);
    if (value == NULL) {
        return NULL;
    }

    char *result = from_wide(value);
    free(value);
    return result;
}

// ---- Processes (Sable.Process's floor) ----------------------------------

static char *current_directory(void) {
    DWORD size = GetCurrentDirectoryW(0, NULL);
    wchar_t *path = size == 0 ? NULL : malloc((size_t)size * sizeof(wchar_t));
    if (path == NULL || GetCurrentDirectoryW(size, path) == 0) {
        free(path);
        return NULL;
    }

    char *result = from_wide(path);
    free(path);
    return result;
}

static bool change_directory(const char *name) {
    wchar_t *wide = to_wide(name);
    bool changed = SetCurrentDirectoryW(wide) != FALSE;
    free(wide);
    return changed;
}

static int64_t process_id(void) {
    return (int64_t)GetCurrentProcessId();
}

// The executable's own path, as Windows knows it.
static char *executable_path(int argc, char **argv) {
    (void)argc;
    (void)argv;
    for (DWORD size = MAX_PATH;; size *= 2) {
        wchar_t *path = malloc((size_t)size * sizeof(wchar_t));
        DWORD length = GetModuleFileNameW(NULL, path, size);
        if (length == 0) {
            free(path);
            return NULL;
        }

        if (length < size) {
            char *result = from_wide(path);
            free(path);
            return result;
        }

        free(path);
    }
}

// ---- Randomness ----------------------------------------------------------

static bool random_bytes(void *buffer, size_t length) {
    // A call's length is 32 bits.
    PUCHAR next = buffer;
    while (length > 0) {
        ULONG part = length < 0x40000000 ? (ULONG)length : 0x40000000;
        if (BCryptGenRandom(NULL, next, part, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
            return false;
        }

        next += part;
        length -= part;
    }

    return true;
}

// ---- File handles ----------------------------------------------------------
//
// A file is a HANDLE, as an integer. Positional reads and writes give the
// offset in an OVERLAPPED, which a handle opened for synchronous use reads
// or writes at, moving its own position as it does (POSIX's pread leaves
// it): Sable.IO's positional calls say nothing of the position after.

// What GetLastError says went wrong, as IoError's kinds.
static int file_error_of(DWORD code) {
    switch (code) {
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND:
        case ERROR_INVALID_NAME:
            return FILE_ERROR_NOT_FOUND;
        case ERROR_ACCESS_DENIED:
        case ERROR_SHARING_VIOLATION:
        case ERROR_LOCK_VIOLATION:
            return FILE_ERROR_PERMISSION;
        case ERROR_FILE_EXISTS:
        case ERROR_ALREADY_EXISTS:
            return FILE_ERROR_EXISTS;
        case ERROR_DIRECTORY:
            return FILE_ERROR_IS_DIRECTORY;
        case ERROR_OPERATION_ABORTED:
            return FILE_ERROR_INTERRUPTED;
        case ERROR_INVALID_PARAMETER:
        case ERROR_INVALID_HANDLE:
            return FILE_ERROR_INVALID;
        default:
            return FILE_ERROR_OTHER;
    }
}

static void file_failed(FileError *error) {
    DWORD code = GetLastError();
    error->kind = file_error_of(code);
    error->code = (int64_t)code;
}

static PlatformFile file_open(const char *name, int flags, int permissions, FileError *error) {
    (void)permissions;
    DWORD access = 0;
    if (flags & FILE_READ) {
        access |= GENERIC_READ;
    }
    // Appending is FILE_APPEND_DATA without FILE_WRITE_DATA: every write
    // goes at the end of the file as it is then.
    if (flags & FILE_APPEND) {
        access |= FILE_APPEND_DATA | SYNCHRONIZE;
    } else if (flags & FILE_WRITE) {
        access |= GENERIC_WRITE;
    }

    DWORD disposition = OPEN_EXISTING;
    if (flags & FILE_CREATE_NEW) {
        disposition = CREATE_NEW;
    } else if ((flags & FILE_CREATE) && (flags & FILE_TRUNCATE)) {
        disposition = CREATE_ALWAYS;
    } else if (flags & FILE_CREATE) {
        disposition = OPEN_ALWAYS;
    } else if (flags & FILE_TRUNCATE) {
        disposition = TRUNCATE_EXISTING;
    }

    wchar_t *wide = to_wide(name);
    HANDLE file = CreateFileW(wide, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, disposition,
                              FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        file_failed(error);
        // A directory refuses access as a file: say what it is.
        DWORD attributes = GetFileAttributesW(wide);
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
            error->kind = FILE_ERROR_IS_DIRECTORY;
        }
        free(wide);
        return -1;
    }

    free(wide);
    return (PlatformFile)(intptr_t)file;
}

static HANDLE handle_of(PlatformFile file) {
    return (HANDLE)(intptr_t)file;
}

static int64_t file_read(PlatformFile file, void *into, int64_t most, int64_t offset, FileError *error) {
    DWORD wanted = most > 0x40000000 ? 0x40000000 : (DWORD)most;
    DWORD got = 0;
    OVERLAPPED at = { 0 };
    if (offset >= 0) {
        at.Offset = (DWORD)((uint64_t)offset & 0xFFFFFFFF);
        at.OffsetHigh = (DWORD)((uint64_t)offset >> 32);
    }

    if (!ReadFile(handle_of(file), into, wanted, &got, offset >= 0 ? &at : NULL)) {
        DWORD code = GetLastError();
        // The end of a file read at an offset, or of a pipe whose writer
        // has gone: nothing more, not a failure.
        if (code == ERROR_HANDLE_EOF || code == ERROR_BROKEN_PIPE) {
            return 0;
        }
        file_failed(error);
        return -1;
    }

    return (int64_t)got;
}

static int64_t file_write(PlatformFile file, const void *from, int64_t count, int64_t offset, FileError *error) {
    DWORD wanted = count > 0x40000000 ? 0x40000000 : (DWORD)count;
    DWORD put = 0;
    OVERLAPPED at = { 0 };
    if (offset >= 0) {
        at.Offset = (DWORD)((uint64_t)offset & 0xFFFFFFFF);
        at.OffsetHigh = (DWORD)((uint64_t)offset >> 32);
    }

    if (!WriteFile(handle_of(file), from, wanted, &put, offset >= 0 ? &at : NULL)) {
        file_failed(error);
        return -1;
    }

    return (int64_t)put;
}

static int64_t file_seek(PlatformFile file, int64_t offset, int whence, FileError *error) {
    LARGE_INTEGER distance, at;
    distance.QuadPart = offset;
    DWORD method = whence == 0 ? FILE_BEGIN : whence == 1 ? FILE_CURRENT : FILE_END;
    if (!SetFilePointerEx(handle_of(file), distance, &at, method)) {
        file_failed(error);
        return -1;
    }

    return at.QuadPart;
}

static bool file_sync(PlatformFile file, FileError *error) {
    if (!FlushFileBuffers(handle_of(file))) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool file_set_length(PlatformFile file, int64_t length, FileError *error) {
    FILE_END_OF_FILE_INFO end;
    end.EndOfFile.QuadPart = length;
    if (!SetFileInformationByHandle(handle_of(file), FileEndOfFileInfo, &end, sizeof end)) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool file_metadata(PlatformFile file, FileMetadata *metadata, FileError *error) {
    BY_HANDLE_FILE_INFORMATION info;
    if (!GetFileInformationByHandle(handle_of(file), &info)) {
        file_failed(error);
        return false;
    }

    metadata->size = (int64_t)(((uint64_t)info.nFileSizeHigh << 32) | info.nFileSizeLow);
    int64_t ticks = (int64_t)(((uint64_t)info.ftLastWriteTime.dwHighDateTime << 32) | info.ftLastWriteTime.dwLowDateTime);
    metadata->modified = (ticks - 116444736000000000LL) * 100;
    metadata->kind = (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
    return true;
}

static bool file_close(PlatformFile file, FileError *error) {
    if (!CloseHandle(handle_of(file))) {
        file_failed(error);
        return false;
    }

    return true;
}

static PlatformFile file_standard(int which) {
    DWORD id = which == 0 ? STD_INPUT_HANDLE : which == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE;
    return (PlatformFile)(intptr_t)GetStdHandle(id);
}

// The whole file copied by the system: CopyFileW, which copies the data and
// the attributes, replacing a file there.
static bool file_copy(const char *from, const char *to, FileError *error) {
    wchar_t *source = to_wide(from);
    wchar_t *target = to_wide(to);
    BOOL copied = CopyFileW(source, target, FALSE);
    if (!copied) {
        file_failed(error);
    }

    free(source);
    free(target);
    return copied != 0;
}

static char *file_canonical(const char *name, FileError *error) {
    wchar_t *wide = to_wide(name);
    // Opened for nothing but its name, a directory too (backup semantics).
    HANDLE handle = CreateFileW(wide, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE) {
        file_failed(error);
        return NULL;
    }

    DWORD length = GetFinalPathNameByHandleW(handle, NULL, 0, FILE_NAME_NORMALIZED);
    wchar_t *path = length == 0 ? NULL : malloc(((size_t)length + 1) * sizeof(wchar_t));
    if (path == NULL || GetFinalPathNameByHandleW(handle, path, length + 1, FILE_NAME_NORMALIZED) == 0) {
        file_failed(error);
        free(path);
        CloseHandle(handle);
        return NULL;
    }

    CloseHandle(handle);
    char *result = from_wide(path);
    free(path);
    return result;
}

// ---- Mapped files ----------------------------------------------------------

static void *file_map(PlatformFile file, int64_t length, int mode, FileError *error) {
    DWORD protection = mode == FILE_MAP_READ_WRITE ? PAGE_READWRITE : mode == FILE_MAP_COPY_ON_WRITE ? PAGE_WRITECOPY : PAGE_READONLY;
    DWORD access = mode == FILE_MAP_READ_WRITE ? FILE_MAP_WRITE : mode == FILE_MAP_COPY_ON_WRITE ? FILE_MAP_COPY : FILE_MAP_READ;
    HANDLE mapping = CreateFileMappingW(handle_of(file), NULL, protection, (DWORD)((uint64_t)length >> 32),
                                        (DWORD)((uint64_t)length & 0xFFFFFFFF), NULL);
    if (mapping == NULL) {
        file_failed(error);
        return NULL;
    }

    void *view = MapViewOfFile(mapping, access, 0, 0, (SIZE_T)length);
    if (view == NULL) {
        file_failed(error);
    }

    // The view keeps the mapping open.
    CloseHandle(mapping);
    return view;
}

static void file_unmap(void *base, int64_t length) {
    (void)length;
    UnmapViewOfFile(base);
}

static bool file_map_flush(void *base, int64_t at, int64_t length, FileError *error) {
    if (!FlushViewOfFile((char *)base + at, (SIZE_T)length)) {
        file_failed(error);
        return false;
    }

    return true;
}

static void file_map_advise(void *base, int64_t length, int advice) {
#if _WIN32_WINNT >= 0x0602
    if (advice == FILE_ADVISE_WILL_NEED) {
        WIN32_MEMORY_RANGE_ENTRY range = { base, (SIZE_T)length };
        (void)PrefetchVirtualMemory(GetCurrentProcess(), 1, &range, 0);
    }
#else
    (void)base;
    (void)length;
    (void)advice;
#endif
}

// ---- The environment, written ------------------------------------------------

static bool set_environment_variable(const char *name, const char *value) {
    wchar_t *wide_name = to_wide(name);
    wchar_t *wide_value = value == NULL ? NULL : to_wide(value);
    BOOL set = SetEnvironmentVariableW(wide_name, wide_value);
    free(wide_name);
    free(wide_value);
    // Removing one that is not there is no failure.
    return set || (value == NULL && GetLastError() == ERROR_ENVVAR_NOT_FOUND);
}

static char **environment_entries(size_t *count) {
    wchar_t *block = GetEnvironmentStringsW();
    size_t n = 0;
    for (wchar_t *at = block; at != NULL && *at != 0; at += wcslen(at) + 1) {
        n++;
    }

    char **entries = malloc((n > 0 ? n : 1) * sizeof(char *));
    if (entries == NULL) {
        sable_trap("out of memory");
    }

    size_t kept = 0;
    for (wchar_t *at = block; at != NULL && *at != 0; at += wcslen(at) + 1) {
        // "=C:=C:\\work" and the like: the shell's per-drive directories,
        // which are not variables.
        if (*at != L'=') {
            entries[kept++] = from_wide(at);
        }
    }

    if (block != NULL) {
        FreeEnvironmentStringsW(block);
    }

    *count = kept;
    return entries;
}

// ---- The system ----------------------------------------------------------

static char *host_name(void) {
    wchar_t name[256];
    DWORD size = 256;
    return GetComputerNameExW(ComputerNameDnsHostname, name, &size) ? from_wide(name) : NULL;
}

static char *user_name(void) {
    wchar_t name[257];
    DWORD size = 257;
    return GetUserNameW(name, &size) ? from_wide(name) : NULL;
}

static char *home_directory(void) {
    return environment_variable("USERPROFILE");
}

static char *temporary_directory(void) {
    wchar_t path[MAX_PATH + 2];
    DWORD length = GetTempPathW(MAX_PATH + 1, path);
    if (length == 0 || length > MAX_PATH + 1) {
        return NULL;
    }

    // It ends in '\\': the directory, without it.
    if (length > 3 && path[length - 1] == L'\\') {
        path[length - 1] = 0;
    }

    return from_wide(path);
}

typedef LONG(WINAPI *RtlGetVersionCall)(PRTL_OSVERSIONINFOW);

static char *os_version(void) {
    // RtlGetVersion, which is not capped at the version the program's
    // manifest declares, as GetVersionEx is.
    RTL_OSVERSIONINFOW info = { sizeof info };
    RtlGetVersionCall call = (RtlGetVersionCall)(void *)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion");
    if (call == NULL || call(&info) != 0) {
        return NULL;
    }

    char text[64];
    snprintf(text, sizeof text, "%lu.%lu.%lu", info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber);
    return strdup(text);
}

static int64_t page_size(void) {
    SYSTEM_INFO info;
    GetSystemInfo(&info);
    return info.dwPageSize;
}

static int64_t physical_memory(void) {
    MEMORYSTATUSEX status = { sizeof status };
    return GlobalMemoryStatusEx(&status) ? (int64_t)status.ullTotalPhys : 0;
}

// ---- Starting programs, and signals ----------------------------------------

#include "platform_process_win32.c"
