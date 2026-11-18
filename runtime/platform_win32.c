// The Sable runtime's platform layer for Windows, with the Microsoft C
// runtime: what platform.h says each platform gives the rest.
//
// Every name Windows takes or gives is UTF-16, and the runtime's are UTF-8:
// they are converted here, at the boundary, so that a path, an argument or
// a variable holds any character on Windows as it does elsewhere.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <dbghelp.h>
#include <fcntl.h>
#include <io.h>
#include <shellapi.h>

#pragma comment(lib, "dbghelp")
#pragma comment(lib, "shell32")

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
__attribute__((constructor)) static void sable_streams_start(void) {
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
    SetConsoleOutputCP(CP_UTF8);
}

// ---- Traps -------------------------------------------------------------------

// With SABLE_BACKTRACE set, a trap says where it happened: a frame per line,
// as 'sable.Module.function + offset' where the symbol is known. Windows
// walks the stack by the unwind tables every function has, and names a
// frame from the program's PDB, which the link always writes.
__attribute__((noinline)) static void print_backtrace(void) {
    if (getenv("SABLE_BACKTRACE") == NULL) {
        return;
    }

    void *frames[64];
    int count = (int)CaptureStackBackTrace(0, 64, frames, NULL);
    HANDLE process = GetCurrentProcess();
    SymSetOptions(SYMOPT_DEFERRED_LOADS);
    bool symbols = SymInitialize(process, NULL, TRUE) != FALSE;
    union {
        SYMBOL_INFO info;
        char space[sizeof(SYMBOL_INFO) + 512];
    } symbol;

    fprintf(stderr, "backtrace:\n");
    // The first two frames are this function and sable_trap.
    for (int i = 2; i < count; i++) {
        const char *at = (const char *)frames[i] - 1;
        memset(&symbol, 0, sizeof symbol);
        symbol.info.SizeOfStruct = sizeof(SYMBOL_INFO);
        symbol.info.MaxNameLen = 512;
        DWORD64 offset = 0;
        if (symbols && SymFromAddr(process, (DWORD64)(uintptr_t)at, &offset, &symbol.info)) {
            fprintf(stderr, "  %2d  %s + %lld\n", i - 2, symbol.info.Name, (long long)offset + 1);
        } else {
            fprintf(stderr, "  %2d  %p\n", i - 2, frames[i]);
        }
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

__attribute__((constructor)) static void sable_watch_main_stack(void) {
    watch_stack();
    AddVectoredExceptionHandler(1, on_exception);
}

// ---- Tasks' threads -------------------------------------------------------

// A slim reader-writer lock, held exclusively, and condition variables.
struct TaskCondition {
    CONDITION_VARIABLE condition;
};

static SRWLOCK task_lock = SRWLOCK_INIT;

static void lock_tasks(void) {
    AcquireSRWLockExclusive(&task_lock);
}

static void condition_init(TaskCondition *condition) {
    InitializeConditionVariable(&condition->condition);
}

static void condition_destroy(TaskCondition *condition) {
    (void)condition;
}

static void condition_signal(TaskCondition *condition) {
    WakeConditionVariable(&condition->condition);
}

static void condition_wait(TaskCondition *condition) {
    SleepConditionVariableSRW(&condition->condition, &task_lock, INFINITE, 0);
}

typedef struct {
    void (*body)(void *argument);
    void *argument;
} ThreadStart;

static DWORD WINAPI thread_entry(void *start) {
    ThreadStart what = *(ThreadStart *)start;
    free(start);
    what.body(what.argument);
    return 0;
}

static bool start_thread(void (*body)(void *argument), void *argument) {
    ThreadStart *start = malloc(sizeof(ThreadStart));
    if (start == NULL) {
        return false;
    }

    start->body = body;
    start->argument = argument;
    HANDLE thread = CreateThread(NULL, 8u << 20, thread_entry, start, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (thread == NULL) {
        free(start);
        return false;
    }

    CloseHandle(thread);
    return true;
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
