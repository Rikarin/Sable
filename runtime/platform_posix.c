// The Sable runtime's platform layer for POSIX systems — macOS and Linux:
// what platform.h says each platform gives the rest.

#include <dirent.h>
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <spawn.h>
#include <sys/mman.h>
#include <sys/random.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#if defined(__linux__)
#include <link.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#else
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <sys/event.h>
#include <sys/sysctl.h>
#endif

extern char **environ;

// A destructor: run as the program exits, after main returns or exit is called.
#define SABLE_AT_EXIT(name) __attribute__((destructor)) static void name(void)

// ---- Traps -------------------------------------------------------------------

// The return addresses of the calls that led here, this function's caller's
// first.
__attribute__((noinline)) static int capture_frames(void **frames, int capacity) {
    void *all[BACKTRACE_DEPTH + 1];
    int count = backtrace(all, capacity + 1 < BACKTRACE_DEPTH + 1 ? capacity + 1 : BACKTRACE_DEPTH + 1);
    // The first is this function.
    for (int i = 1; i < count; i++) {
        frames[i - 1] = all[i];
    }

    return count > 0 ? count - 1 : 0;
}

// A frame as 'sable.Module.function at file:line:column' where the line
// table places it (a debug build's), 'sable.Module.function + offset' where
// only the symbol is known, its address otherwise. The return address is
// looked up one byte back: after a call that never returns, it is past the
// end of the caller, inside whatever comes next; one byte back is in the
// call, whose line is the one that made it.
static void name_frame(void *frame, char *text, size_t size) {
    const char *at = (const char *)frame - 1;
    Dl_info info;
    char place[300];
    if (dladdr(at, &info) != 0 && info.dli_sname != NULL) {
        if (frame_place(at, info.dli_fbase, place, sizeof place)) {
            snprintf(text, size, "%s at %s", info.dli_sname, place);
        } else {
            snprintf(text, size, "%s + %ld", info.dli_sname, (long)(at + 1 - (const char *)info.dli_saddr));
        }
    } else {
        snprintf(text, size, "%p", frame);
    }
}

// The whole of a file, mapped read-only for as long as the program runs;
// NULL when it cannot be.
static const uint8_t *map_whole_file(const char *path, size_t *size) {
    int file = open(path, O_RDONLY | O_CLOEXEC);
    if (file < 0) {
        return NULL;
    }

    struct stat status;
    void *mapped = MAP_FAILED;
    if (fstat(file, &status) == 0 && status.st_size > 0) {
        mapped = mmap(NULL, (size_t)status.st_size, PROT_READ, MAP_PRIVATE, file, 0);
    }

    close(file);
    if (mapped == MAP_FAILED) {
        return NULL;
    }

    *size = (size_t)status.st_size;
    return mapped;
}

// A section of a mapped file, if it lies inside it.
static bool section_in(const uint8_t *file, size_t file_size, uint64_t offset, uint64_t size, const uint8_t **section, size_t *section_size) {
    if (offset > file_size || size > file_size - offset) {
        return false;
    }

    *section = file + offset;
    *section_size = (size_t)size;
    return true;
}

#if defined(__APPLE__)

// The program's own UUID, which its .dSYM was written with: LC_UUID in the
// load commands that follow its header in memory.
static const uint8_t *image_uuid(const struct mach_header_64 *header) {
    const uint8_t *command = (const uint8_t *)(header + 1);
    for (uint32_t i = 0; i < header->ncmds; i++) {
        const struct load_command *load = (const struct load_command *)command;
        if (load->cmd == LC_UUID) {
            return ((const struct uuid_command *)load)->uuid;
        }

        command += load->cmdsize;
    }

    return NULL;
}

// The line table of a debug build on macOS: in the .dSYM clang leaves
// beside the program (dsymutil's), 'program.dSYM/Contents/Resources/DWARF/
// program', a Mach-O file whose __DWARF segment holds it. Only one of this
// very build — its UUID the program's — is read.
static bool line_sections(LineSections *sections) {
    const struct mach_header_64 *image = (const struct mach_header_64 *)_dyld_get_image_header(0);
    const uint8_t *uuid = image != NULL && image->magic == MH_MAGIC_64 ? image_uuid(image) : NULL;
    char path[PATH_MAX], real[PATH_MAX], dsym[PATH_MAX + 64];
    uint32_t length = sizeof path;
    if (uuid == NULL || _NSGetExecutablePath(path, &length) != 0 || realpath(path, real) == NULL) {
        return false;
    }

    const char *name = strrchr(real, '/');
    snprintf(dsym, sizeof dsym, "%s.dSYM/Contents/Resources/DWARF/%s", real, name != NULL ? name + 1 : real);
    size_t size = 0;
    const uint8_t *file = map_whole_file(dsym, &size);
    if (file == NULL) {
        return false;
    }

    const struct mach_header_64 *header = (const struct mach_header_64 *)file;
    if (size < sizeof *header || header->magic != MH_MAGIC_64 || header->sizeofcmds > size - sizeof *header) {
        return false;
    }

    bool same = false;
    const uint8_t *command = file + sizeof *header;
    const uint8_t *commands_end = command + header->sizeofcmds;
    for (uint32_t i = 0; i < header->ncmds && command + sizeof(struct load_command) <= commands_end; i++) {
        const struct load_command *load = (const struct load_command *)command;
        if (load->cmdsize < sizeof *load || load->cmdsize > (size_t)(commands_end - command)) {
            return false;
        }

        if (load->cmd == LC_UUID && load->cmdsize >= sizeof(struct uuid_command)) {
            same = memcmp(((const struct uuid_command *)load)->uuid, uuid, 16) == 0;
        } else if (load->cmd == LC_SEGMENT_64 && load->cmdsize >= sizeof(struct segment_command_64)) {
            const struct segment_command_64 *segment = (const struct segment_command_64 *)load;
            const struct section_64 *section = (const struct section_64 *)(segment + 1);
            if (strncmp(segment->segname, "__DWARF", 16) == 0
                && segment->nsects <= (load->cmdsize - sizeof *segment) / sizeof *section) {
                for (uint32_t s = 0; s < segment->nsects; s++) {
                    if (strncmp(section[s].sectname, "__debug_line", 16) == 0) {
                        section_in(file, size, section[s].offset, section[s].size, &sections->line, &sections->line_size);
                    } else if (strncmp(section[s].sectname, "__debug_line_str", 16) == 0) {
                        section_in(file, size, section[s].offset, section[s].size, &sections->line_str, &sections->line_str_size);
                    } else if (strncmp(section[s].sectname, "__debug_str", 16) == 0) {
                        section_in(file, size, section[s].offset, section[s].size, &sections->str, &sections->str_size);
                    }
                }
            }
        }

        command += load->cmdsize;
    }

    sections->slide = _dyld_get_image_vmaddr_slide(0);
    sections->image = image;
    return same && sections->line != NULL;
}

#else

// Where the program itself is loaded: the first object dl_iterate_phdr
// gives, its load bias, and the start of its lowest segment, which is
// what dladdr says an address of it is in.
static int find_program_image(struct dl_phdr_info *info, size_t size, void *data) {
    (void)size;
    LineSections *sections = data;
    uintptr_t lowest = UINTPTR_MAX;
    for (int i = 0; i < info->dlpi_phnum; i++) {
        if (info->dlpi_phdr[i].p_type == PT_LOAD && info->dlpi_phdr[i].p_vaddr < lowest) {
            lowest = info->dlpi_phdr[i].p_vaddr;
        }
    }

    // A segment is mapped from the start of its page.
    lowest = lowest == UINTPTR_MAX ? 0 : lowest & ~(uintptr_t)4095;
    sections->slide = (intptr_t)info->dlpi_addr;
    sections->image = (const void *)(info->dlpi_addr + lowest);
    return 1;
}

// The line table of a debug build on Linux: in the program's own ELF
// file, its .debug_line section. A section compressed (-gz) is not read.
static bool line_sections(LineSections *sections) {
    size_t size = 0;
    const uint8_t *file = map_whole_file("/proc/self/exe", &size);
    if (file == NULL) {
        return false;
    }

    const Elf64_Ehdr *header = (const Elf64_Ehdr *)file;
    if (size < sizeof *header || memcmp(header->e_ident, ELFMAG, SELFMAG) != 0 || header->e_ident[EI_CLASS] != ELFCLASS64
        || header->e_shentsize != sizeof(Elf64_Shdr) || header->e_shoff > size
        || (uint64_t)header->e_shnum * sizeof(Elf64_Shdr) > size - header->e_shoff || header->e_shstrndx >= header->e_shnum) {
        return false;
    }

    const Elf64_Shdr *table = (const Elf64_Shdr *)(file + header->e_shoff);
    const uint8_t *names;
    size_t names_size;
    if (!section_in(file, size, table[header->e_shstrndx].sh_offset, table[header->e_shstrndx].sh_size, &names, &names_size)) {
        return false;
    }

    for (int i = 0; i < header->e_shnum; i++) {
        const char *name = line_string_at(names, names_size, table[i].sh_name);
        if (name == NULL || (table[i].sh_flags & SHF_COMPRESSED) != 0 || table[i].sh_type == SHT_NOBITS) {
            continue;
        }

        if (strcmp(name, ".debug_line") == 0) {
            section_in(file, size, table[i].sh_offset, table[i].sh_size, &sections->line, &sections->line_size);
        } else if (strcmp(name, ".debug_line_str") == 0) {
            section_in(file, size, table[i].sh_offset, table[i].sh_size, &sections->line_str, &sections->line_str_size);
        } else if (strcmp(name, ".debug_str") == 0) {
            section_in(file, size, table[i].sh_offset, table[i].sh_size, &sections->str, &sections->str_size);
        }
    }

    dl_iterate_phdr(find_program_image, sections);
    return sections->line != NULL && sections->image != NULL;
}

#endif

_Noreturn static void stop_trapped(void) {
    abort();
}

// ---- The debugger --------------------------------------------------------

// Whether a debugger is tracing the process now (Debugger.isAttached): on
// macOS the kernel's P_TRACED flag for it, on Linux the TracerPid its
// /proc/self/status gives. Asked each time, as one may attach or leave.
bool sable_debugger_attached(void) {
#if defined(__APPLE__)
    int name[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid() };
    struct kinfo_proc info;
    memset(&info, 0, sizeof info);
    size_t size = sizeof info;
    if (sysctl(name, 4, &info, &size, NULL, 0) != 0) {
        return false;
    }

    return (info.kp_proc.p_flag & P_TRACED) != 0;
#else
    FILE *status = fopen("/proc/self/status", "r");
    if (status == NULL) {
        return false;
    }

    char line[256];
    long tracer = 0;
    while (fgets(line, sizeof line, status) != NULL) {
        if (strncmp(line, "TracerPid:", 10) == 0) {
            tracer = strtol(line + 10, NULL, 10);
            break;
        }
    }

    fclose(status);
    return tracer != 0;
#endif
}

// ---- Stack overflow ------------------------------------------------------

// Recursion that runs off the end of a thread's stack touches the guard page
// below it, and the system sends SIGSEGV (SIGBUS on some). A handler can
// only run on a stack that is still there, so each thread is given a small
// one of its own (sigaltstack). The handler tells an overflow from any other
// bad access by where it was: at the low end of the thread's stack, which
// each thread notes as it starts.
static _Thread_local char *stack_low;

enum { SIGNAL_STACK = 64 * 1024, GUARD_SLACK = 64 * 1024, FRAME_SLACK = 1 << 20 };

// The lowest address of the calling thread's stack.
static char *thread_stack_low(void) {
#if defined(__APPLE__)
    pthread_t self = pthread_self();
    return (char *)pthread_get_stackaddr_np(self) - pthread_get_stacksize_np(self);
#else
    pthread_attr_t attributes;
    void *low = NULL;
    size_t size = 0;
    if (pthread_getattr_np(pthread_self(), &attributes) == 0) {
        pthread_attr_getstack(&attributes, &low, &size);
        pthread_attr_destroy(&attributes);
    }

    return low;
#endif
}

static void on_fault(int signal_number, siginfo_t *info, void *context) {
    (void)context;
    char *address = info->si_addr;

    // Just below the stack — a frame larger than the guard page reaches
    // further — or in its last few pages.
    if (stack_low != NULL && address >= stack_low - FRAME_SLACK && address < stack_low + GUARD_SLACK) {
        // What the program printed before is its output, as before any trap.
        fflush(stdout);
        static const char message[] = "fatal error: stack overflow: calls nested too deeply\n";
        ssize_t written = write(STDERR_FILENO, message, sizeof message - 1);
        (void)written;
        signal(SIGABRT, SIG_DFL);
        abort();
    }

    // Not an overflow: what the signal does without a handler, as the
    // faulting instruction runs again.
    signal(signal_number, SIG_DFL);
}

// The main thread's stack for the handler, for as long as the program runs.
static _Alignas(16) char main_signal_stack[SIGNAL_STACK];

// Gives the calling thread — the main one, or a worker of the pool as it
// starts — a stack for the handler, and notes where its own stack ends.
static void *watch_stack_on(char *memory) {
    stack_low = thread_stack_low();
    stack_t alternate = { 0 };
    alternate.ss_sp = memory;
    alternate.ss_size = SIGNAL_STACK;
    if (alternate.ss_sp != NULL) {
        sigaltstack(&alternate, NULL);
    }

    return alternate.ss_sp;
}

static void platform_start(void) {
    watch_stack_on(main_signal_stack);
    struct sigaction action = { 0 };
    action.sa_sigaction = on_fault;
    action.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&action.sa_mask);
    sigaction(SIGSEGV, &action, NULL);
    sigaction(SIGBUS, &action, NULL);
}

// ---- Memory ----------------------------------------------------------------

// The system's mapping is aligned to a page, not to the slab: twice the size
// is mapped, and what lies outside the aligned slab within it unmapped.
static void *map_slab(size_t size) {
    char *mapped = mmap(NULL, size * 2, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (mapped == MAP_FAILED) {
        return NULL;
    }

    uintptr_t start = ((uintptr_t)mapped + size - 1) & ~(uintptr_t)(size - 1);
    size_t before = start - (uintptr_t)mapped;
    if (before > 0) {
        munmap(mapped, before);
    }

    if (size - before > 0) {
        munmap((char *)start + size, size - before);
    }

    return (void *)start;
}

static void unmap_slab(void *slab, size_t size) {
    munmap(slab, size);
}

// The most this program's memory has held. On Linux, getrusage's ru_maxrss
// is the wrong measure: it survives execve, and exec sets it from the memory
// the process had before — after a fork that is the parent's resident set,
// and after a vfork (what .NET's Process.Start and posix_spawn use) the
// parent's memory itself. A program started by a test host of 270 MB says
// it peaked at 270 MB, whatever it used. VmHWM in /proc/self/status is the
// high-water mark of this program's own memory, which exec made new.
static int64_t peak_memory(void) {
#if defined(__linux__)
    FILE *status = fopen("/proc/self/status", "r");
    if (status != NULL) {
        char line[256];
        long long kib = -1;
        while (fgets(line, sizeof line, status) != NULL) {
            if (sscanf(line, "VmHWM: %lld kB", &kib) == 1) {
                break;
            }
        }

        fclose(status);
        if (kib >= 0) {
            return (int64_t)kib * 1024;
        }
    }
#endif

    struct rusage usage;
    if (getrusage(RUSAGE_SELF, &usage) != 0) {
        return 0;
    }

#ifdef __APPLE__
    return (int64_t)usage.ru_maxrss; // bytes
#else
    return (int64_t)usage.ru_maxrss * 1024; // kilobytes
#endif
}

// ---- Time (Sable.Time's floor) ------------------------------------------

int64_t sable_time_monotonic(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

int64_t sable_time_wall_clock(void) {
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
}

// At least as long as asked: nanosleep resumes after a signal.
void sable_time_sleep(int64_t nanoseconds) {
    if (nanoseconds <= 0) {
        return;
    }

    struct timespec wait = { (time_t)(nanoseconds / 1000000000), (long)(nanoseconds % 1000000000) };
    while (nanosleep(&wait, &wait) != 0 && errno == EINTR) {
    }
}

// ---- Threads ---------------------------------------------------------------

static void mutex_init(PlatformMutex *mutex) {
    pthread_mutex_init(mutex, NULL);
}

static void mutex_lock(PlatformMutex *mutex) {
    pthread_mutex_lock(mutex);
}

static void mutex_unlock(PlatformMutex *mutex) {
    pthread_mutex_unlock(mutex);
}

// A timed wait counts on the monotonic clock: macOS waits for a relative
// time, and elsewhere the condition is made to use that clock.
static void condition_init(PlatformCondition *condition) {
#if defined(__APPLE__)
    pthread_cond_init(condition, NULL);
#else
    pthread_condattr_t attributes;
    pthread_condattr_init(&attributes);
    pthread_condattr_setclock(&attributes, CLOCK_MONOTONIC);
    pthread_cond_init(condition, &attributes);
    pthread_condattr_destroy(&attributes);
#endif
}

static void condition_wait(PlatformCondition *condition, PlatformMutex *mutex, int64_t until) {
    if (until < 0) {
        pthread_cond_wait(condition, mutex);
        return;
    }

#if defined(__APPLE__)
    int64_t left = until - sable_time_monotonic();
    if (left <= 0) {
        return;
    }

    struct timespec wait = { (time_t)(left / 1000000000), (long)(left % 1000000000) };
    pthread_cond_timedwait_relative_np(condition, mutex, &wait);
#else
    struct timespec deadline = { (time_t)(until / 1000000000), (long)(until % 1000000000) };
    pthread_cond_timedwait(condition, mutex, &deadline);
#endif
}

static void condition_signal(PlatformCondition *condition) {
    pthread_cond_signal(condition);
}

static void condition_broadcast(PlatformCondition *condition) {
    pthread_cond_broadcast(condition);
}

typedef struct ThreadStart {
    void (*run)(void *argument);
    void *argument;
} ThreadStart;

static void *thread_main(void *pointer) {
    ThreadStart start = *(ThreadStart *)pointer;
    free(pointer);
    // A stack for the overflow handler, for as long as the thread runs.
    watch_stack_on(malloc(SIGNAL_STACK));
    start.run(start.argument);
    return NULL;
}

static bool thread_start(void (*run)(void *argument), void *argument) {
    ThreadStart *start = malloc(sizeof *start);
    if (start == NULL) {
        return false;
    }

    start->run = run;
    start->argument = argument;
    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    // As large as the main thread's usually is: a task's calls run here.
    pthread_attr_setstacksize(&attributes, 8 * 1024 * 1024);
    pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
    pthread_t thread;
    bool started = pthread_create(&thread, &attributes, thread_main, start) == 0;
    pthread_attr_destroy(&attributes);
    if (!started) {
        free(start);
    }

    return started;
}

static int processor_count(void) {
    long count = sysconf(_SC_NPROCESSORS_ONLN);
    return count > 0 ? (int)count : 1;
}

static void thread_yield(void) {
    sched_yield();
}

// ---- The I/O reactor -------------------------------------------------------

// kqueue where there is one (macOS, the BSDs), epoll on Linux. Sockets are
// watched edge-triggered for both directions (EV_CLEAR, EPOLLET), and a
// wake is a user event (EVFILT_USER) or an eventfd.
static int reactor_fd = -1;

#if defined(__linux__)
static int reactor_waker = -1;

static bool reactor_open(void) {
    reactor_fd = epoll_create1(EPOLL_CLOEXEC);
    reactor_waker = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
    if (reactor_fd < 0 || reactor_waker < 0) {
        return false;
    }

    struct epoll_event event = { .events = EPOLLIN | EPOLLET, .data.ptr = NULL };
    return epoll_ctl(reactor_fd, EPOLL_CTL_ADD, reactor_waker, &event) == 0;
}

static bool reactor_add(PlatformSocket socket, void *key) {
    struct epoll_event event = { .events = EPOLLIN | EPOLLOUT | EPOLLRDHUP | EPOLLET, .data.ptr = key };
    return epoll_ctl(reactor_fd, EPOLL_CTL_ADD, (int)socket, &event) == 0;
}

static void reactor_remove(PlatformSocket socket) {
    epoll_ctl(reactor_fd, EPOLL_CTL_DEL, (int)socket, NULL);
}

static int reactor_wait(ReactorEvent *events, int capacity, int64_t until) {
    struct epoll_event ready[64];
    if (capacity > 64) {
        capacity = 64;
    }

    int timeout = -1;
    if (until >= 0) {
        int64_t left = until - sable_time_monotonic();
        // Milliseconds, rounded up: never woken before the deadline.
        timeout = left <= 0 ? 0 : left / 1000000 >= INT32_MAX ? INT32_MAX : (int)((left + 999999) / 1000000);
    }

    int count = epoll_wait(reactor_fd, ready, capacity, timeout);
    int n = 0;
    for (int i = 0; i < count; i++) {
        if (ready[i].data.ptr == NULL) {
            uint64_t ignored;
            ssize_t got = read(reactor_waker, &ignored, sizeof ignored);
            (void)got;
            continue;
        }

        uint32_t what = ready[i].events;
        events[n].key = ready[i].data.ptr;
        // A hang-up or an error is readiness too: the call made next says what it is.
        events[n].events = (what & (EPOLLIN | EPOLLRDHUP | EPOLLHUP | EPOLLERR) ? REACTOR_READABLE : 0)
                           | (what & (EPOLLOUT | EPOLLHUP | EPOLLERR) ? REACTOR_WRITABLE : 0);
        n++;
    }

    return n;
}

static void reactor_wake(void) {
    uint64_t one = 1;
    ssize_t written = write(reactor_waker, &one, sizeof one);
    (void)written;
}
#else
static bool reactor_open(void) {
    reactor_fd = kqueue();
    if (reactor_fd < 0) {
        return false;
    }

    fcntl(reactor_fd, F_SETFD, FD_CLOEXEC);
    struct kevent wake;
    EV_SET(&wake, 0, EVFILT_USER, EV_ADD | EV_CLEAR, 0, 0, NULL);
    return kevent(reactor_fd, &wake, 1, NULL, 0, NULL) == 0;
}

static bool reactor_add(PlatformSocket socket, void *key) {
    struct kevent changes[2];
    EV_SET(&changes[0], (uintptr_t)socket, EVFILT_READ, EV_ADD | EV_CLEAR, 0, 0, key);
    EV_SET(&changes[1], (uintptr_t)socket, EVFILT_WRITE, EV_ADD | EV_CLEAR, 0, 0, key);
    return kevent(reactor_fd, changes, 2, NULL, 0, NULL) == 0;
}

static void reactor_remove(PlatformSocket socket) {
    struct kevent changes[2];
    EV_SET(&changes[0], (uintptr_t)socket, EVFILT_READ, EV_DELETE, 0, 0, NULL);
    EV_SET(&changes[1], (uintptr_t)socket, EVFILT_WRITE, EV_DELETE, 0, 0, NULL);
    kevent(reactor_fd, changes, 2, NULL, 0, NULL);
}

static int reactor_wait(ReactorEvent *events, int capacity, int64_t until) {
    struct kevent ready[64];
    if (capacity > 64) {
        capacity = 64;
    }

    struct timespec timeout, *wait = NULL;
    if (until >= 0) {
        int64_t left = until - sable_time_monotonic();
        if (left < 0) {
            left = 0;
        }

        timeout.tv_sec = (time_t)(left / 1000000000);
        timeout.tv_nsec = (long)(left % 1000000000);
        wait = &timeout;
    }

    int count = kevent(reactor_fd, NULL, 0, ready, capacity, wait);
    int n = 0;
    for (int i = 0; i < count; i++) {
        if (ready[i].filter == EVFILT_USER) {
            continue;
        }

        // The two directions of one socket come as two events: one each.
        events[n].key = ready[i].udata;
        events[n].events = ready[i].filter == EVFILT_READ ? REACTOR_READABLE : REACTOR_WRITABLE;
        n++;
    }

    return n;
}

static void reactor_wake(void) {
    struct kevent wake;
    EV_SET(&wake, 0, EVFILT_USER, 0, NOTE_TRIGGER, 0, NULL);
    kevent(reactor_fd, &wake, 1, NULL, 0, NULL);
}
#endif

// ---- Sockets -----------------------------------------------------------------

// Nonblocking, closed on exec, and never a SIGPIPE: a write to a closed
// connection fails instead.
static bool socket_prepare(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0) {
        return false;
    }

    fcntl(fd, F_SETFD, FD_CLOEXEC);
#ifdef SO_NOSIGPIPE
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#endif
    return true;
}

#ifdef MSG_NOSIGNAL
#define SOCKET_SEND_FLAGS MSG_NOSIGNAL
#else
#define SOCKET_SEND_FLAGS 0
#endif

static struct addrinfo *socket_resolve(const char *host, int port, bool passive) {
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
    int fd = -1;
    for (struct addrinfo *at = found; at != NULL && fd < 0; at = at->ai_next) {
        fd = socket(at->ai_family, at->ai_socktype, at->ai_protocol);
        if (fd < 0) {
            continue;
        }

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
        if (!socket_prepare(fd) || bind(fd, at->ai_addr, at->ai_addrlen) != 0 || listen(fd, SOMAXCONN) != 0) {
            close(fd);
            fd = -1;
        }
    }

    if (found != NULL) {
        freeaddrinfo(found);
    }

    if (fd < 0) {
        return IO_FAILED;
    }

    struct sockaddr_storage address;
    socklen_t length = sizeof address;
    getsockname(fd, (struct sockaddr *)&address, &length);
    *bound = address.ss_family == AF_INET6 ? ntohs(((struct sockaddr_in6 *)&address)->sin6_port)
                                           : ntohs(((struct sockaddr_in *)&address)->sin_port);
    return fd;
}

static PlatformSocket socket_accept(PlatformSocket listener) {
    for (;;) {
        int fd = accept((int)listener, NULL, NULL);
        if (fd >= 0) {
            if (!socket_prepare(fd)) {
                close(fd);
                return IO_FAILED;
            }

            int one = 1;
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
            return fd;
        }

        if (errno == EINTR || errno == ECONNABORTED) {
            continue;
        }

        return errno == EAGAIN || errno == EWOULDBLOCK ? IO_WOULD_BLOCK : IO_FAILED;
    }
}

static PlatformSocket socket_connect(const char *host, int port) {
    struct addrinfo *found = socket_resolve(host, port, false);
    int fd = -1;
    for (struct addrinfo *at = found; at != NULL && fd < 0; at = at->ai_next) {
        fd = socket(at->ai_family, at->ai_socktype, at->ai_protocol);
        if (fd < 0) {
            continue;
        }

        if (!socket_prepare(fd) || (connect(fd, at->ai_addr, at->ai_addrlen) != 0 && errno != EINPROGRESS)) {
            close(fd);
            fd = -1;
        }
    }

    if (found != NULL) {
        freeaddrinfo(found);
    }

    if (fd < 0) {
        return IO_FAILED;
    }

    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    return fd;
}

static int socket_connected(PlatformSocket socket) {
    int error = 0;
    socklen_t length = sizeof error;
    if (getsockopt((int)socket, SOL_SOCKET, SO_ERROR, &error, &length) != 0 || error != 0) {
        return IO_FAILED;
    }

    // Writable with no error yet is not always made: a peer says it is.
    struct sockaddr_storage peer;
    socklen_t peer_length = sizeof peer;
    return getpeername((int)socket, (struct sockaddr *)&peer, &peer_length) == 0 ? 0 : IO_FAILED;
}

static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size) {
    for (;;) {
        ssize_t got = recv((int)socket, bytes, (size_t)size, 0);
        if (got >= 0) {
            return got;
        }

        if (errno != EINTR) {
            return errno == EAGAIN || errno == EWOULDBLOCK ? IO_WOULD_BLOCK : IO_FAILED;
        }
    }
}

static int64_t socket_write(PlatformSocket socket, const void *bytes, int64_t size) {
    for (;;) {
        ssize_t sent = send((int)socket, bytes, (size_t)size, SOCKET_SEND_FLAGS);
        if (sent >= 0) {
            return sent;
        }

        if (errno != EINTR) {
            return errno == EAGAIN || errno == EWOULDBLOCK ? IO_WOULD_BLOCK : IO_FAILED;
        }
    }
}

static void socket_shutdown_write(PlatformSocket socket) {
    shutdown((int)socket, SHUT_WR);
}

static void socket_close(PlatformSocket socket) {
    close((int)socket);
}

// ---- Arguments, files and the environment ----------------------------------

static char **platform_arguments(int argc, char **argv, int *count) {
    *count = argc;
    return argv;
}

static FILE *open_file(const char *name, const char *mode) {
    return fopen(name, mode);
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

// Runs the program to its end: the input written to it and its output and
// errors read as they come, all three at once, so that neither side waits
// on a full pipe.
static bool run_program(char **argv, const char *input, int64_t input_length, Buffer *output, Buffer *errors, int64_t *status) {
    char *found = find_program(argv[0]);
    if (found == NULL) {
        return false;
    }

    int in[2], out[2], err[2];
    bool piped = pipe(in) == 0;
    piped = piped && pipe(out) == 0;
    piped = piped && pipe(err) == 0;

    pid_t child = 0;
    int spawned = -1;
    if (piped) {
        posix_spawn_file_actions_t actions;
        posix_spawn_file_actions_init(&actions);
        posix_spawn_file_actions_adddup2(&actions, in[0], 0);
        posix_spawn_file_actions_adddup2(&actions, out[1], 1);
        posix_spawn_file_actions_adddup2(&actions, err[1], 2);
        int ends[] = { in[0], in[1], out[0], out[1], err[0], err[1] };
        for (int i = 0; i < 6; i++) {
            posix_spawn_file_actions_addclose(&actions, ends[i]);
        }

        fflush(stdout);
        fflush(stderr);
        spawned = posix_spawn(&child, found, &actions, NULL, argv, environ);
        posix_spawn_file_actions_destroy(&actions);
        close(in[0]);
        close(out[1]);
        close(err[1]);
    }

    free(found);

    if (spawned != 0) {
        if (piped) {
            close(in[1]);
            close(out[0]);
            close(err[0]);
        }

        return false;
    }

    int64_t written = 0;
    int writing = in[1];
    if (input_length == 0) {
        close(writing);
        writing = -1;
    }

    int reading_out = out[0], reading_err = err[0];
    char chunk[4096];
    while (reading_out >= 0 || reading_err >= 0) {
        struct pollfd watched[3];
        int n = 0;
        if (writing >= 0) {
            watched[n++] = (struct pollfd){ writing, POLLOUT, 0 };
        }

        if (reading_out >= 0) {
            watched[n++] = (struct pollfd){ reading_out, POLLIN, 0 };
        }

        if (reading_err >= 0) {
            watched[n++] = (struct pollfd){ reading_err, POLLIN, 0 };
        }

        if (poll(watched, (nfds_t)n, -1) < 0) {
            if (errno == EINTR) {
                continue;
            }

            break;
        }

        for (int i = 0; i < n; i++) {
            if (watched[i].revents == 0) {
                continue;
            }

            int fd = watched[i].fd;
            if (fd == writing) {
                ssize_t sent = write(writing, input + written, (size_t)(input_length - written));
                if (sent > 0) {
                    written += sent;
                }

                // Done, or it stopped reading: either way, its input ends.
                if (sent <= 0 || written == input_length) {
                    close(writing);
                    writing = -1;
                }

                continue;
            }

            ssize_t got = read(fd, chunk, sizeof chunk);
            if (got > 0) {
                buffer_add(fd == reading_out ? output : errors, chunk, (size_t)got);
            } else if (got == 0 || errno != EINTR) {
                close(fd);
                if (fd == reading_out) {
                    reading_out = -1;
                } else {
                    reading_err = -1;
                }
            }
        }
    }

    if (writing >= 0) {
        close(writing);
    }

    int ended = 0;
    while (waitpid(child, &ended, 0) < 0 && errno == EINTR) {
    }

    // A signal's end as the shell reports it: 128 and the signal.
    *status = WIFEXITED(ended) ? WEXITSTATUS(ended) : 128 + WTERMSIG(ended);
    return true;
}

static char *current_directory(void) {
    char path[4096];
    return getcwd(path, sizeof path) == NULL ? NULL : strdup(path);
}

static bool change_directory(const char *name) {
    return chdir(name) == 0;
}

static int64_t process_id(void) {
    return (int64_t)getpid();
}

// The path this program was started by: argv[0], made absolute when it
// names a file. A name without a '/' was found on PATH, and is given as it
// is, for process_run to find the same way.
static char *executable_path(int argc, char **argv) {
    if (argc < 1 || argv[0] == NULL) {
        return NULL;
    }

    if (strchr(argv[0], '/') != NULL) {
        char *resolved = realpath(argv[0], NULL);
        if (resolved != NULL) {
            return resolved;
        }
    }

    return strdup(argv[0]);
}

// ---- Randomness ----------------------------------------------------------

static bool random_bytes(void *buffer, size_t length) {
    return getentropy(buffer, length) == 0;
}
