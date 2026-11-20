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

// A frame as 'sable.Module.function + offset' where the symbol is known, its
// address otherwise, named from the program's PDB, which the link always
// writes. The return address is looked up one byte back, as on POSIX.
static void name_frame(void *frame, char *text, size_t size) {
    static bool started, symbols;
    HANDLE process = GetCurrentProcess();
    if (!started) {
        started = true;
        SymSetOptions(SYMOPT_DEFERRED_LOADS);
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
    if (symbols && SymFromAddr(process, (DWORD64)(uintptr_t)at, &offset, &symbol.info)) {
        snprintf(text, size, "%s + %lld", symbol.info.Name, (long long)offset + 1);
    } else {
        snprintf(text, size, "%p", frame);
    }
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

static bool reactor_add(PlatformSocket socket, void *key) {
    return reactor_notify(socket, key, SOCK_NOTIFY_OP_ENABLE);
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

static bool is_file(const wchar_t *name) {
    DWORD attributes = GetFileAttributesW(name);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

// 'name' as it is, or with '.exe' or '.com' after it: the first that is a file.
static wchar_t *with_extension(const wchar_t *name) {
    static const wchar_t *const extensions[] = { L"", L".exe", L".com" };
    size_t length = wcslen(name);
    for (size_t i = 0; i < sizeof extensions / sizeof *extensions; i++) {
        size_t extra = wcslen(extensions[i]);
        wchar_t *candidate = malloc((length + extra + 1) * sizeof(wchar_t));
        memcpy(candidate, name, length * sizeof(wchar_t));
        memcpy(candidate + length, extensions[i], (extra + 1) * sizeof(wchar_t));
        if (is_file(candidate)) {
            return candidate;
        }

        free(candidate);
    }

    return NULL;
}

// The program's path: the name itself when it holds a path, else the first
// program of that name on PATH — as POSIX finds one, but with the extension
// Windows gives a program, which the name may leave out. NULL when there is
// none.
static wchar_t *find_program(const char *name) {
    wchar_t *wide = to_wide(name);
    if (strpbrk(name, "/\\:") != NULL) {
        wchar_t *found = with_extension(wide);
        free(wide);
        return found;
    }

    DWORD size = GetEnvironmentVariableW(L"PATH", NULL, 0);
    if (size == 0 || *name == '\0') {
        free(wide);
        return NULL;
    }

    wchar_t *path = malloc((size_t)size * sizeof(wchar_t));
    GetEnvironmentVariableW(L"PATH", path, size);
    size_t name_length = wcslen(wide);
    wchar_t *found = NULL;
    for (wchar_t *entry = path; found == NULL && *entry != L'\0';) {
        wchar_t *end = wcschr(entry, L';');
        size_t length = end == NULL ? wcslen(entry) : (size_t)(end - entry);
        if (length > 0) {
            wchar_t *candidate = malloc((length + name_length + 2) * sizeof(wchar_t));
            memcpy(candidate, entry, length * sizeof(wchar_t));
            candidate[length] = L'\\';
            memcpy(candidate + length + 1, wide, (name_length + 1) * sizeof(wchar_t));
            found = with_extension(candidate);
            free(candidate);
        }

        if (end == NULL) {
            break;
        }

        entry = end + 1;
    }

    free(path);
    free(wide);
    return found;
}

// One argument on a command line, quoted as the C runtime (and
// CommandLineToArgvW) reads it back: backslashes are literal except before
// a quote, where they are doubled.
static void add_argument(Buffer *line, const char *argument) {
    if (line->length > 0) {
        buffer_add(line, " ", 1);
    }

    if (*argument != '\0' && strpbrk(argument, " \t\n\v\"") == NULL) {
        buffer_add(line, argument, strlen(argument));
        return;
    }

    buffer_add(line, "\"", 1);
    for (const char *p = argument;; p++) {
        size_t backslashes = 0;
        while (*p == '\\') {
            backslashes++;
            p++;
        }

        if (*p == '\0') {
            for (size_t i = 0; i < backslashes * 2; i++) {
                buffer_add(line, "\\", 1);
            }

            break;
        }

        for (size_t i = 0; i < (*p == '"' ? backslashes * 2 + 1 : backslashes); i++) {
            buffer_add(line, "\\", 1);
        }

        buffer_add(line, p, 1);
    }

    buffer_add(line, "\"", 1);
}

// A pipe read to its end by a thread of its own.
typedef struct {
    HANDLE pipe;
    Buffer *buffer;
} PipeReader;

static DWORD WINAPI read_pipe(void *argument) {
    PipeReader *reader = argument;
    char chunk[4096];
    DWORD got;
    while (ReadFile(reader->pipe, chunk, sizeof chunk, &got, NULL) && got > 0) {
        buffer_add(reader->buffer, chunk, got);
    }

    return 0;
}

// A pipe written, all of it or until the program stops reading, and closed.
typedef struct {
    HANDLE pipe;
    const char *bytes;
    int64_t length;
} PipeWriter;

static DWORD WINAPI write_pipe(void *argument) {
    PipeWriter *writer = argument;
    int64_t written = 0;
    while (written < writer->length) {
        DWORD chunk = writer->length - written > (1 << 20) ? (1 << 20) : (DWORD)(writer->length - written);
        DWORD sent;
        if (!WriteFile(writer->pipe, writer->bytes + written, chunk, &sent, NULL)) {
            break;
        }

        written += sent;
    }

    CloseHandle(writer->pipe);
    return 0;
}

// A pipe whose one end the program started inherits, and the other not.
static bool make_pipe(HANDLE *read, HANDLE *write, bool child_reads) {
    SECURITY_ATTRIBUTES inherited = { sizeof inherited, NULL, TRUE };
    if (!CreatePipe(read, write, &inherited, 0)) {
        return false;
    }

    SetHandleInformation(child_reads ? *write : *read, HANDLE_FLAG_INHERIT, 0);
    return true;
}

// Runs the program to its end: the input written to it and its output and
// errors read as they come, each by a thread of its own, so that neither
// side waits on a full pipe.
static bool run_program(char **argv, const char *input, int64_t input_length, Buffer *output, Buffer *errors, int64_t *status) {
    wchar_t *found = find_program(argv[0]);
    if (found == NULL) {
        return false;
    }

    Buffer line = { 0 };
    for (char **argument = argv; *argument != NULL; argument++) {
        add_argument(&line, *argument);
    }

    buffer_add(&line, "", 1);
    wchar_t *command = to_wide(line.bytes);
    free(line.bytes);

    HANDLE in_read = NULL, in_write = NULL, out_read = NULL, out_write = NULL, err_read = NULL, err_write = NULL;
    bool piped = make_pipe(&in_read, &in_write, true);
    piped = piped && make_pipe(&out_read, &out_write, false);
    piped = piped && make_pipe(&err_read, &err_write, false);

    PROCESS_INFORMATION child = { 0 };
    bool spawned = false;
    if (piped) {
        STARTUPINFOW startup = { 0 };
        startup.cb = sizeof startup;
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = in_read;
        startup.hStdOutput = out_write;
        startup.hStdError = err_write;
        fflush(stdout);
        fflush(stderr);
        spawned = CreateProcessW(found, command, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &child) != FALSE;
    }

    free(command);
    free(found);
    HANDLE child_ends[] = { in_read, out_write, err_write };
    for (int i = 0; i < 3; i++) {
        if (child_ends[i] != NULL) {
            CloseHandle(child_ends[i]);
        }
    }

    if (!spawned) {
        HANDLE own_ends[] = { in_write, out_read, err_read };
        for (int i = 0; i < 3; i++) {
            if (own_ends[i] != NULL) {
                CloseHandle(own_ends[i]);
            }
        }

        return false;
    }

    PipeWriter writer = { in_write, input, input_length };
    PipeReader reading_errors = { err_read, errors };
    PipeReader reading_output = { out_read, output };
    HANDLE threads[2];
    threads[0] = CreateThread(NULL, 0, write_pipe, &writer, 0, NULL);
    if (threads[0] == NULL) {
        write_pipe(&writer);
    }

    threads[1] = CreateThread(NULL, 0, read_pipe, &reading_errors, 0, NULL);
    read_pipe(&reading_output);
    if (threads[1] == NULL) {
        read_pipe(&reading_errors);
    }

    for (int i = 0; i < 2; i++) {
        if (threads[i] != NULL) {
            WaitForSingleObject(threads[i], INFINITE);
            CloseHandle(threads[i]);
        }
    }

    CloseHandle(out_read);
    CloseHandle(err_read);
    WaitForSingleObject(child.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(child.hProcess, &code);
    CloseHandle(child.hProcess);
    CloseHandle(child.hThread);
    *status = (int64_t)code;
    return true;
}

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
    return BCryptGenRandom(NULL, (PUCHAR)buffer, (ULONG)length, BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0;
}
