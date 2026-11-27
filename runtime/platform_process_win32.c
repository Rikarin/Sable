// The Sable runtime's processes and signals for Windows: what platform.h
// says, under "Processes". Part of platform_win32.c.

// ---- The program and its command line ----------------------------------------

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

// ---- Pipes -----------------------------------------------------------------

// An anonymous pipe's reads and writes cannot be overlapped, so a child's
// stream is a named pipe with a name of its own, one reader and one writer
// (as .NET's and Rust's are): the child's end blocks, as a program's
// streams do, and this program's is read and written without waiting. A
// read or a write is begun into the pipe's own buffer and is asked about
// again later; the thread pool hears its event, and has the reactor wake
// the task that waits.
//
// A pipe closed is used again for another, never freed: a task that reads
// one another is closing finds it closed, not gone.
enum { WIN_PIPE_BUFFER = 65536 };

typedef struct WinPipe {
    SRWLOCK lock;
    HANDLE handle; // NULL once closed
    HANDLE event;  // reset by the wait it wakes; each operation's
    HANDLE wait;   // the thread pool's, while watched
    void *key;
    bool reads;
    bool pending; // an operation is begun, and not heard of since
    OVERLAPPED operation;
    DWORD length; // of 'bytes': read and to be given, or being written
    DWORD used;   // given already
    struct WinPipe *next_free;
    char bytes[WIN_PIPE_BUFFER];
} WinPipe;

static SRWLOCK free_pipes_lock = SRWLOCK_INIT;
static WinPipe *free_pipes;

static WinPipe *pipe_make(HANDLE handle, bool reads) {
    AcquireSRWLockExclusive(&free_pipes_lock);
    WinPipe *pipe = free_pipes;
    if (pipe != NULL) {
        free_pipes = pipe->next_free;
    }

    ReleaseSRWLockExclusive(&free_pipes_lock);
    if (pipe == NULL) {
        pipe = calloc(1, sizeof *pipe);
        if (pipe == NULL) {
            sable_trap("out of memory");
        }

        InitializeSRWLock(&pipe->lock);
        pipe->event = CreateEventW(NULL, FALSE, FALSE, NULL);
        if (pipe->event == NULL) {
            sable_trap("out of memory");
        }
    }

    AcquireSRWLockExclusive(&pipe->lock);
    ResetEvent(pipe->event);
    pipe->handle = handle;
    pipe->reads = reads;
    pipe->pending = false;
    pipe->length = pipe->used = 0;
    pipe->wait = NULL;
    ReleaseSRWLockExclusive(&pipe->lock);
    return pipe;
}

static void pipe_operation(WinPipe *pipe) {
    memset(&pipe->operation, 0, sizeof pipe->operation);
    pipe->operation.hEvent = pipe->event;
}

// Whether the pipe's other end has gone.
static bool pipe_broken(DWORD code) {
    return code == ERROR_BROKEN_PIPE || code == ERROR_HANDLE_EOF || code == ERROR_PIPE_NOT_CONNECTED || code == ERROR_NO_DATA;
}

static int64_t pipe_read(PlatformPipe from, void *bytes, int64_t size) {
    WinPipe *pipe = (WinPipe *)from;
    int64_t result = IO_FAILED;
    AcquireSRWLockExclusive(&pipe->lock);
    while (pipe->handle != NULL) {
        if (!pipe->pending && pipe->used < pipe->length) {
            DWORD left = pipe->length - pipe->used;
            DWORD take = size < (int64_t)left ? (DWORD)size : left;
            memcpy(bytes, pipe->bytes + pipe->used, take);
            pipe->used += take;
            result = take;
            break;
        }

        if (!pipe->pending) {
            pipe_operation(pipe);
            // A message longer than the buffer is read in parts (a named pipe's in message mode).
            if (!ReadFile(pipe->handle, pipe->bytes, WIN_PIPE_BUFFER, NULL, &pipe->operation) && GetLastError() != ERROR_IO_PENDING
                && GetLastError() != ERROR_MORE_DATA) {
                result = pipe_broken(GetLastError()) ? 0 : IO_FAILED;
                break;
            }

            pipe->pending = true;
        }

        DWORD got = 0;
        if (!GetOverlappedResult(pipe->handle, &pipe->operation, &got, FALSE) && GetLastError() != ERROR_MORE_DATA) {
            DWORD code = GetLastError();
            if (code == ERROR_IO_INCOMPLETE) {
                result = IO_WOULD_BLOCK;
            } else {
                pipe->pending = false;
                result = pipe_broken(code) ? 0 : IO_FAILED;
            }

            break;
        }

        // None is a write of nothing by the child, not its end: again.
        pipe->pending = false;
        pipe->used = 0;
        pipe->length = got;
    }

    ReleaseSRWLockExclusive(&pipe->lock);
    return result;
}

// Bytes are said to be written once the child's end has them, so that
// closing the pipe after loses none: until then it would block, and is
// tried again with the same bytes. Other bytes are those of a write given
// up (its task cancelled): the one begun is let finish, and these follow.
static int64_t pipe_write(PlatformPipe to, const void *bytes, int64_t size) {
    WinPipe *pipe = (WinPipe *)to;
    int64_t result = IO_FAILED;
    AcquireSRWLockExclusive(&pipe->lock);
    while (pipe->handle != NULL) {
        if (!pipe->pending) {
            pipe_operation(pipe);
            pipe->length = size < WIN_PIPE_BUFFER ? (DWORD)size : WIN_PIPE_BUFFER;
            memcpy(pipe->bytes, bytes, pipe->length);
            if (!WriteFile(pipe->handle, pipe->bytes, pipe->length, NULL, &pipe->operation) && GetLastError() != ERROR_IO_PENDING) {
                break;
            }

            pipe->pending = true;
        }

        DWORD put = 0;
        if (!GetOverlappedResult(pipe->handle, &pipe->operation, &put, FALSE)) {
            if (GetLastError() == ERROR_IO_INCOMPLETE) {
                result = IO_WOULD_BLOCK;
            } else {
                pipe->pending = false;
            }

            break;
        }

        pipe->pending = false;
        if ((int64_t)pipe->length <= size && memcmp(pipe->bytes, bytes, pipe->length) == 0) {
            result = put;
            break;
        }
    }

    ReleaseSRWLockExclusive(&pipe->lock);
    return result;
}

static void pipe_close(PlatformPipe which) {
    WinPipe *pipe = (WinPipe *)which;
    AcquireSRWLockExclusive(&pipe->lock);
    HANDLE handle = pipe->handle;
    if (handle != NULL && pipe->pending) {
        // What was begun is stopped, and heard to have: it has the buffer.
        DWORD ignored;
        CancelIoEx(handle, &pipe->operation);
        GetOverlappedResult(handle, &pipe->operation, &ignored, TRUE);
        pipe->pending = false;
    }

    pipe->handle = NULL;
    ReleaseSRWLockExclusive(&pipe->lock);
    if (handle != NULL) {
        CloseHandle(handle);
        AcquireSRWLockExclusive(&free_pipes_lock);
        pipe->next_free = free_pipes;
        free_pipes = pipe;
        ReleaseSRWLockExclusive(&free_pipes_lock);
    }
}

static VOID CALLBACK pipe_signalled(PVOID context, BOOLEAN timed_out) {
    (void)timed_out;
    WinPipe *pipe = context;
    reactor_ready(pipe->key, pipe->reads ? REACTOR_READABLE : REACTOR_WRITABLE);
}

static bool pipe_watch(PlatformPipe which, void *key) {
    WinPipe *pipe = (WinPipe *)which;
    pipe->key = key;
    return RegisterWaitForSingleObject(&pipe->wait, pipe->event, pipe_signalled, pipe, INFINITE, WT_EXECUTEDEFAULT) != FALSE;
}

// Returns once no callback runs or will: the key is not used after.
static void pipe_unwatch(PlatformPipe which) {
    WinPipe *pipe = (WinPipe *)which;
    if (pipe->wait != NULL) {
        UnregisterWaitEx(pipe->wait, INVALID_HANDLE_VALUE);
        pipe->wait = NULL;
    }
}

static void pipes_wait(const PlatformPipe pipes[3]) {
    HANDLE events[3];
    DWORD n = 0;
    for (int i = 0; i < 3; i++) {
        if (pipes[i] != PIPE_NONE) {
            events[n++] = ((WinPipe *)pipes[i])->event;
        }
    }

    if (n > 0) {
        WaitForMultipleObjects(n, events, FALSE, INFINITE);
    }
}

// ---- Starting a program ------------------------------------------------------

static bool process_failed(ProcessError *error, int kind, int64_t code) {
    error->kind = kind;
    error->code = code;
    return false;
}

// The child's end of a pipe in 'stream', to be inherited, and this
// program's in 'pipe'.
static bool open_pipe(int which, HANDLE *stream, PlatformPipe *pipe) {
    static LONG count;
    bool reads = which != 0;
    unsigned char random[8];
    if (!random_bytes(random, sizeof random)) {
        memset(random, 0, sizeof random);
    }

    unsigned long long part;
    memcpy(&part, random, sizeof part);
    wchar_t name[96];
    swprintf(name, sizeof name / sizeof *name, L"\\\\.\\pipe\\sable-%lu-%ld-%016llx", GetCurrentProcessId(), InterlockedIncrement(&count), part);
    HANDLE own = CreateNamedPipeW(name, (reads ? PIPE_ACCESS_INBOUND : PIPE_ACCESS_OUTBOUND) | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, WIN_PIPE_BUFFER, WIN_PIPE_BUFFER, 0, NULL);
    if (own == INVALID_HANDLE_VALUE) {
        return false;
    }

    SECURITY_ATTRIBUTES inherited = { sizeof inherited, NULL, TRUE };
    HANDLE child = CreateFileW(name, reads ? GENERIC_WRITE | FILE_READ_ATTRIBUTES : GENERIC_READ | FILE_WRITE_ATTRIBUTES, 0, &inherited,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (child == INVALID_HANDLE_VALUE) {
        CloseHandle(own);
        return false;
    }

    *stream = child;
    *pipe = (PlatformPipe)pipe_make(own, reads);
    return true;
}

// A child's stream opened, to be inherited: nothing, a file, its end of a
// pipe, or a copy of this program's own, which may be none (NULL).
static bool open_stream(const ProcessSpec *spec, int which, HANDLE *stream, PlatformPipe *pipe, ProcessError *error) {
    SECURITY_ATTRIBUTES inherited = { sizeof inherited, NULL, TRUE };
    DWORD share = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
    HANDLE opened = INVALID_HANDLE_VALUE;
    switch (spec->stdio[which]) {
    case STDIO_INHERIT: {
        HANDLE own = GetStdHandle(which == 0 ? STD_INPUT_HANDLE : which == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE);
        if (own == NULL || own == INVALID_HANDLE_VALUE
            || !DuplicateHandle(GetCurrentProcess(), own, GetCurrentProcess(), stream, 0, TRUE, DUPLICATE_SAME_ACCESS)) {
            *stream = NULL;
        }

        return true;
    }
    case STDIO_NULL:
        opened = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, share, &inherited, OPEN_EXISTING, 0, NULL);
        break;
    case STDIO_FILE: {
        wchar_t *name = to_wide(spec->files[which]);
        opened = which == 0 ? CreateFileW(name, GENERIC_READ, share, &inherited, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL)
                            : CreateFileW(name, GENERIC_WRITE, share, &inherited, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        free(name);
        break;
    }
    case STDIO_HANDLE:
        // A copy to be inherited, closed after as the others are: the handle stays this program's.
        return DuplicateHandle(GetCurrentProcess(), (HANDLE)spec->handles[which], GetCurrentProcess(), stream, 0, TRUE, DUPLICATE_SAME_ACCESS)
               || process_failed(error, PROCESS_ERROR_FILE, which);
    default:
        return open_pipe(which, stream, pipe) || process_failed(error, PROCESS_ERROR_OTHER, (int64_t)GetLastError());
    }

    if (opened == INVALID_HANDLE_VALUE) {
        return process_failed(error, PROCESS_ERROR_FILE, which);
    }

    *stream = opened;
    return true;
}

// A batch file: the system would run it with cmd.exe, which reads its
// arguments as it reads a command line, and no quoting keeps every one of
// them from being read as more commands.
static bool is_batch_file(const wchar_t *path) {
    size_t length = wcslen(path);
    while (length > 0 && (path[length - 1] == L'.' || path[length - 1] == L' ')) {
        length--;
    }

    return length >= 4 && (_wcsnicmp(path + length - 4, L".bat", 4) == 0 || _wcsnicmp(path + length - 4, L".cmd", 4) == 0);
}

// As the system keeps an environment, and the functions that search one
// expect: in order, whatever the letters' case.
static int compare_variables(const void *left, const void *right) {
    const wchar_t *a = *(const wchar_t *const *)left, *b = *(const wchar_t *const *)right;
    const wchar_t *a_end = wcschr(a, L'='), *b_end = wcschr(b, L'=');
    int a_length = a_end == NULL ? -1 : (int)(a_end - a), b_length = b_end == NULL ? -1 : (int)(b_end - b);
    return CompareStringOrdinal(a, a_length, b, b_length, TRUE) - CSTR_EQUAL;
}

// "NAME=value", each ended by a NUL, and the whole by another.
static wchar_t *environment_block(char **environment) {
    size_t count = 0, units = 2;
    while (environment[count] != NULL) {
        count++;
    }

    wchar_t **variables = malloc((count + 1) * sizeof *variables);
    if (variables == NULL) {
        sable_trap("out of memory");
    }

    for (size_t i = 0; i < count; i++) {
        variables[i] = to_wide(environment[i]);
        units += wcslen(variables[i]) + 1;
    }

    qsort(variables, count, sizeof *variables, compare_variables);
    wchar_t *block = calloc(units, sizeof *block);
    if (block == NULL) {
        sable_trap("out of memory");
    }

    wchar_t *at = block;
    for (size_t i = 0; i < count; i++) {
        size_t length = wcslen(variables[i]) + 1;
        memcpy(at, variables[i], length * sizeof *at);
        at += length;
        free(variables[i]);
    }

    free(variables);
    return block;
}

// Suspended until it is in its job, which then has everything it starts.
static bool create_process(const wchar_t *program, wchar_t *command, wchar_t *environment, const wchar_t *directory, const HANDLE streams[3],
    PlatformProcess *process, ProcessError *error) {
    // The handles it inherits, and no others, however many this program
    // has to be inherited; each named once.
    HANDLE inherited[3];
    DWORD count = 0;
    for (int i = 0; i < 3; i++) {
        if (streams[i] != NULL) {
            inherited[count++] = streams[i];
        }
    }

    SIZE_T size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &size);
    LPPROC_THREAD_ATTRIBUTE_LIST attributes = malloc(size);
    if (attributes == NULL) {
        sable_trap("out of memory");
    }

    STARTUPINFOEXW startup = { 0 };
    startup.StartupInfo.cb = sizeof startup;
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = streams[0];
    startup.StartupInfo.hStdOutput = streams[1];
    startup.StartupInfo.hStdError = streams[2];
    bool listed = InitializeProcThreadAttributeList(attributes, 1, 0, &size) != FALSE;
    bool ready = listed;
    if (listed && count > 0) {
        ready = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, count * sizeof(HANDLE), NULL, NULL) != FALSE;
    }

    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION child = { 0 };
    bool started = ready
                   && CreateProcessW(program, command, NULL, NULL, count > 0, CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
                          environment, directory, &startup.StartupInfo, &child) != FALSE;
    DWORD code = GetLastError();
    if (listed) {
        DeleteProcThreadAttributeList(attributes);
    }

    free(attributes);
    if (!started) {
        switch (code) {
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND:
            return process_failed(error, PROCESS_ERROR_NOT_FOUND, code);
        case ERROR_ACCESS_DENIED:
        case ERROR_ELEVATION_REQUIRED:
            return process_failed(error, PROCESS_ERROR_PERMISSION, code);
        case ERROR_DIRECTORY:
            return process_failed(error, PROCESS_ERROR_DIRECTORY, code);
        case ERROR_BAD_EXE_FORMAT:
        case ERROR_EXE_MACHINE_TYPE_MISMATCH:
            return process_failed(error, PROCESS_ERROR_NOT_EXECUTABLE, code);
        default:
            return process_failed(error, PROCESS_ERROR_OTHER, code);
        }
    }

    HANDLE job = CreateJobObjectW(NULL, NULL);
    if (job != NULL && !AssignProcessToJobObject(job, child.hProcess)) {
        CloseHandle(job);
        job = NULL;
    }

    ResumeThread(child.hThread);
    CloseHandle(child.hThread);
    process->id = (int64_t)child.dwProcessId;
    process->process = child.hProcess;
    process->job = job;
    return true;
}

static bool process_start(const ProcessSpec *spec, PlatformProcess *process, PlatformPipe pipes[3], ProcessError *error) {
    error->kind = PROCESS_ERROR_NONE;
    error->code = 0;
    pipes[0] = pipes[1] = pipes[2] = PIPE_NONE;
    if (spec->directory != NULL && !is_directory(spec->directory)) {
        return process_failed(error, PROCESS_ERROR_DIRECTORY, ERROR_DIRECTORY);
    }

    wchar_t *program = find_program(spec->argv[0]);
    if (program == NULL) {
        return process_failed(error, PROCESS_ERROR_NOT_FOUND, ERROR_FILE_NOT_FOUND);
    }

    if (is_batch_file(program)) {
        free(program);
        return process_failed(error, PROCESS_ERROR_NOT_EXECUTABLE, ERROR_BAD_EXE_FORMAT);
    }

    Buffer line = { 0 };
    for (char **argument = spec->argv; *argument != NULL; argument++) {
        add_argument(&line, *argument);
    }

    buffer_add(&line, "", 1);
    wchar_t *command = to_wide(line.bytes);
    free(line.bytes);
    wchar_t *environment = spec->environment == NULL ? NULL : environment_block(spec->environment);
    wchar_t *directory = spec->directory == NULL ? NULL : to_wide(spec->directory);

    HANDLE streams[3] = { NULL, NULL, NULL };
    bool started = true;
    for (int i = 0; i < 3 && started; i++) {
        // Output and errors to one file: one opening of it, and one
        // position, so that neither writes over the other.
        if (i == 2 && spec->stdio[1] == STDIO_FILE && spec->stdio[2] == STDIO_FILE && strcmp(spec->files[1], spec->files[2]) == 0) {
            started = DuplicateHandle(GetCurrentProcess(), streams[1], GetCurrentProcess(), &streams[2], 0, TRUE, DUPLICATE_SAME_ACCESS)
                      || process_failed(error, PROCESS_ERROR_OTHER, (int64_t)GetLastError());
        } else {
            started = open_stream(spec, i, &streams[i], &pipes[i], error);
        }
    }

    fflush(stdout);
    fflush(stderr);
    started = started && create_process(program, command, environment, directory, streams, process, error);
    for (int i = 0; i < 3; i++) {
        if (streams[i] != NULL) {
            CloseHandle(streams[i]);
        }

        if (!started && pipes[i] != PIPE_NONE) {
            pipe_close(pipes[i]);
            pipes[i] = PIPE_NONE;
        }
    }

    free(directory);
    free(environment);
    free(command);
    free(program);
    return started;
}

// ---- A started program -------------------------------------------------------

static void process_wait(const PlatformProcess *process) {
    WaitForSingleObject(process->process, INFINITE);
}

static void process_reap(PlatformProcess *process, int64_t *code, int64_t *signal) {
    DWORD ended = 0;
    GetExitCodeProcess(process->process, &ended);
    *code = (int64_t)ended;
    *signal = 0;
    CloseHandle(process->process);
    // Closed, not ended: what the program started and left runs on.
    if (process->job != NULL) {
        CloseHandle(process->job);
    }

    process->process = process->job = NULL;
}

// Windows has no signals: what SIGKILL and SIGTERM do, done as it can be.
static bool process_signal(const PlatformProcess *process, int signal) {
    if (signal != 9 && signal != 15) {
        return false;
    }

    // Refused for one that has ended, and is not yet reaped: as on POSIX, done.
    return TerminateProcess(process->process, 1) || WaitForSingleObject(process->process, 0) == WAIT_OBJECT_0;
}

static bool process_kill_tree(const PlatformProcess *process) {
    if (process->job != NULL) {
        return TerminateJobObject(process->job, 1) != FALSE;
    }

    return process_signal(process, 9);
}

// ---- Signals ---------------------------------------------------------------

// The console's events, as .NET reads them: Ctrl+C an interrupt, Ctrl+Break
// a quit, the window closed a hangup, the system shut down a terminate. The
// system runs the handler on a thread of its own.
enum { WIN_SIGHUP = 1, WIN_SIGINT = 2, WIN_SIGQUIT = 3, WIN_SIGTERM = 15 };

static volatile LONG signal_caught[16];
static bool signal_handling;

static BOOL WINAPI on_console_event(DWORD event) {
    int number = event == CTRL_C_EVENT ? WIN_SIGINT : event == CTRL_BREAK_EVENT ? WIN_SIGQUIT : event == CTRL_CLOSE_EVENT ? WIN_SIGHUP : WIN_SIGTERM;
    if (!InterlockedCompareExchange(&signal_caught[number], 0, 0)) {
        // It ends the program.
        terminal_restore();
        return FALSE;
    }

    signal_arrived(number);
    if (number == WIN_SIGHUP || number == WIN_SIGTERM) {
        // The process ends as this returns: the program is given the
        // seconds the system allows to end by itself.
        Sleep(INFINITE);
    }

    return TRUE;
}

// The handler set, once: it is told of each event, caught or not.
static bool console_events(void) {
    if (!signal_handling) {
        signal_handling = SetConsoleCtrlHandler(on_console_event, TRUE) != FALSE;
    }

    return signal_handling;
}

static bool signal_catch(int number) {
    if (number != WIN_SIGHUP && number != WIN_SIGINT && number != WIN_SIGQUIT && number != WIN_SIGTERM) {
        return false;
    }

    if (!console_events()) {
        return false;
    }

    InterlockedExchange(&signal_caught[number], 1);
    return true;
}

static void signal_let_go(int number) {
    if (number > 0 && number < 16) {
        InterlockedExchange(&signal_caught[number], 0);
    }
}

// Said to this program alone: a console's event would be said to every
// program on the console.
static bool signal_raise(int number) {
    if (number > 0 && number < 16 && InterlockedCompareExchange(&signal_caught[number], 0, 0)) {
        signal_arrived(number);
        return true;
    }

    if (number == WIN_SIGHUP || number == WIN_SIGINT || number == WIN_SIGQUIT || number == WIN_SIGTERM || number == 9) {
        fflush(stdout);
        fflush(stderr);
        terminal_restore();
        ExitProcess(128 + (UINT)number);
    }

    return false;
}

// ---- Service managers ------------------------------------------------------

// A service's dispatcher takes a thread of its own for the service's life,
// and runs service_main on another as the Service Control Manager starts
// it; the controls come to on_service_control on the dispatcher's. A program
// the manager did not start is told so at once.
static SERVICE_STATUS_HANDLE service_handle;
static DWORD service_checkpoint;
static HANDLE service_connected;
static volatile LONG service_result = 2;
static wchar_t service_name[] = L"";

static bool service_report(DWORD state) {
    if (service_handle == NULL) {
        return false;
    }

    bool pending = state == SERVICE_START_PENDING || state == SERVICE_STOP_PENDING;
    SERVICE_STATUS status;
    memset(&status, 0, sizeof status);
    status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    status.dwCurrentState = state;
    status.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
    status.dwWin32ExitCode = NO_ERROR;
    status.dwCheckPoint = pending ? ++service_checkpoint : 0;
    status.dwWaitHint = pending ? 30000 : 0;
    return SetServiceStatus(service_handle, &status) != FALSE;
}

static DWORD WINAPI on_service_control(DWORD control, DWORD type, LPVOID data, LPVOID context) {
    (void)type, (void)data, (void)context;
    switch (control) {
    case SERVICE_CONTROL_STOP:
    case SERVICE_CONTROL_SHUTDOWN:
        service_report(SERVICE_STOP_PENDING);
        // The host's SIGTERM; a program that does not catch it ends.
        signal_raise(WIN_SIGTERM);
        return NO_ERROR;
    case SERVICE_CONTROL_INTERROGATE:
        return NO_ERROR;
    default:
        return ERROR_CALL_NOT_IMPLEMENTED;
    }
}

static VOID WINAPI service_main(DWORD count, LPWSTR *names) {
    service_handle = RegisterServiceCtrlHandlerExW(count > 0 && names != NULL ? names[0] : service_name, on_service_control, NULL);
    if (service_handle != NULL) {
        service_report(SERVICE_START_PENDING);
    }

    InterlockedExchange(&service_result, service_handle != NULL ? 0 : 2);
    SetEvent(service_connected);
}

static DWORD WINAPI service_dispatch(LPVOID unused) {
    (void)unused;
    // An own process's service is not looked up by its name.
    SERVICE_TABLE_ENTRYW table[] = {{service_name, service_main}, {NULL, NULL}};
    if (!StartServiceCtrlDispatcherW(table)) {
        InterlockedExchange(&service_result, GetLastError() == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT ? 1 : 2);
        SetEvent(service_connected);
    }

    return 0;
}

static int service_control(int call) {
    if (call == 0) {
        if (service_connected == NULL) {
            service_connected = CreateEventW(NULL, TRUE, FALSE, NULL);
            if (service_connected == NULL) {
                return 2;
            }

            HANDLE thread = CreateThread(NULL, 0, service_dispatch, NULL, 0, NULL);
            if (thread == NULL) {
                SetEvent(service_connected);
            } else {
                CloseHandle(thread);
            }
        }

        WaitForSingleObject(service_connected, INFINITE);
        return (int)InterlockedCompareExchange(&service_result, 0, 0);
    }

    if (service_handle == NULL) {
        return 1;
    }

    DWORD state = call == 1 ? SERVICE_RUNNING : call == 2 ? SERVICE_STOP_PENDING : SERVICE_STOPPED;
    return service_report(state) ? 0 : 2;
}
