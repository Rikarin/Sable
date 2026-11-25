// The Sable runtime's processes and signals for POSIX systems: what
// platform.h says, under "Processes". Part of platform_posix.c.

#if defined(__APPLE__)
#include <libproc.h>
#else
#include <sys/syscall.h>
#endif

// How a program is started: by posix_spawn on macOS, which takes every
// descriptor the child is not to have from it, whoever opened it
// (POSIX_SPAWN_CLOEXEC_DEFAULT); by fork and exec elsewhere, which says why
// an exec failed on every libc and under emulation. (-DPROCESS_FORK=1 has
// macOS fork too, which is how that path is tried off Linux.)
#ifndef PROCESS_FORK
#if defined(__APPLE__)
#define PROCESS_FORK 0
#else
#define PROCESS_FORK 1
#endif
#endif

// ---- Pipes -----------------------------------------------------------------

// Held from a pipe's making until it is closed on exec, and across a fork:
// where a pipe cannot be made closed on exec, no child the runtime starts
// meanwhile inherits it.
static pthread_mutex_t spawn_lock = PTHREAD_MUTEX_INITIALIZER;

// A pipe, both ends closed on exec, under the spawn lock.
static bool make_pipe(int ends[2]) {
#if defined(__linux__)
    return pipe2(ends, O_CLOEXEC) == 0;
#else
    if (pipe(ends) != 0) {
        return false;
    }

    fcntl(ends[0], F_SETFD, FD_CLOEXEC);
    fcntl(ends[1], F_SETFD, FD_CLOEXEC);
    return true;
#endif
}

static bool make_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
#ifdef F_SETNOSIGPIPE
    fcntl(fd, F_SETNOSIGPIPE, 1);
#endif
    return flags >= 0 && fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0;
}

static int64_t pipe_read(PlatformPipe pipe, void *bytes, int64_t size) {
    for (;;) {
        ssize_t got = read((int)pipe, bytes, (size_t)size);
        if (got >= 0) {
            return got;
        }

        if (errno != EINTR) {
            return errno == EAGAIN || errno == EWOULDBLOCK ? IO_WOULD_BLOCK : IO_FAILED;
        }
    }
}

static int64_t write_retrying(int fd, const void *bytes, int64_t size) {
    for (;;) {
        ssize_t put = write(fd, bytes, (size_t)size);
        if (put >= 0) {
            return put;
        }

        if (errno != EINTR) {
            return errno == EAGAIN || errno == EWOULDBLOCK ? IO_WOULD_BLOCK : IO_FAILED;
        }
    }
}

// A write to a pipe no one reads raises SIGPIPE, which would end the
// program. macOS is told not to for the descriptor (F_SETNOSIGPIPE); Linux
// has no such thing for a pipe, so the signal is blocked on this thread for
// the write, and one it raised — sent to the thread — is taken before it
// is let through again. The process's own handling of it is left alone.
static int64_t pipe_write(PlatformPipe pipe, const void *bytes, int64_t size) {
#ifdef F_SETNOSIGPIPE
    return write_retrying((int)pipe, bytes, size);
#else
    sigset_t broken, before, pending;
    sigemptyset(&broken);
    sigaddset(&broken, SIGPIPE);
    sigpending(&pending);
    bool was_pending = sigismember(&pending, SIGPIPE) == 1;
    pthread_sigmask(SIG_BLOCK, &broken, &before);
    int64_t put = write_retrying((int)pipe, bytes, size);
    int error = errno;
    if (put == IO_FAILED && error == EPIPE && !was_pending) {
        struct timespec none = { 0, 0 };
        while (sigtimedwait(&broken, NULL, &none) < 0 && errno == EINTR) {
        }
    }

    pthread_sigmask(SIG_SETMASK, &before, NULL);
    errno = error;
    return put;
#endif
}

static void pipe_close(PlatformPipe pipe) {
    close((int)pipe);
}

static bool pipe_watch(PlatformPipe pipe, void *key) {
    return reactor_add(pipe, key);
}

static void pipe_unwatch(PlatformPipe pipe) {
    reactor_remove(pipe);
}

static void pipes_wait(const PlatformPipe pipes[3]) {
    struct pollfd watched[3];
    nfds_t n = 0;
    for (int i = 0; i < 3; i++) {
        if (pipes[i] != PIPE_NONE) {
            watched[n++] = (struct pollfd){ (int)pipes[i], i == 0 ? POLLOUT : POLLIN, 0 };
        }
    }

    // Interrupted, or failed: the pipes say which, as they are tried again.
    poll(watched, n, -1);
}

// ---- Starting a program ------------------------------------------------------

// The program's path: the name itself when it holds a '/', else the first
// executable of that name on PATH; NULL when there is none. Looked up here
// rather than left to posix_spawnp, which does not report a failed exec
// everywhere (under emulation, the child just exits 127).
static char *find_program(const char *name) {
    if (strchr(name, '/') != NULL) {
        return access(name, X_OK) == 0 ? strdup(name) : NULL;
    }

    const char *path = getenv("PATH");
    if (path == NULL || *name == '\0') {
        return NULL;
    }

    size_t name_length = strlen(name);
    while (*path != '\0') {
        const char *end = strchr(path, ':');
        size_t length = end == NULL ? strlen(path) : (size_t)(end - path);
        char *candidate = malloc(length + name_length + 2);
        if (length == 0) {
            memcpy(candidate, ".", 1);
            length = 1;
        } else {
            memcpy(candidate, path, length);
        }

        candidate[length] = '/';
        memcpy(candidate + length + 1, name, name_length + 1);
        struct stat info;
        if (access(candidate, X_OK) == 0 && stat(candidate, &info) == 0 && S_ISREG(info.st_mode)) {
            return candidate;
        }

        free(candidate);
        if (end == NULL) {
            break;
        }

        path = end + 1;
    }

    return NULL;
}

// The path as it is from this program's directory, wherever the child
// starts: what was found is what runs.
static char *absolute_path(char *path) {
    if (path[0] == '/') {
        return path;
    }

    char *directory = current_directory();
    if (directory == NULL) {
        return path;
    }

    size_t length = strlen(directory) + strlen(path) + 2;
    char *absolute = malloc(length);
    if (absolute == NULL) {
        sable_trap("out of memory");
    }

    snprintf(absolute, length, "%s/%s", directory, path);
    free(directory);
    free(path);
    return absolute;
}

static bool process_failed(ProcessError *error, int kind, int64_t code) {
    error->kind = kind;
    error->code = code;
    return false;
}

// Why an exec failed. The program was there a moment ago, so one not found
// now is a script whose interpreter is not.
static int exec_error(int code) {
    switch (code) {
    case EACCES:
    case EPERM:
        return PROCESS_ERROR_PERMISSION;
    case ENOENT:
    case ENOEXEC:
#ifdef EBADARCH
    case EBADARCH:
    case EBADEXEC:
#endif
        return PROCESS_ERROR_NOT_EXECUTABLE;
    default:
        return PROCESS_ERROR_OTHER;
    }
}

// The descriptor, or a copy of it, clear of the child's 0, 1 and 2, which
// the streams are put over one by one; -1 if it was.
static int clear_of_streams(int fd) {
    if (fd < 0 || fd > 2) {
        return fd;
    }

    int moved = fcntl(fd, F_DUPFD_CLOEXEC, 3);
    close(fd);
    return moved;
}

static void close_all(int *fds, int count) {
    for (int i = 0; i < count; i++) {
        if (fds[i] >= 0) {
            close(fds[i]);
            fds[i] = -1;
        }
    }
}

#if PROCESS_FORK
// Where the child stopped, and why, for the parent.
typedef struct StartFailure {
    int at_directory;
    int code;
} StartFailure;

// What the child does, every call safe after a fork of a program with many
// threads: no memory made, no lock taken. 'failed' is closed by the exec,
// which is how the parent hears it was done.
__attribute__((noinline)) _Noreturn static void child_start(const ProcessSpec *spec, const char *program, char **environment, const int streams[3], int failed) {
    StartFailure failure = { 0, 0 };

    // This program's handlers are not the new one's, and are not to run
    // here: signals are blocked until they are all gone. A signal it
    // ignores stays ignored, as nohup has it, but for SIGPIPE, which a
    // program counts on.
    for (int number = 1; number < NSIG; number++) {
        struct sigaction action;
        if (sigaction(number, NULL, &action) == 0 && action.sa_handler != SIG_DFL && (action.sa_handler != SIG_IGN || number == SIGPIPE)) {
            memset(&action, 0, sizeof action);
            action.sa_handler = SIG_DFL;
            sigaction(number, &action, NULL);
        }
    }

    sigset_t none;
    sigemptyset(&none);
    sigprocmask(SIG_SETMASK, &none, NULL);

    for (int i = 0; i < 3; i++) {
        if (streams[i] >= 0) {
            while (dup2(streams[i], i) < 0) {
                if (errno != EINTR) {
                    goto stopped;
                }
            }
        }
    }

    // Whatever else is open, the runtime's or not: 5.11's close_range,
    // where the kernel has it, marks it all closed on exec.
#if defined(__linux__)
#ifndef SYS_close_range
#define SYS_close_range 436
#endif
    syscall(SYS_close_range, 3u, ~0u, 4u /* CLOSE_RANGE_CLOEXEC */);
#endif

    if (spec->directory != NULL && chdir(spec->directory) != 0) {
        failure.at_directory = 1;
        goto stopped;
    }

    execve(program, spec->argv, environment);

stopped:
    failure.code = errno;
    const char *at = (const char *)&failure;
    for (size_t left = sizeof failure; left > 0;) {
        ssize_t put = write(failed, at, left);
        if (put > 0) {
            at += put;
            left -= (size_t)put;
        } else if (errno != EINTR) {
            break;
        }
    }

    _exit(127);
}

// On Linux the child runs in this program's memory, this thread waiting, until
// its exec: no page table is copied, nor page written to copied after. What
// it does until then is what may be: it writes to its own frames alone, with
// signals blocked until no handler of this program's is left, its signal
// actions and its directory being its own. (A macro, as a function may not
// return from a vfork.)
#if defined(__linux__)
#define fork_to_exec vfork
#else
#define fork_to_exec fork
#endif

// Under the spawn lock: the child forked, and its exec waited for.
static bool start_child(const ProcessSpec *spec, const char *program, char **environment, const int streams[3], pid_t *pid, ProcessError *error) {
    int failed[2];
    if (!make_pipe(failed)) {
        return process_failed(error, PROCESS_ERROR_OTHER, errno);
    }

    failed[1] = clear_of_streams(failed[1]);
    sigset_t all, before;
    sigfillset(&all);
    pthread_sigmask(SIG_SETMASK, &all, &before);
    pid_t child = failed[1] < 0 ? -1 : fork_to_exec();
    if (child == 0) {
        child_start(spec, program, environment, streams, failed[1]);
    }

    int code = errno;
    pthread_sigmask(SIG_SETMASK, &before, NULL);
    if (failed[1] >= 0) {
        close(failed[1]);
    }

    if (child < 0) {
        close(failed[0]);
        return process_failed(error, PROCESS_ERROR_OTHER, code);
    }

    StartFailure failure = { 0, 0 };
    size_t got = 0;
    while (got < sizeof failure) {
        ssize_t n = read(failed[0], (char *)&failure + got, sizeof failure - got);
        if (n > 0) {
            got += (size_t)n;
        } else if (n == 0 || errno != EINTR) {
            break;
        }
    }

    close(failed[0]);
    if (got < sizeof failure) {
        *pid = child;
        return true;
    }

    while (waitpid(child, NULL, 0) < 0 && errno == EINTR) {
    }

    return failure.at_directory ? process_failed(error, PROCESS_ERROR_DIRECTORY, failure.code)
                                : process_failed(error, exec_error(failure.code), failure.code);
}
#else
// Under the spawn lock: the child started, or why it could not be.
static bool start_child(const ProcessSpec *spec, const char *program, char **environment, const int streams[3], pid_t *pid, ProcessError *error) {
    // posix_spawn gives one error for a directory it cannot start in and a
    // program it cannot run: which it was, asked first.
    if (spec->directory != NULL && (!is_directory(spec->directory) || access(spec->directory, X_OK) != 0)) {
        return process_failed(error, PROCESS_ERROR_DIRECTORY, errno);
    }

    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attributes;
    posix_spawn_file_actions_init(&actions);
    posix_spawnattr_init(&attributes);
    for (int i = 0; i < 3; i++) {
        if (streams[i] >= 0) {
            posix_spawn_file_actions_adddup2(&actions, streams[i], i);
        } else if (fcntl(i, F_GETFD) >= 0) {
            posix_spawn_file_actions_addinherit_np(&actions, i);
        }
    }

    if (spec->directory != NULL) {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        posix_spawn_file_actions_addchdir_np(&actions, spec->directory);
#pragma clang diagnostic pop
    }

    // No signal blocked, and SIGPIPE's default, which a program counts on;
    // what this program handles is reset by the exec.
    sigset_t none, defaults;
    sigemptyset(&none);
    sigemptyset(&defaults);
    sigaddset(&defaults, SIGPIPE);
    posix_spawnattr_setsigmask(&attributes, &none);
    posix_spawnattr_setsigdefault(&attributes, &defaults);
    posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_CLOEXEC_DEFAULT);
    int code;
    do {
        code = posix_spawn(pid, program, &actions, &attributes, spec->argv, environment);
    } while (code == EINTR);
    posix_spawnattr_destroy(&attributes);
    posix_spawn_file_actions_destroy(&actions);
    return code == 0 || process_failed(error, exec_error(code), code);
}
#endif

// A child's stream opened: nothing, a file, or its end of a pipe, whose
// other is put in 'pipe'. The stream is -1 for one inherited; false when it
// cannot be opened.
static bool open_stream(const ProcessSpec *spec, int which, int *stream, PlatformPipe *pipe, ProcessError *error) {
    switch (spec->stdio[which]) {
    case STDIO_INHERIT:
        return true;
    case STDIO_NULL:
    case STDIO_FILE: {
        bool null = spec->stdio[which] == STDIO_NULL;
        int mode = null ? O_RDWR : which == 0 ? O_RDONLY : O_WRONLY | O_CREAT | O_TRUNC;
        do {
            *stream = open(null ? "/dev/null" : spec->files[which], mode | O_CLOEXEC, 0666);
        } while (*stream < 0 && errno == EINTR);
        *stream = clear_of_streams(*stream);
        return *stream >= 0 || process_failed(error, PROCESS_ERROR_FILE, which);
    }
    default: {
        int ends[2];
        if (!make_pipe(ends)) {
            return process_failed(error, PROCESS_ERROR_OTHER, errno);
        }

        int own = which == 0 ? ends[1] : ends[0];
        *stream = clear_of_streams(which == 0 ? ends[0] : ends[1]);
        *pipe = own;
        // Each end has its own flags: the child's still blocks.
        return (*stream >= 0 && make_nonblocking(own)) || process_failed(error, PROCESS_ERROR_OTHER, errno);
    }
    }
}

static bool process_start(const ProcessSpec *spec, PlatformProcess *process, PlatformPipe pipes[3], ProcessError *error) {
    error->kind = PROCESS_ERROR_NONE;
    error->code = 0;
    pipes[0] = pipes[1] = pipes[2] = PIPE_NONE;
    char *program = find_program(spec->argv[0]);
    if (program == NULL) {
        bool there = strchr(spec->argv[0], '/') != NULL && file_exists(spec->argv[0]);
        return process_failed(error, there ? PROCESS_ERROR_PERMISSION : PROCESS_ERROR_NOT_FOUND, there ? EACCES : ENOENT);
    }

    program = absolute_path(program);

    // With SIGCHLD ignored, the system reaps children itself: how they
    // ended is never known, and their identifiers may be another's by the
    // time they are signalled.
    struct sigaction child_ended;
    if (sigaction(SIGCHLD, NULL, &child_ended) == 0 && (child_ended.sa_handler == SIG_IGN || (child_ended.sa_flags & SA_NOCLDWAIT))) {
        memset(&child_ended, 0, sizeof child_ended);
        child_ended.sa_handler = SIG_DFL;
        sigaction(SIGCHLD, &child_ended, NULL);
    }

    fflush(stdout);
    fflush(stderr);
    int streams[3] = { -1, -1, -1 };
    pid_t pid = -1;
    bool started = true;
    pthread_mutex_lock(&spawn_lock);
    for (int i = 0; i < 3 && started; i++) {
        // Output and errors to one file: one opening of it, and one
        // position, so that neither writes over the other.
        if (i == 2 && spec->stdio[1] == STDIO_FILE && spec->stdio[2] == STDIO_FILE && strcmp(spec->files[1], spec->files[2]) == 0) {
            streams[2] = fcntl(streams[1], F_DUPFD_CLOEXEC, 3);
            started = streams[2] >= 0 || process_failed(error, PROCESS_ERROR_OTHER, errno);
        } else {
            started = open_stream(spec, i, &streams[i], &pipes[i], error);
        }
    }

    started = started && start_child(spec, program, spec->environment != NULL ? spec->environment : environ, streams, &pid, error);
    pthread_mutex_unlock(&spawn_lock);
    close_all(streams, 3);
    free(program);
    if (!started) {
        for (int i = 0; i < 3; i++) {
            if (pipes[i] != PIPE_NONE) {
                pipe_close(pipes[i]);
                pipes[i] = PIPE_NONE;
            }
        }
    }

    process->id = pid;
    return started;
}

// ---- A started program -------------------------------------------------------

static void process_wait(const PlatformProcess *process) {
#if defined(__APPLE__)
    // macOS's waitid is not to be relied on to leave a child unreaped; its
    // kqueue says when one has ended, and that one already has (ESRCH).
    int queue = kqueue();
    if (queue >= 0) {
        fcntl(queue, F_SETFD, FD_CLOEXEC);
        struct kevent ended, reported;
        EV_SET(&ended, (uintptr_t)process->id, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0, NULL);
        while (kevent(queue, &ended, 1, &reported, 1, NULL) < 0 && errno == EINTR) {
        }

        close(queue);
    }
#else
    siginfo_t info;
    while (waitid(P_PID, (id_t)process->id, &info, WEXITED | WNOWAIT) < 0 && errno == EINTR) {
    }
#endif
}

static void process_reap(PlatformProcess *process, int64_t *code, int64_t *signal) {
    int ended = 0;
    pid_t reaped;
    do {
        reaped = waitpid((pid_t)process->id, &ended, 0);
    } while (reaped < 0 && errno == EINTR);

    if (reaped < 0) {
        // Reaped by something else in the program: how it ended is lost.
        *code = -1;
        *signal = 0;
    } else if (WIFSIGNALED(ended)) {
        *signal = WTERMSIG(ended);
        *code = 128 + *signal;
    } else {
        *signal = 0;
        *code = WEXITSTATUS(ended);
    }
}

static bool process_signal(const PlatformProcess *process, int signal) {
    return kill((pid_t)process->id, signal) == 0;
}

// The process's children now; how many, in memory the caller frees.
static pid_t *children_of(pid_t parent, size_t *count) {
    size_t capacity = 64;
    pid_t *children = malloc(capacity * sizeof(pid_t));
    *count = 0;
#if defined(__APPLE__)
    while (children != NULL) {
        int bytes = proc_listpids(PROC_PPID_ONLY, (uint32_t)parent, children, (int)(capacity * sizeof(pid_t)));
        if (bytes <= 0) {
            break;
        }

        if ((size_t)bytes >= capacity * sizeof(pid_t)) {
            // As many as there was room for, so perhaps more.
            capacity *= 2;
            free(children);
            children = malloc(capacity * sizeof(pid_t));
            continue;
        }

        // Asked of each again: what is killed is the parent's, surely.
        for (size_t i = 0; i < (size_t)bytes / sizeof(pid_t); i++) {
            struct proc_bsdshortinfo info;
            if (children[i] > 1 && proc_pidinfo(children[i], PROC_PIDT_SHORTBSDINFO, 0, &info, sizeof info) == (int)sizeof info
                && info.pbsi_ppid == (uint32_t)parent) {
                children[(*count)++] = children[i];
            }
        }

        break;
    }
#else
    DIR *processes = children == NULL ? NULL : opendir("/proc");
    if (processes == NULL) {
        return children;
    }

    struct dirent *entry;
    while ((entry = readdir(processes)) != NULL) {
        char *end;
        long id = strtol(entry->d_name, &end, 10);
        if (id <= 0 || *end != '\0') {
            continue;
        }

        char name[64], line[512];
        snprintf(name, sizeof name, "/proc/%ld/stat", id);
        FILE *status = fopen(name, "re");
        if (status == NULL) {
            continue;
        }

        size_t length = fread(line, 1, sizeof line - 1, status);
        fclose(status);
        line[length] = '\0';
        // "id (name) state parent ...", and a name may hold anything.
        char *after = strrchr(line, ')');
        char state;
        long owner;
        if (after == NULL || sscanf(after + 1, " %c %ld", &state, &owner) != 2 || owner != (long)parent) {
            continue;
        }

        if (*count == capacity) {
            pid_t *grown = realloc(children, capacity * 2 * sizeof(pid_t));
            if (grown == NULL) {
                break;
            }

            capacity *= 2;
            children = grown;
        }

        children[(*count)++] = (pid_t)id;
    }

    closedir(processes);
#endif
    return children;
}

// Stopped first: a stopped process starts no more, and reaps none, so that
// its children's identifiers stay theirs while they are killed.
static void kill_tree(pid_t root) {
    if (root <= 1 || kill(root, SIGSTOP) != 0) {
        return;
    }

    size_t count;
    pid_t *children = children_of(root, &count);
    for (size_t i = 0; i < count; i++) {
        kill_tree(children[i]);
    }

    free(children);
    kill(root, SIGKILL);
}

static bool process_kill_tree(const PlatformProcess *process) {
    kill_tree((pid_t)process->id);
    return true;
}

// ---- Signals ---------------------------------------------------------------

// The handler notes which signal came and writes a byte to a pipe, the two
// things it may do; a thread reads the pipe and says which, as .NET's does.
// A full pipe has a wake in it already.
static int signal_pipe[2] = { -1, -1 };
static pid_t signal_owner;
static int signal_came[NSIG];
static struct sigaction signal_before[NSIG];
static bool signal_caught[NSIG];

static void on_signal(int number) {
    int error = errno;
    // Not in a child something else forked, which has the pipe too.
    if (getpid() == signal_owner) {
        __atomic_store_n(&signal_came[number], 1, __ATOMIC_SEQ_CST);
        ssize_t put = write(signal_pipe[1], "", 1);
        (void)put;
    }

    errno = error;
}

static void signal_thread(void *argument) {
    (void)argument;
    for (;;) {
        char bytes[64];
        ssize_t got = read(signal_pipe[0], bytes, sizeof bytes);
        if (got <= 0) {
            if (got < 0 && errno == EINTR) {
                continue;
            }

            return;
        }

        // Cleared first: one that comes as it is told of is told of again.
        for (int number = 1; number < NSIG; number++) {
            if (__atomic_exchange_n(&signal_came[number], 0, __ATOMIC_SEQ_CST)) {
                signal_arrived(number);
            }
        }
    }
}

// Both under process.c's lock.
static bool signal_catch(int number) {
    if (number <= 0 || number >= NSIG || number == SIGKILL || number == SIGSTOP || number == SIGSEGV || number == SIGBUS
        || number == SIGILL || number == SIGFPE || number == SIGABRT || number == SIGTRAP) {
        return false;
    }

    if (signal_caught[number]) {
        return true;
    }

    if (signal_pipe[0] < 0) {
        pthread_mutex_lock(&spawn_lock);
        bool made = make_pipe(signal_pipe);
        pthread_mutex_unlock(&spawn_lock);
        if (!made) {
            signal_pipe[0] = signal_pipe[1] = -1;
            return false;
        }

        make_nonblocking(signal_pipe[1]);
        signal_owner = getpid();
        if (!thread_start(signal_thread, NULL)) {
            close_all(signal_pipe, 2);
            return false;
        }
    }

    struct sigaction action;
    memset(&action, 0, sizeof action);
    action.sa_handler = on_signal;
    action.sa_flags = SA_RESTART | SA_ONSTACK;
    sigfillset(&action.sa_mask);
    signal_caught[number] = sigaction(number, &action, &signal_before[number]) == 0;
    return signal_caught[number];
}

static void signal_let_go(int number) {
    if (number > 0 && number < NSIG && signal_caught[number]) {
        signal_caught[number] = false;
        sigaction(number, &signal_before[number], NULL);
        terminal_watch();
    }
}

static bool signal_raise(int number) {
    return kill(getpid(), number) == 0;
}
