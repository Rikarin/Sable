// The Sable runtime's platform layer for Windows, with the Microsoft C
// runtime: what platform.h says each platform gives the rest.
//
// Every name Windows takes or gives is UTF-16, and the runtime's are UTF-8:
// they are converted here, at the boundary, so that a path, an argument or
// a variable holds any character on Windows as it does elsewhere.

#include <winsock2.h>
#include <ws2tcpip.h>
#include <afunix.h>
#include <iphlpapi.h>
#include <bcrypt.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <fcntl.h>
#include <io.h>
#include <shellapi.h>
#include <psapi.h>

#include "wide.h"

#pragma comment(lib, "advapi32")
#pragma comment(lib, "bcrypt")
#pragma comment(lib, "dbghelp")
#pragma comment(lib, "iphlpapi")
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

// Windows' wide text as the runtime has it, WTF-8 (wide.h): well-formed UTF-16
// is UTF-8, and any other comes back as it was. In memory the caller frees.
static wchar_t *to_wide(const char *bytes) {
    wchar_t *wide = malloc(wtf8_to_wide(bytes, NULL) * sizeof(wchar_t));
    if (wide == NULL) {
        sable_trap("out of memory");
    }

    wtf8_to_wide(bytes, (uint16_t *)wide);
    return wide;
}

// A path as the system's functions take it. One of more than 260 units, or of
// a directory of more than 248, they take only as written ('\\\\?\\'), where '/',
// '.' and '..' are names like any others: so it is made whole first.
static wchar_t *to_wide_path(const char *bytes) {
    wchar_t *wide = to_wide(bytes);
    if (wcslen(wide) < 240 || wcsncmp(wide, L"\\\\?\\", 4) == 0 || wcsncmp(wide, L"\\\\.\\", 4) == 0) {
        return wide;
    }

    DWORD room = GetFullPathNameW(wide, 0, NULL, NULL);
    wchar_t *whole = room == 0 ? NULL : malloc(((size_t)room + 8) * sizeof(wchar_t));
    if (whole == NULL) {
        return wide;
    }

    // Room for the longer of the two prefixes before it.
    wchar_t *full = whole + 8;
    DWORD length = GetFullPathNameW(wide, room, full, NULL);
    if (length == 0 || length >= room) {
        free(whole);
        return wide;
    }

    free(wide);
    bool share = wcsncmp(full, L"\\\\", 2) == 0;
    const wchar_t *prefix = share ? L"\\\\?\\UNC\\" : L"\\\\?\\";
    const wchar_t *rest = share ? full + 2 : full;
    size_t before = wcslen(prefix);
    memmove(whole + before, rest, (wcslen(rest) + 1) * sizeof(wchar_t));
    memcpy(whole, prefix, before * sizeof(wchar_t));
    return whole;
}

static char *from_wide(const wchar_t *wide) {
    char *bytes = malloc(wide_to_wtf8((const uint16_t *)wide, NULL));
    if (bytes == NULL) {
        sable_trap("out of memory");
    }

    wide_to_wtf8((const uint16_t *)wide, bytes);
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

// ---- Every thread's frames ---------------------------------------------------

// The thread is stopped for as long as its stack is walked, by the unwind
// tables every function has. Nothing is asked of the heap meanwhile, as the
// thread may hold the heap's lock.
static int stopped_frames(HANDLE thread, void **frames, int capacity) {
    CONTEXT context;
    memset(&context, 0, sizeof context);
    context.ContextFlags = CONTEXT_FULL;
    if (SuspendThread(thread) == (DWORD)-1) {
        return 0;
    }

    int count = 0;
    if (GetThreadContext(thread, &context)) {
        while (count < capacity) {
#if defined(_M_ARM64) || defined(__aarch64__)
            DWORD64 at = context.Pc;
#else
            DWORD64 at = context.Rip;
#endif
            if (at == 0) {
                break;
            }

            frames[count++] = (void *)(uintptr_t)at;
            DWORD64 image = 0;
            PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(at, &image, NULL);
            if (function == NULL) {
                // A leaf, which keeps nothing: its return address is where a
                // call leaves it.
#if defined(_M_ARM64) || defined(__aarch64__)
                if (context.Pc == context.Lr) {
                    break;
                }

                context.Pc = context.Lr;
#else
                context.Rip = *(DWORD64 *)(uintptr_t)context.Rsp;
                context.Rsp += 8;
#endif
                continue;
            }

            void *handler = NULL;
            DWORD64 establisher = 0;
            RtlVirtualUnwind(UNW_FLAG_NHANDLER, image, at, function, &context, &handler, &establisher, NULL);
        }
    }

    ResumeThread(thread);
    return count;
}

static void each_thread_frames(void (*told)(void **frames, int count)) {
    HANDLE threads = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (threads == INVALID_HANDLE_VALUE) {
        return;
    }

    THREADENTRY32 entry;
    entry.dwSize = sizeof entry;
    for (BOOL more = Thread32First(threads, &entry); more; more = Thread32Next(threads, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId() || entry.th32ThreadID == GetCurrentThreadId()) {
            continue;
        }

        HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, entry.th32ThreadID);
        if (thread != NULL) {
            void *frames[BACKTRACE_DEPTH];
            int count = stopped_frames(thread, frames, BACKTRACE_DEPTH);
            CloseHandle(thread);
            if (count > 0) {
                told(frames, count);
            }
        }
    }

    CloseHandle(threads);
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
    terminal_restore();
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

// Windows keeps no sum of the processes waited for.
static int64_t processor_time(bool children) {
    FILETIME created, exited, kernel, user;
    if (children) {
        return 0;
    }

    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) {
        return -1;
    }

    return (int64_t)((((uint64_t)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime)
        + (((uint64_t)user.dwHighDateTime << 32) | user.dwLowDateTime)) * 100;
}

// Cycles are counted for every process; instructions only with privileges.
static int64_t hardware_counter(bool cycles) {
    ULONG64 count = 0;
    return cycles && QueryProcessCycleTime(GetCurrentProcess(), &count) ? (int64_t)count : -1;
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

// Interrupt time without what the machine slept, in 100 ns units: as fine
// as the system's clock tick (0.5 to 15.6 ms).
int64_t sable_time_suspending(void) {
    ULONGLONG ticks = 0;
    QueryUnbiasedInterruptTime(&ticks);
    return (int64_t)ticks * 100;
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

static bool mutex_looks_free(PlatformMutex *mutex) {
    (void)mutex;
    return true;
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

static bool thread_start_sized(void (*run)(void *argument), void *argument, size_t stack) {
    ThreadStart *start = malloc(sizeof *start);
    if (start == NULL) {
        return false;
    }

    start->run = run;
    start->argument = argument;
    // Room for a handler's frames, and the guarantee (watch_stack), at least.
    enum { LEAST = 256 * 1024 };
    HANDLE thread = CreateThread(NULL, stack < LEAST ? LEAST : stack, thread_main, start, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (thread == NULL) {
        free(start);
        return false;
    }

    CloseHandle(thread);
    return true;
}

// 8 MiB, as the main thread is linked with (PlatformLinkFlags).
static bool thread_start(void (*run)(void *argument), void *argument) {
    return thread_start_sized(run, argument, THREAD_STACK);
}

// SetThreadDescription is Windows 10's, from 1607 on: asked for, not linked.
static void thread_set_name(const char *name) {
    typedef HRESULT(WINAPI * Describe)(HANDLE, PCWSTR);
    HMODULE kernel = GetModuleHandleW(L"kernelbase.dll");
    Describe describe = kernel != NULL ? (Describe)(void *)GetProcAddress(kernel, "SetThreadDescription") : NULL;
    wchar_t wide[256];
    if (describe != NULL && MultiByteToWideChar(CP_UTF8, 0, name, -1, wide, 256) > 0) {
        describe(GetCurrentThread(), wide);
    }
}

static void thread_set_priority(int priority) {
    SetThreadPriority(GetCurrentThread(), priority);
}

// Those the process is bound to, where it is of one group of processors.
static int processor_count(void) {
    DWORD count = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    DWORD_PTR mine = 0, all = 0;
    if (count <= 64 && GetProcessAffinityMask(GetCurrentProcess(), &mine, &all) && mine != 0) {
        count = 0;
        for (; mine != 0; mine &= mine - 1) {
            count++;
        }
    }

    return count > 0 ? (int)count : 1;
}

static void thread_yield(void) {
    SwitchToThread();
}

// The calling thread's value, given to foreign_thread_end (foreign.c) as a
// fiber-local one's clean-up, which a thread that ends runs.
static void foreign_thread_end(void *pointer);

static void WINAPI foreign_fiber_end(void *value) {
    if (value != NULL) {
        foreign_thread_end(value);
    }
}

static void platform_at_thread_exit(void *value) {
    static DWORD slot = FLS_OUT_OF_INDEXES;
    static SRWLOCK lock = SRWLOCK_INIT;
    AcquireSRWLockExclusive(&lock);
    if (slot == FLS_OUT_OF_INDEXES) {
        slot = FlsAlloc(foreign_fiber_end);
    }

    ReleaseSRWLockExclusive(&lock);
    if (slot == FLS_OUT_OF_INDEXES || !FlsSetValue(slot, value)) {
        sable_trap("no thread slot is left");
    }
}

// ---- Libraries ---------------------------------------------------------------

static char *windows_error(DWORD code) {
    wchar_t *text = NULL;
    DWORD length = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        NULL, code, 0, (wchar_t *)&text, 0, NULL);
    while (length > 0 && (text[length - 1] == L'\n' || text[length - 1] == L'\r' || text[length - 1] == L' ')) {
        text[--length] = L'\0';
    }

    if (length == 0) {
        char number[48];
        snprintf(number, sizeof number, "the system's error %lu", (unsigned long)code);
        return strdup(number);
    }

    char *message = from_wide(text);
    LocalFree(text);
    return message;
}

// The program, as a library: its symbols are looked for in every module loaded.
static char program_library;

static void *library_open(const char *path, int flags, char **message) {
    (void)flags; // every module's symbols are its own
    if (path == NULL) {
        return &program_library;
    }

    wchar_t *wide = to_wide(path);
    // A path's own directory is where what it needs is looked for first;
    // a name is looked for where a program's libraries are, and not in the
    // working directory.
    bool is_path = wcspbrk(wide, L"\\/") != NULL;
    DWORD mode = 0;
    SetThreadErrorMode(SEM_FAILCRITICALERRORS, &mode);
    HMODULE library;
    DWORD code = 0;
    if (is_path) {
        // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR is for an absolute path alone.
        wchar_t *full = _wfullpath(NULL, wide, 0);
        library = LoadLibraryExW(full != NULL ? full : wide, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        code = GetLastError();
        free(full);
    } else {
        library = LoadLibraryExW(wide, NULL, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        code = GetLastError();
    }

    SetThreadErrorMode(mode, NULL);
    free(wide);
    if (library == NULL) {
        *message = windows_error(code);
    }

    return library;
}

static bool library_symbol(void *library, const char *name, void **address, char **message) {
    *address = NULL;
    DWORD code = ERROR_PROC_NOT_FOUND;
    if (library != &program_library) {
        *address = (void *)GetProcAddress(library, name);
        code = GetLastError();
    } else {
        HMODULE modules[1024];
        DWORD size = 0;
        if (EnumProcessModules(GetCurrentProcess(), modules, sizeof modules, &size)) {
            DWORD count = size / sizeof modules[0];
            for (DWORD i = 0; i < count && i < 1024 && *address == NULL; i++) {
                *address = (void *)GetProcAddress(modules[i], name);
            }
        }
    }

    if (*address == NULL) {
        *message = windows_error(code);
    }

    return *address != NULL;
}

static void library_close(void *library) {
    if (library != &program_library) {
        FreeLibrary(library);
    }
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

static int socket_error_kind(int code) {
    switch (code) {
        case WSAECONNRESET: case WSAENOTCONN: case WSAECONNABORTED: case WSAESHUTDOWN: case WSAENETRESET: return NET_ERROR_DISCONNECTED;
        case WSAECONNREFUSED: return NET_ERROR_REFUSED;
        case WSAETIMEDOUT: return NET_ERROR_TIMED_OUT;
        case WSAENETUNREACH: case WSAEHOSTUNREACH: case WSAENETDOWN: case WSAEHOSTDOWN: case WSAEADDRNOTAVAIL: return NET_ERROR_UNREACHABLE;
        case WSAEADDRINUSE: return NET_ERROR_IN_USE;
        // Another's socket has the address to itself (SO_EXCLUSIVEADDRUSE),
        // or the program may not have it: Windows says the same of both.
        case WSAEACCES: return NET_ERROR_PERMISSION;
        case WSAEMFILE: case WSAENOBUFS: case WSA_NOT_ENOUGH_MEMORY: return NET_ERROR_TRY_LATER;
        case WSAEINVAL: case WSAEFAULT: case WSAENAMETOOLONG: case WSAEDESTADDRREQ: case WSAEISCONN: return NET_ERROR_INVALID;
        case WSAEMSGSIZE: return NET_ERROR_TOO_LONG;
        case WSAEOPNOTSUPP: case WSAENOPROTOOPT: case WSAEAFNOSUPPORT: case WSAEPROTONOSUPPORT: case WSAEPROTOTYPE: case WSAESOCKTNOSUPPORT:
            return NET_ERROR_UNSUPPORTED;
        default: return NET_ERROR_OTHER;
    }
}

static int socket_fail(void) {
    net_last_failure.code = WSAGetLastError();
    net_last_failure.kind = socket_error_kind((int)net_last_failure.code);
    return IO_FAILED;
}

static int socket_fail_as(int kind) {
    net_last_failure.code = 0;
    net_last_failure.kind = kind;
    return IO_FAILED;
}

// A call that failed: it would block, or why it did.
static int64_t socket_status(void) {
    return WSAGetLastError() == WSAEWOULDBLOCK ? IO_WOULD_BLOCK : socket_fail();
}

static bool socket_prepare(SOCKET socket) {
    u_long one = 1;
    // No program started inherits it.
    SetHandleInformation((HANDLE)socket, HANDLE_FLAG_INHERIT, 0);
    return ioctlsocket(socket, FIONBIO, &one) == 0;
}

// The address as the system takes it: its length, or 0 for one it cannot.
static int socket_address(const PlatformAddress *address, struct sockaddr_storage *to) {
    memset(to, 0, sizeof *to);
    if (address->family == NET_IPV4) {
        struct sockaddr_in *at = (struct sockaddr_in *)to;
        at->sin_family = AF_INET;
        at->sin_port = htons((u_short)address->port);
        at->sin_addr.s_addr = htonl((u_long)address->low);
        return sizeof *at;
    }

    if (address->family == NET_IPV6) {
        struct sockaddr_in6 *at = (struct sockaddr_in6 *)to;
        at->sin6_family = AF_INET6;
        at->sin6_port = htons((u_short)address->port);
        at->sin6_scope_id = address->scope;
        for (int i = 0; i < 8; i++) {
            at->sin6_addr.s6_addr[i] = (uint8_t)(address->high >> (56 - 8 * i));
            at->sin6_addr.s6_addr[8 + i] = (uint8_t)(address->low >> (56 - 8 * i));
        }

        return sizeof *at;
    }

    struct sockaddr_un *at = (struct sockaddr_un *)to;
    at->sun_family = AF_UNIX;
    if (address->path_length == 0 || address->path_length >= sizeof at->sun_path
        || memchr(address->path, '\0', address->path_length) != NULL) {
        return 0;
    }

    memcpy(at->sun_path, address->path, address->path_length);
    return (int)(offsetof(struct sockaddr_un, sun_path) + address->path_length + 1);
}

static void socket_address_from(const struct sockaddr_storage *from, int length, PlatformAddress *address) {
    memset(address, 0, sizeof *address);
    if (length >= (int)sizeof(struct sockaddr_in) && from->ss_family == AF_INET) {
        const struct sockaddr_in *at = (const struct sockaddr_in *)from;
        address->family = NET_IPV4;
        address->port = ntohs(at->sin_port);
        address->low = ntohl(at->sin_addr.s_addr);
    } else if (length >= (int)sizeof(struct sockaddr_in6) && from->ss_family == AF_INET6) {
        const struct sockaddr_in6 *at = (const struct sockaddr_in6 *)from;
        address->family = NET_IPV6;
        address->port = ntohs(at->sin6_port);
        address->scope = at->sin6_scope_id;
        for (int i = 0; i < 8; i++) {
            address->high = address->high << 8 | at->sin6_addr.s6_addr[i];
            address->low = address->low << 8 | at->sin6_addr.s6_addr[8 + i];
        }
    } else if (length >= (int)offsetof(struct sockaddr_un, sun_path) && from->ss_family == AF_UNIX) {
        const struct sockaddr_un *at = (const struct sockaddr_un *)from;
        size_t most = (size_t)length - offsetof(struct sockaddr_un, sun_path);
        address->family = NET_UNIX;
        address->path_length = strnlen(at->sun_path, most > sizeof at->sun_path ? sizeof at->sun_path : most);
        memcpy(address->path, at->sun_path, address->path_length);
    }
}

// Whether a reparse point with the tag (IO_REPARSE_TAG_AF_UNIX, which older
// headers have not) is a Unix domain socket's file: no link.
static bool is_socket_tag(DWORD tag) {
    return tag == 0x80000023u;
}

static SOCKET socket_open(const PlatformAddress *address, int kind) {
    if (!sockets_start()) {
        return INVALID_SOCKET;
    }

    if (address->family == NET_UNIX && kind != SOCK_STREAM) {
        WSASetLastError(WSAESOCKTNOSUPPORT);
        return INVALID_SOCKET;
    }

    SOCKET handle = socket(address->family == NET_IPV4 ? AF_INET : address->family == NET_IPV6 ? AF_INET6 : AF_UNIX, kind, 0);
    if (handle != INVALID_SOCKET && !socket_prepare(handle)) {
        int code = WSAGetLastError();
        closesocket(handle);
        WSASetLastError(code);
        return INVALID_SOCKET;
    }

    return handle;
}

static int socket_give_up(SOCKET handle) {
    int code = WSAGetLastError();
    closesocket(handle);
    WSASetLastError(code);
    return socket_fail();
}

static bool socket_set(SOCKET handle, int level, int name, int value) {
    return setsockopt(handle, level, name, (const char *)&value, sizeof value) == 0;
}

// SO_REUSEADDR here lets a second socket take a port another listens on,
// whoever's it is, so it is only for datagram sockets that share one, and
// every other socket has its port to itself.
static bool socket_before_bind(SOCKET handle, const PlatformAddress *address, int flags) {
    if (flags & NET_REUSE_PORT) {
        WSASetLastError(WSAENOPROTOOPT);
        return false;
    }

    if (address->family == NET_UNIX) {
        return true;
    }

    return socket_set(handle, SOL_SOCKET, flags & NET_REUSE_ADDRESS ? SO_REUSEADDR : SO_EXCLUSIVEADDRUSE, 1)
           && (address->family != NET_IPV6 || socket_set(handle, IPPROTO_IPV6, IPV6_V6ONLY, !(flags & NET_DUAL_STACK)));
}

static bool socket_bind(SOCKET handle, const PlatformAddress *address, int flags) {
    struct sockaddr_storage at;
    int length = socket_address(address, &at);
    if (length == 0) {
        WSASetLastError(WSAEINVAL);
        return false;
    }

    if (address->family == NET_UNIX && (flags & NET_REPLACE)) {
        char *name = ((struct sockaddr_un *)&at)->sun_path;
        wchar_t *wide = to_wide(name);
        WIN32_FIND_DATAW found;
        HANDLE search = FindFirstFileW(wide, &found);
        if (search != INVALID_HANDLE_VALUE) {
            FindClose(search);
            if ((found.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) && is_socket_tag(found.dwReserved0)) {
                DeleteFileW(wide);
            }
        }

        free(wide);
    }

    return bind(handle, (struct sockaddr *)&at, length) == 0;
}

static PlatformSocket socket_listen(const PlatformAddress *address, int backlog, int flags) {
    SOCKET handle = socket_open(address, SOCK_STREAM);
    if (handle == INVALID_SOCKET) {
        return socket_fail();
    }

    if (!socket_before_bind(handle, address, flags & ~NET_REUSE_ADDRESS) || !socket_bind(handle, address, flags) || listen(handle, backlog) != 0) {
        return socket_give_up(handle);
    }

    return (PlatformSocket)handle;
}

static PlatformSocket socket_accept(PlatformSocket listener, PlatformAddress *peer) {
    for (;;) {
        struct sockaddr_storage from;
        int length = sizeof from;
        SOCKET handle = accept((SOCKET)listener, (struct sockaddr *)&from, &length);
        if (handle != INVALID_SOCKET) {
            if (!socket_prepare(handle)) {
                closesocket(handle);
                continue;
            }

            if (from.ss_family != AF_UNIX) {
                socket_set(handle, IPPROTO_TCP, TCP_NODELAY, 1);
            }

            socket_address_from(&from, length, peer);
            return (PlatformSocket)handle;
        }

        // A connection that went before it was accepted: the next one's turn.
        if (WSAGetLastError() != WSAECONNRESET && WSAGetLastError() != WSAECONNABORTED) {
            return (PlatformSocket)socket_status();
        }
    }
}

static PlatformSocket socket_connect(const PlatformAddress *address) {
    struct sockaddr_storage to;
    int length = socket_address(address, &to);
    if (length == 0) {
        return socket_fail_as(NET_ERROR_INVALID);
    }

    if (address->family == NET_UNIX) {
        // Windows says of a path where nothing is that it refused.
        wchar_t *wide = to_wide(((struct sockaddr_un *)&to)->sun_path);
        bool missing = GetFileAttributesW(wide) == INVALID_FILE_ATTRIBUTES
                       && (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND);
        free(wide);
        if (missing) {
            return socket_fail_as(NET_ERROR_NOT_FOUND);
        }
    }

    SOCKET handle = socket_open(address, SOCK_STREAM);
    if (handle == INVALID_SOCKET) {
        return socket_fail();
    }

    if (connect(handle, (struct sockaddr *)&to, length) != 0 && WSAGetLastError() != WSAEWOULDBLOCK) {
        return socket_give_up(handle);
    }

    if (address->family != NET_UNIX) {
        socket_set(handle, IPPROTO_TCP, TCP_NODELAY, 1);
    }

    return (PlatformSocket)handle;
}

static int socket_connected(PlatformSocket socket) {
    int error = 0;
    int length = sizeof error;
    if (getsockopt((SOCKET)socket, SOL_SOCKET, SO_ERROR, (char *)&error, &length) != 0) {
        return socket_fail();
    }

    if (error != 0) {
        WSASetLastError(error);
        return socket_fail();
    }

    struct sockaddr_storage peer;
    int peer_length = sizeof peer;
    if (getpeername((SOCKET)socket, (struct sockaddr *)&peer, &peer_length) == 0) {
        return 0;
    }

    int code = WSAGetLastError();
    WSAPOLLFD now = { .fd = (SOCKET)socket, .events = POLLWRNORM };
    if (WSAPoll(&now, 1, 0) == 0) {
        return IO_WOULD_BLOCK;
    }

    WSASetLastError(code);
    return socket_fail();
}

static PlatformSocket socket_datagram(const PlatformAddress *address, int flags) {
    SOCKET handle = socket_open(address, SOCK_DGRAM);
    if (handle == INVALID_SOCKET) {
        return socket_fail();
    }

    if (!socket_before_bind(handle, address, flags) || !socket_bind(handle, address, flags)) {
        return socket_give_up(handle);
    }

    // A datagram no one took would have a receive fail some time later, as
    // it does on no other system (SIO_UDP_CONNRESET).
    BOOL off = FALSE;
    DWORD ignored = 0;
    WSAIoctl(handle, _WSAIOW(IOC_VENDOR, 12), &off, sizeof off, NULL, 0, &ignored, NULL, NULL);
    return (PlatformSocket)handle;
}

static int socket_connect_datagram(PlatformSocket socket, const PlatformAddress *address) {
    struct sockaddr_storage to;
    int length = socket_address(address, &to);
    if (length == 0) {
        return socket_fail_as(NET_ERROR_INVALID);
    }

    return connect((SOCKET)socket, (struct sockaddr *)&to, length) == 0 ? 0 : socket_fail();
}

static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size) {
    int got = recv((SOCKET)socket, bytes, size > INT32_MAX ? INT32_MAX : (int)size, 0);
    if (got < 0 && WSAGetLastError() == WSAESHUTDOWN) {
        // No more is to be read of it: its end, as POSIX says it.
        return 0;
    }

    return got >= 0 ? got : socket_status();
}

static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size) {
    int sent = send((SOCKET)socket, bytes, size > INT32_MAX ? INT32_MAX : (int)size, 0);
    return sent >= 0 ? sent : socket_status();
}

static int64_t socket_send_to(PlatformSocket socket, const void *bytes, int64_t size, const PlatformAddress *address) {
    if (size > INT32_MAX) {
        return socket_fail_as(NET_ERROR_TOO_LONG);
    }

    if (address->family == NET_NONE) {
        return socket_write(socket, bytes, size);
    }

    struct sockaddr_storage to;
    int length = socket_address(address, &to);
    if (length == 0) {
        return socket_fail_as(NET_ERROR_INVALID);
    }

    int sent = sendto((SOCKET)socket, bytes, (int)size, 0, (struct sockaddr *)&to, length);
    return sent >= 0 ? sent : socket_status();
}

static int64_t socket_receive(PlatformSocket socket, void *bytes, int64_t size, PlatformAddress *from, bool *truncated) {
    struct sockaddr_storage address;
    int length = sizeof address;
    memset(&address, 0, sizeof address);
    int got = recvfrom((SOCKET)socket, bytes, size > INT32_MAX ? INT32_MAX : (int)size, 0, (struct sockaddr *)&address, &length);
    *truncated = got < 0 && WSAGetLastError() == WSAEMSGSIZE;
    if (*truncated) {
        got = (int)size;
    }

    if (got < 0) {
        return socket_status();
    }

    socket_address_from(&address, length, from);
    return got;
}

static bool socket_name(PlatformSocket socket, bool peer, PlatformAddress *address) {
    struct sockaddr_storage at;
    int length = sizeof at;
    memset(&at, 0, sizeof at);
    if ((peer ? getpeername((SOCKET)socket, (struct sockaddr *)&at, &length) : getsockname((SOCKET)socket, (struct sockaddr *)&at, &length)) != 0) {
        socket_fail();
        return false;
    }

    socket_address_from(&at, length, address);
    return true;
}

static bool socket_is_six(SOCKET handle) {
    struct sockaddr_storage at;
    int length = sizeof at;
    memset(&at, 0, sizeof at);
    return getsockname(handle, (struct sockaddr *)&at, &length) == 0 && at.ss_family == AF_INET6;
}

// The level and name of an option that is a number; false for one that is not.
static bool socket_option_name(SOCKET handle, int option, int *level, int *name) {
    bool six = socket_is_six(handle);
    *level = SOL_SOCKET;
    switch (option) {
        case NET_OPTION_NO_DELAY: *level = IPPROTO_TCP; *name = TCP_NODELAY; return true;
        case NET_OPTION_KEEP_ALIVE: *name = SO_KEEPALIVE; return true;
        case NET_OPTION_KEEP_IDLE: *level = IPPROTO_TCP; *name = TCP_KEEPIDLE; return true;
        case NET_OPTION_KEEP_INTERVAL: *level = IPPROTO_TCP; *name = TCP_KEEPINTVL; return true;
        case NET_OPTION_KEEP_COUNT: *level = IPPROTO_TCP; *name = TCP_KEEPCNT; return true;
        case NET_OPTION_RECEIVE_BUFFER: *name = SO_RCVBUF; return true;
        case NET_OPTION_SEND_BUFFER: *name = SO_SNDBUF; return true;
        case NET_OPTION_HOPS: *level = six ? IPPROTO_IPV6 : IPPROTO_IP; *name = six ? IPV6_UNICAST_HOPS : IP_TTL; return true;
        case NET_OPTION_BROADCAST: *name = SO_BROADCAST; return true;
        case NET_OPTION_MULTICAST_LOOP: *level = six ? IPPROTO_IPV6 : IPPROTO_IP; *name = six ? IPV6_MULTICAST_LOOP : IP_MULTICAST_LOOP; return true;
        case NET_OPTION_MULTICAST_HOPS: *level = six ? IPPROTO_IPV6 : IPPROTO_IP; *name = six ? IPV6_MULTICAST_HOPS : IP_MULTICAST_TTL; return true;
        case NET_OPTION_MULTICAST_INTERFACE: *level = six ? IPPROTO_IPV6 : IPPROTO_IP; *name = six ? IPV6_MULTICAST_IF : IP_MULTICAST_IF; return true;
        case NET_OPTION_IPV6_ONLY: *level = IPPROTO_IPV6; *name = IPV6_V6ONLY; return true;
        default: return false;
    }
}

static bool socket_set_option(PlatformSocket socket, int option, int64_t value) {
    if (value < INT32_MIN || value > INT32_MAX) {
        socket_fail_as(NET_ERROR_INVALID);
        return false;
    }

    SOCKET handle = (SOCKET)socket;
    int level = 0, name = 0;
    bool done = false;
    if (option == NET_OPTION_LINGER) {
        if (value > UINT16_MAX) {
            socket_fail_as(NET_ERROR_INVALID);
            return false;
        }

        struct linger linger = { .l_onoff = value >= 0, .l_linger = (u_short)(value < 0 ? 0 : value) };
        done = setsockopt(handle, SOL_SOCKET, SO_LINGER, (const char *)&linger, sizeof linger) == 0;
    } else if (option != NET_OPTION_IPV6_ONLY && socket_option_name(handle, option, &level, &name)) {
        // An IPv4 interface's index is given in network order.
        done = socket_set(handle, level, name, option == NET_OPTION_MULTICAST_INTERFACE && level == IPPROTO_IP ? (int)htonl((u_long)value) : (int)value);
    } else {
        WSASetLastError(WSAENOPROTOOPT);
    }

    if (!done) {
        socket_fail();
    }

    return done;
}

static bool socket_get_option(PlatformSocket socket, int option, int64_t *value) {
    SOCKET handle = (SOCKET)socket;
    if (option == NET_OPTION_LINGER) {
        struct linger linger = { 0 };
        int size = sizeof linger;
        if (getsockopt(handle, SOL_SOCKET, SO_LINGER, (char *)&linger, &size) != 0) {
            socket_fail();
            return false;
        }

        *value = linger.l_onoff ? linger.l_linger : -1;
        return true;
    }

    int level = 0, name = 0, number = 0;
    int size = sizeof number;
    if (option == NET_OPTION_MULTICAST_INTERFACE || !socket_option_name(handle, option, &level, &name)) {
        WSASetLastError(WSAENOPROTOOPT);
        socket_fail();
        return false;
    }

    if (getsockopt(handle, level, name, (char *)&number, &size) != 0) {
        socket_fail();
        return false;
    }

    *value = size == 1 ? number & 0xFF : number;
    return true;
}

static bool socket_membership(PlatformSocket socket, bool join, const PlatformAddress *group, uint32_t index) {
    GROUP_REQ request;
    memset(&request, 0, sizeof request);
    if (group->family == NET_UNIX || socket_address(group, &request.gr_group) == 0) {
        socket_fail_as(NET_ERROR_INVALID);
        return false;
    }

    request.gr_interface = index;
    int level = group->family == NET_IPV6 ? IPPROTO_IPV6 : IPPROTO_IP;
    if (setsockopt((SOCKET)socket, level, join ? MCAST_JOIN_GROUP : MCAST_LEAVE_GROUP, (const char *)&request, sizeof request) != 0) {
        socket_fail();
        return false;
    }

    return true;
}

static void socket_shutdown(PlatformSocket socket, bool writing) {
    shutdown((SOCKET)socket, writing ? SD_SEND : SD_RECEIVE);
}

static void socket_close(PlatformSocket socket) {
    closesocket((SOCKET)socket);
}

// Windows' Unix sockets are streams alone.
static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length) {
    (void)name, (void)name_length, (void)bytes, (void)length;
    return 1;
}

// ---- Names and interfaces ------------------------------------------------------

static int host_resolve(const char *host, int family, PlatformAddress **addresses, int *code) {
    *addresses = NULL;
    *code = NET_ERROR_OTHER;
    if (!sockets_start()) {
        return -1;
    }

    ADDRINFOW hints = { 0 };
    hints.ai_family = family == NET_IPV4 ? AF_INET : family == NET_IPV6 ? AF_INET6 : AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    ADDRINFOW *found = NULL;
    wchar_t *wide = to_wide(host);
    int error = GetAddrInfoW(wide, NULL, &hints, &found);
    free(wide);
    if (error != 0) {
        *code = error == WSAHOST_NOT_FOUND || error == WSANO_DATA || error == WSAEAFNOSUPPORT ? NET_ERROR_NOT_FOUND
                : error == WSATRY_AGAIN ? NET_ERROR_TRY_LATER
                                        : NET_ERROR_OTHER;
        return -1;
    }

    int count = 0;
    for (ADDRINFOW *at = found; at != NULL; at = at->ai_next) {
        count++;
    }

    PlatformAddress *all = calloc((size_t)count + 1, sizeof *all);
    if (all == NULL) {
        sable_trap("out of memory");
    }

    int n = 0;
    for (ADDRINFOW *at = found; at != NULL; at = at->ai_next) {
        struct sockaddr_storage address;
        memset(&address, 0, sizeof address);
        if (at->ai_addrlen <= sizeof address) {
            memcpy(&address, at->ai_addr, at->ai_addrlen);
            socket_address_from(&address, (int)at->ai_addrlen, &all[n]);
            n += all[n].family == NET_IPV4 || all[n].family == NET_IPV6 ? 1 : 0;
        }
    }

    FreeAddrInfoW(found);
    *addresses = all;
    return n;
}

static uint32_t interface_index(const char *name) {
    return if_nametoindex(name);
}

static int interfaces_list(PlatformInterface **interfaces) {
    *interfaces = NULL;
    ULONG size = 32768;
    IP_ADAPTER_ADDRESSES *found = NULL;
    ULONG error = ERROR_BUFFER_OVERFLOW;
    for (int tries = 0; tries < 4 && error == ERROR_BUFFER_OVERFLOW; tries++) {
        free(found);
        found = malloc(size);
        if (found == NULL) {
            sable_trap("out of memory");
        }

        error = GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, NULL, found, &size);
    }

    if (error != ERROR_SUCCESS) {
        free(found);
        net_last_failure.kind = NET_ERROR_OTHER;
        net_last_failure.code = error;
        return error == ERROR_NO_DATA ? 0 : -1;
    }

    int count = 0;
    for (IP_ADAPTER_ADDRESSES *at = found; at != NULL; at = at->Next) {
        count++;
        for (IP_ADAPTER_UNICAST_ADDRESS *one = at->FirstUnicastAddress; one != NULL; one = one->Next) {
            count++;
        }
    }

    PlatformInterface *all = calloc((size_t)count + 1, sizeof *all);
    if (all == NULL) {
        sable_trap("out of memory");
    }

    int n = 0;
    for (IP_ADAPTER_ADDRESSES *at = found; at != NULL; at = at->Next) {
        PlatformInterface row = { 0 };
        // The name if_nametoindex takes, which is not the one people gave it.
        row.index = at->IfIndex != 0 ? at->IfIndex : at->Ipv6IfIndex;
        if (if_indextoname(row.index, row.name) == NULL) {
            snprintf(row.name, sizeof row.name, "%s", at->AdapterName);
        }

        row.flags = (at->OperStatus == IfOperStatusUp ? NET_INTERFACE_UP | NET_INTERFACE_RUNNING : 0)
                    | (at->IfType == IF_TYPE_SOFTWARE_LOOPBACK ? NET_INTERFACE_LOOPBACK : 0)
                    | (at->IfType == IF_TYPE_PPP || at->IfType == IF_TYPE_TUNNEL ? NET_INTERFACE_POINT_TO_POINT : 0)
                    | (at->Flags & IP_ADAPTER_NO_MULTICAST ? 0 : NET_INTERFACE_MULTICAST);
        row.mtu = at->Mtu == 0xFFFFFFFF ? 0 : (int64_t)at->Mtu;
        row.hardware = -1;
        if (at->PhysicalAddressLength == 6) {
            row.hardware = 0;
            for (int i = 0; i < 6; i++) {
                row.hardware = row.hardware << 8 | at->PhysicalAddress[i];
            }
        }

        all[n++] = row;
        for (IP_ADAPTER_UNICAST_ADDRESS *one = at->FirstUnicastAddress; one != NULL; one = one->Next) {
            struct sockaddr_storage address;
            memset(&address, 0, sizeof address);
            if (one->Address.iSockaddrLength > (int)sizeof address) {
                continue;
            }

            memcpy(&address, one->Address.lpSockaddr, (size_t)one->Address.iSockaddrLength);
            PlatformInterface with = row;
            socket_address_from(&address, one->Address.iSockaddrLength, &with.address);
            with.prefix = one->OnLinkPrefixLength;
            all[n++] = with;
        }
    }

    free(found);
    *interfaces = all;
    return n;
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
    wchar_t *wide_name = to_wide_path(name);
    wchar_t *wide_mode = to_wide(mode);
    FILE *file = _wfopen(wide_name, wide_mode);
    free(wide_name);
    free(wide_mode);
    return file;
}

static bool append_file(const char *name, const char *bytes, size_t length) {
    wchar_t *wide = to_wide_path(name);
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
    wchar_t *wide = to_wide_path(name);
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
    wchar_t *wide = to_wide_path(name);
    bool removed = DeleteFileW(wide) || RemoveDirectoryW(wide);
    free(wide);
    return removed;
}

static bool make_directory(const char *name) {
    wchar_t *wide = to_wide_path(name);
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
    wchar_t *wide = to_wide_path(pattern);
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
        case ERROR_BAD_NETPATH:
        case ERROR_BAD_NET_NAME:
            return FILE_ERROR_NOT_FOUND;
        case ERROR_ACCESS_DENIED:
        case ERROR_SHARING_VIOLATION:
        case ERROR_LOCK_VIOLATION:
        case ERROR_PRIVILEGE_NOT_HELD:
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
        case ERROR_FILENAME_EXCED_RANGE:
            return FILE_ERROR_INVALID;
        case ERROR_DIR_NOT_EMPTY:
            return FILE_ERROR_NOT_EMPTY;
        case ERROR_NOT_SAME_DEVICE:
            return FILE_ERROR_CROSSES;
        case ERROR_NOT_SUPPORTED:
        case ERROR_INVALID_FUNCTION:
            return FILE_ERROR_UNSUPPORTED;
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

    wchar_t *wide = to_wide_path(name);
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

static bool file_close(PlatformFile file, FileError *error) {
    if (!CloseHandle(handle_of(file))) {
        file_failed(error);
        return false;
    }

    return true;
}

// The C runtime's descriptors, whose errors are its own.
static void descriptor_failed(FileError *error) {
    error->kind = errno == EBADF ? FILE_ERROR_INVALID : FILE_ERROR_OTHER;
    error->code = errno;
}

static bool descriptor_close(int descriptor, FileError *error) {
    if (_close(descriptor) != 0) {
        descriptor_failed(error);
        return false;
    }

    return true;
}

static int descriptor_duplicate(int descriptor, FileError *error) {
    int made = _dup(descriptor);
    if (made < 0) {
        descriptor_failed(error);
    } else {
        SetHandleInformation((HANDLE)_get_osfhandle(made), HANDLE_FLAG_INHERIT, 0);
    }

    return made;
}

static PlatformFile file_standard(int which) {
    DWORD id = which == 0 ? STD_INPUT_HANDLE : which == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE;
    return (PlatformFile)(intptr_t)GetStdHandle(id);
}

static char *file_canonical(const char *name, FileError *error) {
    wchar_t *wide = to_wide_path(name);
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

// ---- The file system, and watching it ------------------------------------------

#include "platform_fs_win32.c"

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

static char *user_locale(void) {
    wchar_t name[LOCALE_NAME_MAX_LENGTH];
    return GetUserDefaultLocaleName(name, LOCALE_NAME_MAX_LENGTH) > 0 ? from_wide(name) : NULL;
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

// ---- The terminal ----------------------------------------------------------

#include "platform_terminal_win32.c"
