// The Sable runtime's platform layer for POSIX systems — macOS and Linux:
// what platform.h says each platform gives the rest.

#include <dirent.h>
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <spawn.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/random.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#if defined(__linux__)
#include <link.h>
#include <netpacket/packet.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#else
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <net/if_dl.h>
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

#if defined(__linux__)
static bool program_symbol(const void *at, const char **symbol, const void **start, const void **image);
#endif

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
    const char *symbol = NULL;
    const void *start = NULL, *image = NULL;
    if (dladdr(at, &info) != 0) {
        symbol = info.dli_sname;
        start = info.dli_saddr;
        image = info.dli_fbase;
    }

#if defined(__linux__)
    // dladdr sees only the dynamic symbol table, which a program not linked
    // -rdynamic gives none of its own functions (NativeBuild's link flags).
    if (symbol == NULL) {
        program_symbol(at, &symbol, &start, &image);
    }
#endif

    if (symbol != NULL) {
        if (frame_place(at, image, place, sizeof place)) {
            snprintf(text, size, "%s at %s", symbol, place);
        } else {
            snprintf(text, size, "%s + %ld", symbol, (long)(at + 1 - (const char *)start));
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

// The program's own symbol table, .symtab in its ELF file: every function
// of it, where the dynamic one dladdr reads has only what the program
// exports. Found once, by whichever thread first asks, as the line table
// is: 0 not yet looked for, 1 being looked for, 2 found, 3 there is none (a
// program stripped of its symbols).
static int symbols_state;
static struct {
    const Elf64_Sym *symbols;
    size_t count;
    const uint8_t *names;
    size_t names_size;
    LineSections image; // the program's load bias and start, as find_program_image gives them
    uintptr_t end;      // the end of its highest segment, in memory
} program_symbols;

static int find_program_end(struct dl_phdr_info *info, size_t size, void *data) {
    (void)size;
    uintptr_t highest = 0;
    for (int i = 0; i < info->dlpi_phnum; i++) {
        if (info->dlpi_phdr[i].p_type == PT_LOAD && info->dlpi_phdr[i].p_vaddr + info->dlpi_phdr[i].p_memsz > highest) {
            highest = info->dlpi_phdr[i].p_vaddr + info->dlpi_phdr[i].p_memsz;
        }
    }

    *(uintptr_t *)data = info->dlpi_addr + highest;
    return 1;
}

static bool find_program_symbols(void) {
    size_t size = 0;
    const uint8_t *file = map_whole_file("/proc/self/exe", &size);
    if (file == NULL) {
        return false;
    }

    const Elf64_Ehdr *header = (const Elf64_Ehdr *)file;
    if (size < sizeof *header || memcmp(header->e_ident, ELFMAG, SELFMAG) != 0 || header->e_ident[EI_CLASS] != ELFCLASS64
        || header->e_shentsize != sizeof(Elf64_Shdr) || header->e_shoff > size
        || (uint64_t)header->e_shnum * sizeof(Elf64_Shdr) > size - header->e_shoff) {
        return false;
    }

    const Elf64_Shdr *table = (const Elf64_Shdr *)(file + header->e_shoff);
    for (int i = 0; i < header->e_shnum; i++) {
        if (table[i].sh_type != SHT_SYMTAB || table[i].sh_entsize != sizeof(Elf64_Sym) || table[i].sh_link >= header->e_shnum) {
            continue;
        }

        const uint8_t *symbols;
        size_t symbols_size;
        const Elf64_Shdr *names = &table[table[i].sh_link];
        if (!section_in(file, size, table[i].sh_offset, table[i].sh_size, &symbols, &symbols_size)
            || !section_in(file, size, names->sh_offset, names->sh_size, &program_symbols.names, &program_symbols.names_size)) {
            return false;
        }

        program_symbols.symbols = (const Elf64_Sym *)symbols;
        program_symbols.count = symbols_size / sizeof(Elf64_Sym);
        dl_iterate_phdr(find_program_image, &program_symbols.image);
        dl_iterate_phdr(find_program_end, &program_symbols.end);
        return program_symbols.image.image != NULL;
    }

    return false;
}

// The function of the program itself that 'at' is in, from its .symtab:
// its name and start, and where the program starts, as dladdr would give
// them; false where the address is not the program's, or it has no
// symbol table.
static bool program_symbol(const void *at, const char **symbol, const void **start, const void **image) {
    int state = __atomic_load_n(&symbols_state, __ATOMIC_ACQUIRE);
    if (state == 0) {
        int expected = 0;
        if (__atomic_compare_exchange_n(&symbols_state, &expected, 1, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
            state = find_program_symbols() ? 2 : 3;
            __atomic_store_n(&symbols_state, state, __ATOMIC_RELEASE);
        }
    }

    while ((state = __atomic_load_n(&symbols_state, __ATOMIC_ACQUIRE)) == 1) {
        thread_yield();
    }

    if (state != 2 || (uintptr_t)at < (uintptr_t)program_symbols.image.image || (uintptr_t)at >= program_symbols.end) {
        return false;
    }

    uint64_t address = (uint64_t)((uintptr_t)at - (uintptr_t)program_symbols.image.slide);
    for (size_t i = 0; i < program_symbols.count; i++) {
        const Elf64_Sym *entry = &program_symbols.symbols[i];
        if (ELF64_ST_TYPE(entry->st_info) != STT_FUNC || entry->st_shndx == SHN_UNDEF || address < entry->st_value
            || address - entry->st_value >= entry->st_size) {
            continue;
        }

        const char *name = line_string_at(program_symbols.names, program_symbols.names_size, entry->st_name);
        if (name == NULL || name[0] == '\0') {
            continue;
        }

        *symbol = name;
        *start = (const void *)(uintptr_t)(entry->st_value + (uint64_t)program_symbols.image.slide);
        *image = program_symbols.image.image;
        return true;
    }

    return false;
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
    FILE *status = fopen("/proc/self/status", "re");
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
    FILE *status = fopen("/proc/self/status", "re");
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

// macOS counts CLOCK_MONOTONIC through a sleep, and CLOCK_UPTIME_RAW not;
// Linux's CLOCK_MONOTONIC already stops while the machine is suspended (its
// CLOCK_BOOTTIME is the one that does not).
int64_t sable_time_suspending(void) {
#if defined(__APPLE__)
    return (int64_t)clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
#else
    return sable_time_monotonic();
#endif
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

static bool mutex_try_lock(PlatformMutex *mutex) {
    return pthread_mutex_trylock(mutex) == 0;
}

// What it looks like now, without taking its cache line from the thread that
// holds it; true where the system's mutex does not say.
static bool mutex_looks_free(PlatformMutex *mutex) {
#if defined(__GLIBC__)
    return __atomic_load_n(&mutex->__data.__lock, __ATOMIC_RELAXED) == 0;
#else
    (void)mutex;
    return true;
#endif
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

    if (left > 86400000000000) {
        left = 86400000000000; // a day at most: the caller waits again
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
        } else if (left > 86400000000000) {
            left = 86400000000000; // a day at most: the caller waits again
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

// What the system said of the last call that failed, as the library tells
// failures apart.
static int socket_error_kind(int code) {
    switch (code) {
        case ECONNRESET: case EPIPE: case ENOTCONN: case ECONNABORTED: case ESHUTDOWN: return NET_ERROR_DISCONNECTED;
        case ECONNREFUSED: return NET_ERROR_REFUSED;
        case ETIMEDOUT: return NET_ERROR_TIMED_OUT;
        case ENETUNREACH: case EHOSTUNREACH: case ENETDOWN: case EHOSTDOWN: return NET_ERROR_UNREACHABLE;
        case EADDRINUSE: return NET_ERROR_IN_USE;
        case EADDRNOTAVAIL: return NET_ERROR_UNREACHABLE;
        case EACCES: case EPERM: return NET_ERROR_PERMISSION;
        case EMFILE: case ENFILE: case ENOBUFS: case ENOMEM: return NET_ERROR_TRY_LATER;
        case EINVAL: case ENAMETOOLONG: case EDESTADDRREQ: case EISCONN: case EDOM: return NET_ERROR_INVALID;
        case EMSGSIZE: return NET_ERROR_TOO_LONG;
        case ENOENT: case ENOTDIR: return NET_ERROR_NOT_FOUND;
        case EOPNOTSUPP: case ENOPROTOOPT: case EAFNOSUPPORT: case EPROTONOSUPPORT: case EPROTOTYPE: return NET_ERROR_UNSUPPORTED;
        default: return NET_ERROR_OTHER;
    }
}

static int socket_fail(void) {
    net_last_failure.code = errno;
    net_last_failure.kind = socket_error_kind(errno);
    return IO_FAILED;
}

static int socket_fail_as(int kind) {
    net_last_failure.code = 0;
    net_last_failure.kind = kind;
    return IO_FAILED;
}

static bool socket_blocks(void) {
    return errno == EAGAIN || errno == EWOULDBLOCK;
}

// Nonblocking, closed on exec, and never a SIGPIPE: a write to a closed
// connection fails instead.
static bool socket_prepare(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0 || fcntl(fd, F_SETFD, FD_CLOEXEC) < 0) {
        return false;
    }

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

// A socket closed on exec: from the start on Linux; elsewhere in
// socket_prepare, and a program the runtime starts inherits none anyway.
#ifdef SOCK_CLOEXEC
#define SOCKET_CLOEXEC SOCK_CLOEXEC
#else
#define SOCKET_CLOEXEC 0
#endif

// The address as the system takes it: its length, or 0 for one it cannot.
static socklen_t socket_address(const PlatformAddress *address, struct sockaddr_storage *to) {
    memset(to, 0, sizeof *to);
    if (address->family == NET_IPV4) {
        struct sockaddr_in *at = (struct sockaddr_in *)to;
        at->sin_family = AF_INET;
        at->sin_port = htons((uint16_t)address->port);
        at->sin_addr.s_addr = htonl((uint32_t)address->low);
        return sizeof *at;
    }

    if (address->family == NET_IPV6) {
        struct sockaddr_in6 *at = (struct sockaddr_in6 *)to;
        at->sin6_family = AF_INET6;
        at->sin6_port = htons((uint16_t)address->port);
        at->sin6_scope_id = address->scope;
        for (int i = 0; i < 8; i++) {
            at->sin6_addr.s6_addr[i] = (uint8_t)(address->high >> (56 - 8 * i));
            at->sin6_addr.s6_addr[8 + i] = (uint8_t)(address->low >> (56 - 8 * i));
        }

        return sizeof *at;
    }

    struct sockaddr_un *at = (struct sockaddr_un *)to;
    at->sun_family = AF_UNIX;
    size_t length = address->path_length;
    bool abstract = length > 0 && address->path[0] == '\0';
#if !defined(__linux__)
    if (abstract) {
        return 0;
    }
#endif
    // A name ends at its first zero byte, but for an abstract one.
    if (length == 0 || length + (abstract ? 0 : 1) > sizeof at->sun_path || (!abstract && memchr(address->path, '\0', length) != NULL)) {
        return 0;
    }

    memcpy(at->sun_path, address->path, length);
    return (socklen_t)(offsetof(struct sockaddr_un, sun_path) + length + (abstract ? 0 : 1));
}

static void socket_address_from(const struct sockaddr_storage *from, socklen_t length, PlatformAddress *address) {
    memset(address, 0, sizeof *address);
    if (length >= (socklen_t)sizeof(struct sockaddr_in) && from->ss_family == AF_INET) {
        const struct sockaddr_in *at = (const struct sockaddr_in *)from;
        address->family = NET_IPV4;
        address->port = ntohs(at->sin_port);
        address->low = ntohl(at->sin_addr.s_addr);
    } else if (length >= (socklen_t)sizeof(struct sockaddr_in6) && from->ss_family == AF_INET6) {
        const struct sockaddr_in6 *at = (const struct sockaddr_in6 *)from;
        address->family = NET_IPV6;
        address->port = ntohs(at->sin6_port);
        address->scope = at->sin6_scope_id;
        for (int i = 0; i < 8; i++) {
            address->high = address->high << 8 | at->sin6_addr.s6_addr[i];
            address->low = address->low << 8 | at->sin6_addr.s6_addr[8 + i];
        }
    } else if (length >= (socklen_t)offsetof(struct sockaddr_un, sun_path) && from->ss_family == AF_UNIX) {
        const struct sockaddr_un *at = (const struct sockaddr_un *)from;
        size_t most = (size_t)length - offsetof(struct sockaddr_un, sun_path);
        if (most > sizeof at->sun_path) {
            most = sizeof at->sun_path;
        }

        address->family = NET_UNIX;
        address->path_length = most > 0 && at->sun_path[0] == '\0' ? most : strnlen(at->sun_path, most);
#if !defined(__linux__)
        if (most > 0 && at->sun_path[0] == '\0') {
            address->path_length = 0;
        }
#endif
        memcpy(address->path, at->sun_path, address->path_length);
    }
}

// A socket for the address, prepared; or -1.
static int socket_open(const PlatformAddress *address, int kind) {
    int family = address->family == NET_IPV4 ? AF_INET : address->family == NET_IPV6 ? AF_INET6 : AF_UNIX;
    int fd = socket(family, kind | SOCKET_CLOEXEC, 0);
    if (fd < 0) {
        return -1;
    }

    if (!socket_prepare(fd)) {
        int code = errno;
        close(fd);
        errno = code;
        return -1;
    }

    return fd;
}

static int socket_give_up(int fd) {
    int code = errno;
    close(fd);
    errno = code;
    return socket_fail();
}

static bool socket_set(int fd, int level, int name, int value) {
    return setsockopt(fd, level, name, &value, sizeof value) == 0;
}

// What is settled before a socket is bound. An IPv6 socket takes IPv4 too
// only if it is asked to, whatever the system's own default is.
static bool socket_before_bind(int fd, const PlatformAddress *address, int flags) {
    return (!(flags & NET_REUSE_ADDRESS) || socket_set(fd, SOL_SOCKET, SO_REUSEADDR, 1))
           && (!(flags & NET_REUSE_PORT) || socket_set(fd, SOL_SOCKET, SO_REUSEPORT, 1))
           && (address->family != NET_IPV6 || socket_set(fd, IPPROTO_IPV6, IPV6_V6ONLY, !(flags & NET_DUAL_STACK)));
}

static bool socket_bind(int fd, const PlatformAddress *address, int flags) {
    struct sockaddr_storage at;
    socklen_t length = socket_address(address, &at);
    if (length == 0) {
        errno = EINVAL;
        return false;
    }

    struct stat found;
    if (address->family == NET_UNIX && (flags & NET_REPLACE) && address->path[0] != '\0'
        && lstat(((struct sockaddr_un *)&at)->sun_path, &found) == 0 && S_ISSOCK(found.st_mode)) {
        unlink(((struct sockaddr_un *)&at)->sun_path);
    }

    return bind(fd, (struct sockaddr *)&at, length) == 0;
}

static PlatformSocket socket_listen(const PlatformAddress *address, int backlog, int flags) {
    int fd = socket_open(address, SOCK_STREAM);
    if (fd < 0) {
        return socket_fail();
    }

    // SO_REUSEADDR here lets a server take its port again while connections
    // of its last run are in TIME_WAIT; it lets no two listen at once.
    if (!socket_before_bind(fd, address, flags | (address->family == NET_UNIX ? 0 : NET_REUSE_ADDRESS)) || !socket_bind(fd, address, flags)
        || listen(fd, backlog) != 0) {
        return socket_give_up(fd);
    }

    return fd;
}

// The last accept found no descriptor to give its connection, which macOS
// then closes: until there is one to be had, another would lose another.
static bool sockets_short;

static PlatformSocket socket_accept(PlatformSocket listener, PlatformAddress *peer) {
    if (__atomic_load_n(&sockets_short, __ATOMIC_RELAXED)) {
        int spare = fcntl((int)listener, F_DUPFD_CLOEXEC, 0);
        if (spare < 0) {
            return socket_fail();
        }

        close(spare);
        __atomic_store_n(&sockets_short, false, __ATOMIC_RELAXED);
    }

    for (;;) {
        struct sockaddr_storage from;
        socklen_t length = sizeof from;
#if defined(__linux__)
        int fd = accept4((int)listener, (struct sockaddr *)&from, &length, SOCK_CLOEXEC);
#else
        int fd = accept((int)listener, (struct sockaddr *)&from, &length);
#endif
        if (fd >= 0) {
            // Its peer gone already, macOS gives a socket with no address.
            if (length == 0 || !socket_prepare(fd)) {
                close(fd);
                continue;
            }

            if (from.ss_family != AF_UNIX) {
                socket_set(fd, IPPROTO_TCP, TCP_NODELAY, 1);
            }

            socket_address_from(&from, length, peer);
            return fd;
        }

        if (errno == EMFILE || errno == ENFILE) {
            __atomic_store_n(&sockets_short, true, __ATOMIC_RELAXED);
        }

        switch (errno) {
            // A connection that went before it was accepted is the next
            // one's turn; Linux gives its pending errors here too.
            case EINTR: case ECONNABORTED: case EPROTO:
#if defined(__linux__)
            case ENETDOWN: case ENOPROTOOPT: case EHOSTDOWN: case ENONET: case EHOSTUNREACH: case EOPNOTSUPP: case ENETUNREACH:
#endif
                continue;
        }

        return socket_blocks() ? IO_WOULD_BLOCK : socket_fail();
    }
}

static PlatformSocket socket_connect(const PlatformAddress *address) {
    struct sockaddr_storage to;
    socklen_t length = socket_address(address, &to);
    if (length == 0) {
        return socket_fail_as(NET_ERROR_INVALID);
    }

    int fd = socket_open(address, SOCK_STREAM);
    if (fd < 0) {
        return socket_fail();
    }

    if (connect(fd, (struct sockaddr *)&to, length) != 0 && errno != EINPROGRESS && errno != EINTR) {
        // Linux: a Unix listener whose queue is full.
        bool later = socket_blocks();
        socket_give_up(fd);
        return later ? socket_fail_as(NET_ERROR_TRY_LATER) : IO_FAILED;
    }

    if (address->family != NET_UNIX) {
        socket_set(fd, IPPROTO_TCP, TCP_NODELAY, 1);
    }

    return fd;
}

static int socket_connected(PlatformSocket socket) {
    int error = 0;
    socklen_t length = sizeof error;
    if (getsockopt((int)socket, SOL_SOCKET, SO_ERROR, &error, &length) != 0) {
        return socket_fail();
    }

    if (error != 0) {
        errno = error;
        return socket_fail();
    }

    // Writable with no error yet is not always made: a peer says it is.
    struct sockaddr_storage peer;
    socklen_t peer_length = sizeof peer;
    if (getpeername((int)socket, (struct sockaddr *)&peer, &peer_length) == 0) {
        return 0;
    }

    int code = errno;
    struct pollfd now = { .fd = (int)socket, .events = POLLOUT };
    if (poll(&now, 1, 0) == 0) {
        return IO_WOULD_BLOCK;
    }

    errno = code;
    return socket_fail();
}

static PlatformSocket socket_datagram(const PlatformAddress *address, int flags) {
    int fd = socket_open(address, SOCK_DGRAM);
    if (fd < 0) {
        return socket_fail();
    }

#if !defined(__linux__)
    // Sockets that share a port for multicast each take every datagram with
    // SO_REUSEPORT here, and SO_REUSEADDR there.
    if (flags & NET_REUSE_ADDRESS && address->family != NET_UNIX) {
        flags |= NET_REUSE_PORT;
    }
#endif
    bool unbound = address->family == NET_UNIX && address->path_length == 0;
    if (!unbound && (!socket_before_bind(fd, address, flags) || !socket_bind(fd, address, flags))) {
        return socket_give_up(fd);
    }

    return fd;
}

static int socket_connect_datagram(PlatformSocket socket, const PlatformAddress *address) {
    struct sockaddr_storage to;
    socklen_t length = socket_address(address, &to);
    if (length == 0) {
        return socket_fail_as(NET_ERROR_INVALID);
    }

    return connect((int)socket, (struct sockaddr *)&to, length) == 0 ? 0 : socket_fail();
}

static int64_t socket_read(PlatformSocket socket, void *bytes, int64_t size) {
    for (;;) {
        ssize_t got = recv((int)socket, bytes, (size_t)size, 0);
        if (got >= 0) {
            return got;
        }

        if (errno != EINTR) {
            return socket_blocks() ? IO_WOULD_BLOCK : socket_fail();
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
            return socket_blocks() ? IO_WOULD_BLOCK : socket_fail();
        }
    }
}

static int64_t socket_send_to(PlatformSocket socket, const void *bytes, int64_t size, const PlatformAddress *address) {
    struct sockaddr_storage to;
    socklen_t length = 0;
    if (address->family != NET_NONE) {
        length = socket_address(address, &to);
        if (length == 0) {
            return socket_fail_as(NET_ERROR_INVALID);
        }
    }

    for (;;) {
        ssize_t sent = sendto((int)socket, bytes, (size_t)size, SOCKET_SEND_FLAGS, length == 0 ? NULL : (struct sockaddr *)&to, length);
        if (sent >= 0) {
            return sent;
        }

        if (errno != EINTR) {
            return socket_blocks() ? IO_WOULD_BLOCK : socket_fail();
        }
    }
}

static int64_t socket_receive(PlatformSocket socket, void *bytes, int64_t size, PlatformAddress *from, bool *truncated) {
    for (;;) {
        struct sockaddr_storage address;
        struct iovec part = { .iov_base = bytes, .iov_len = (size_t)size };
        struct msghdr message = { 0 };
        memset(&address, 0, sizeof address);
        message.msg_name = &address;
        message.msg_namelen = sizeof address;
        message.msg_iov = &part;
        message.msg_iovlen = 1;
        ssize_t got = recvmsg((int)socket, &message, 0);
        if (got >= 0) {
            socket_address_from(&address, message.msg_namelen, from);
            *truncated = (message.msg_flags & MSG_TRUNC) != 0;
            return got;
        }

        if (errno != EINTR) {
            return socket_blocks() ? IO_WOULD_BLOCK : socket_fail();
        }
    }
}

static bool socket_name(PlatformSocket socket, bool peer, PlatformAddress *address) {
    struct sockaddr_storage at;
    socklen_t length = sizeof at;
    memset(&at, 0, sizeof at);
    if ((peer ? getpeername : getsockname)((int)socket, (struct sockaddr *)&at, &length) != 0) {
        socket_fail();
        return false;
    }

    socket_address_from(&at, length, address);
    return true;
}

#if defined(__APPLE__)
#define SOCKET_KEEP_IDLE TCP_KEEPALIVE
#define SOCKET_LINGER SO_LINGER_SEC
#else
#define SOCKET_KEEP_IDLE TCP_KEEPIDLE
#define SOCKET_LINGER SO_LINGER
#endif

static bool socket_set_option(PlatformSocket socket, int option, int64_t value) {
    int fd = (int)socket;
    if (value < INT32_MIN || value > INT32_MAX) {
        socket_fail_as(NET_ERROR_INVALID);
        return false;
    }

    struct sockaddr_storage at;
    socklen_t length = sizeof at;
    memset(&at, 0, sizeof at);
    if (getsockname(fd, (struct sockaddr *)&at, &length) != 0) {
        socket_fail();
        return false;
    }

    bool six = at.ss_family == AF_INET6;
    int number = (int)value;
    bool done = false;
    switch (option) {
        case NET_OPTION_NO_DELAY: done = socket_set(fd, IPPROTO_TCP, TCP_NODELAY, number); break;
        case NET_OPTION_KEEP_ALIVE: done = socket_set(fd, SOL_SOCKET, SO_KEEPALIVE, number); break;
        case NET_OPTION_KEEP_IDLE: done = socket_set(fd, IPPROTO_TCP, SOCKET_KEEP_IDLE, number); break;
        case NET_OPTION_KEEP_INTERVAL: done = socket_set(fd, IPPROTO_TCP, TCP_KEEPINTVL, number); break;
        case NET_OPTION_KEEP_COUNT: done = socket_set(fd, IPPROTO_TCP, TCP_KEEPCNT, number); break;
        case NET_OPTION_RECEIVE_BUFFER: done = socket_set(fd, SOL_SOCKET, SO_RCVBUF, number); break;
        case NET_OPTION_SEND_BUFFER: done = socket_set(fd, SOL_SOCKET, SO_SNDBUF, number); break;
        case NET_OPTION_LINGER: {
            struct linger linger = { .l_onoff = number >= 0, .l_linger = number < 0 ? 0 : number };
            done = setsockopt(fd, SOL_SOCKET, SOCKET_LINGER, &linger, sizeof linger) == 0;
            break;
        }

        case NET_OPTION_HOPS:
            done = six ? socket_set(fd, IPPROTO_IPV6, IPV6_UNICAST_HOPS, number) : socket_set(fd, IPPROTO_IP, IP_TTL, number);
            break;
        case NET_OPTION_BROADCAST: done = socket_set(fd, SOL_SOCKET, SO_BROADCAST, number); break;
        case NET_OPTION_MULTICAST_LOOP: {
            unsigned char loop = number != 0;
            done = six ? socket_set(fd, IPPROTO_IPV6, IPV6_MULTICAST_LOOP, number != 0)
                       : setsockopt(fd, IPPROTO_IP, IP_MULTICAST_LOOP, &loop, sizeof loop) == 0;
            break;
        }

        case NET_OPTION_MULTICAST_HOPS: {
            unsigned char hops = (unsigned char)number;
            done = six ? socket_set(fd, IPPROTO_IPV6, IPV6_MULTICAST_HOPS, number)
                       : setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL, &hops, sizeof hops) == 0;
            break;
        }

        case NET_OPTION_MULTICAST_INTERFACE:
            if (six) {
                done = socket_set(fd, IPPROTO_IPV6, IPV6_MULTICAST_IF, number);
            } else {
                struct ip_mreqn interface = { 0 };
                interface.imr_ifindex = number;
                done = setsockopt(fd, IPPROTO_IP, IP_MULTICAST_IF, &interface, sizeof interface) == 0;
            }

            break;
        default:
            errno = ENOPROTOOPT;
    }

    if (!done) {
        socket_fail();
    }

    return done;
}

static bool socket_get_option(PlatformSocket socket, int option, int64_t *value) {
    int fd = (int)socket;
    struct sockaddr_storage at;
    socklen_t length = sizeof at;
    memset(&at, 0, sizeof at);
    if (getsockname(fd, (struct sockaddr *)&at, &length) != 0) {
        socket_fail();
        return false;
    }

    bool six = at.ss_family == AF_INET6;
    int level = SOL_SOCKET, name = 0;
    switch (option) {
        case NET_OPTION_NO_DELAY: level = IPPROTO_TCP; name = TCP_NODELAY; break;
        case NET_OPTION_KEEP_ALIVE: name = SO_KEEPALIVE; break;
        case NET_OPTION_KEEP_IDLE: level = IPPROTO_TCP; name = SOCKET_KEEP_IDLE; break;
        case NET_OPTION_KEEP_INTERVAL: level = IPPROTO_TCP; name = TCP_KEEPINTVL; break;
        case NET_OPTION_KEEP_COUNT: level = IPPROTO_TCP; name = TCP_KEEPCNT; break;
        case NET_OPTION_RECEIVE_BUFFER: name = SO_RCVBUF; break;
        case NET_OPTION_SEND_BUFFER: name = SO_SNDBUF; break;
        case NET_OPTION_LINGER: {
            struct linger linger = { 0 };
            length = sizeof linger;
            if (getsockopt(fd, SOL_SOCKET, SOCKET_LINGER, &linger, &length) != 0) {
                socket_fail();
                return false;
            }

            *value = linger.l_onoff ? linger.l_linger : -1;
            return true;
        }

        case NET_OPTION_HOPS: level = six ? IPPROTO_IPV6 : IPPROTO_IP; name = six ? IPV6_UNICAST_HOPS : IP_TTL; break;
        case NET_OPTION_BROADCAST: name = SO_BROADCAST; break;
        case NET_OPTION_MULTICAST_LOOP: level = six ? IPPROTO_IPV6 : IPPROTO_IP; name = six ? IPV6_MULTICAST_LOOP : IP_MULTICAST_LOOP; break;
        case NET_OPTION_MULTICAST_HOPS: level = six ? IPPROTO_IPV6 : IPPROTO_IP; name = six ? IPV6_MULTICAST_HOPS : IP_MULTICAST_TTL; break;
        case NET_OPTION_IPV6_ONLY: level = IPPROTO_IPV6; name = IPV6_V6ONLY; break;
        default: errno = ENOPROTOOPT; socket_fail(); return false;
    }

    // Some answer in a byte, some in an int.
    union {
        int number;
        unsigned char byte;
    } got = { 0 };
    length = sizeof got;
    if (getsockopt(fd, level, name, &got, &length) != 0) {
        socket_fail();
        return false;
    }

    *value = length == 1 ? got.byte : got.number;
    return true;
}

static bool socket_membership(PlatformSocket socket, bool join, const PlatformAddress *group, uint32_t interface) {
    struct group_req request;
    struct sockaddr_storage at;
    memset(&request, 0, sizeof request);
    socklen_t length = socket_address(group, &at);
    if (length == 0 || group->family == NET_UNIX) {
        socket_fail_as(NET_ERROR_INVALID);
        return false;
    }

    request.gr_interface = interface;
    memcpy(&request.gr_group, &at, length);
#if !defined(__linux__)
    request.gr_group.ss_len = (uint8_t)length;
#endif
    int level = group->family == NET_IPV6 ? IPPROTO_IPV6 : IPPROTO_IP;
    if (setsockopt((int)socket, level, join ? MCAST_JOIN_GROUP : MCAST_LEAVE_GROUP, &request, sizeof request) != 0) {
        socket_fail();
        return false;
    }

    return true;
}

static void socket_shutdown(PlatformSocket socket, bool writing) {
    shutdown((int)socket, writing ? SHUT_WR : SHUT_RD);
}

static void socket_close(PlatformSocket socket) {
    close((int)socket);
}

static int unix_datagram_send(const char *name, size_t name_length, const void *bytes, size_t length) {
    bool abstract = name_length > 0 && name[0] == '@';
#if !defined(__linux__)
    if (abstract) {
        return 1;
    }
#endif
    if (name_length < 2 || (!abstract && name[0] != '/')) {
        return 1;
    }

    struct sockaddr_un to;
    memset(&to, 0, sizeof to);
    to.sun_family = AF_UNIX;
    if (name_length >= sizeof to.sun_path) {
        return 2;
    }

    // An abstract name is all the bytes after a NUL in '@''s place, and no more.
    memcpy(to.sun_path, name, name_length);
    if (abstract) {
        to.sun_path[0] = '\0';
    }

    int fd = socket(AF_UNIX, SOCK_DGRAM | SOCKET_CLOEXEC, 0);
    if (fd < 0) {
        return 2;
    }

    fcntl(fd, F_SETFD, FD_CLOEXEC);
    // A receiver that has stopped reading holds its sender up, but not for ever.
    struct timeval limit = {.tv_sec = 5, .tv_usec = 0};
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &limit, sizeof limit);
    socklen_t size = (socklen_t)(offsetof(struct sockaddr_un, sun_path) + name_length + (abstract ? 0 : 1));
    ssize_t sent;
    do {
        sent = sendto(fd, bytes, length, SOCKET_SEND_FLAGS, (struct sockaddr *)&to, size);
    } while (sent < 0 && errno == EINTR);
    close(fd);
    return sent == (ssize_t)length ? 0 : 2;
}

// ---- Names and interfaces ------------------------------------------------------

static int host_resolve(const char *host, int family, PlatformAddress **addresses, int *code) {
    *addresses = NULL;
    *code = 0;
    struct addrinfo hints = { 0 };
    hints.ai_family = family == NET_IPV4 ? AF_INET : family == NET_IPV6 ? AF_INET6 : AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    struct addrinfo *found = NULL;
    int error = getaddrinfo(host, NULL, &hints, &found);
    if (error != 0) {
        *code = error == EAI_SYSTEM ? errno : error;
        switch (error) {
            case EAI_NONAME:
#ifdef EAI_NODATA
#if EAI_NODATA != EAI_NONAME
            case EAI_NODATA:
#endif
#endif
            case EAI_FAMILY:
                *code = NET_ERROR_NOT_FOUND;
                break;
            case EAI_AGAIN:
                *code = NET_ERROR_TRY_LATER;
                break;
            default:
                *code = NET_ERROR_OTHER;
        }

        return -1;
    }

    int count = 0;
    for (struct addrinfo *at = found; at != NULL; at = at->ai_next) {
        count++;
    }

    PlatformAddress *all = calloc((size_t)count + 1, sizeof *all);
    if (all == NULL) {
        sable_trap("out of memory");
    }

    int n = 0;
    for (struct addrinfo *at = found; at != NULL; at = at->ai_next) {
        struct sockaddr_storage address;
        memset(&address, 0, sizeof address);
        if (at->ai_addrlen <= sizeof address) {
            memcpy(&address, at->ai_addr, at->ai_addrlen);
            socket_address_from(&address, at->ai_addrlen, &all[n]);
            n += all[n].family == NET_IPV4 || all[n].family == NET_IPV6 ? 1 : 0;
        }
    }

    freeaddrinfo(found);
    *addresses = all;
    return n;
}

static uint32_t interface_index(const char *name) {
    return if_nametoindex(name);
}

static int interfaces_list(PlatformInterface **interfaces) {
    *interfaces = NULL;
    struct ifaddrs *found = NULL;
    if (getifaddrs(&found) != 0) {
        socket_fail();
        return -1;
    }

    int count = 0;
    for (struct ifaddrs *at = found; at != NULL; at = at->ifa_next) {
        count++;
    }

    PlatformInterface *all = calloc((size_t)count + 1, sizeof *all);
    if (all == NULL) {
        sable_trap("out of memory");
    }

    int probe = socket(AF_INET, SOCK_DGRAM | SOCKET_CLOEXEC, 0);
    int n = 0;
    for (struct ifaddrs *at = found; at != NULL; at = at->ifa_next) {
        PlatformInterface *row = &all[n++];
        snprintf(row->name, sizeof row->name, "%s", at->ifa_name);
        row->index = if_nametoindex(at->ifa_name);
        row->flags = (at->ifa_flags & IFF_UP ? NET_INTERFACE_UP : 0) | (at->ifa_flags & IFF_RUNNING ? NET_INTERFACE_RUNNING : 0)
                     | (at->ifa_flags & IFF_LOOPBACK ? NET_INTERFACE_LOOPBACK : 0)
                     | (at->ifa_flags & IFF_POINTOPOINT ? NET_INTERFACE_POINT_TO_POINT : 0)
                     | (at->ifa_flags & IFF_MULTICAST ? NET_INTERFACE_MULTICAST : 0)
                     | (at->ifa_flags & IFF_BROADCAST ? NET_INTERFACE_BROADCAST : 0);
        row->hardware = -1;
        struct ifreq request;
        memset(&request, 0, sizeof request);
        snprintf(request.ifr_name, sizeof request.ifr_name, "%s", at->ifa_name);
        row->mtu = probe >= 0 && ioctl(probe, SIOCGIFMTU, &request) == 0 ? request.ifr_mtu : 0;
        if (at->ifa_addr == NULL) {
            continue;
        }

        const uint8_t *hardware = NULL;
#if defined(__linux__)
        if (at->ifa_addr->sa_family == AF_PACKET && ((struct sockaddr_ll *)at->ifa_addr)->sll_halen == 6) {
            hardware = ((struct sockaddr_ll *)at->ifa_addr)->sll_addr;
        }
#else
        if (at->ifa_addr->sa_family == AF_LINK && ((struct sockaddr_dl *)at->ifa_addr)->sdl_alen == 6) {
            hardware = (const uint8_t *)LLADDR((struct sockaddr_dl *)at->ifa_addr);
        }
#endif
        if (hardware != NULL) {
            row->hardware = 0;
            for (int i = 0; i < 6; i++) {
                row->hardware = row->hardware << 8 | hardware[i];
            }
        }

        bool six = at->ifa_addr->sa_family == AF_INET6;
        if (!six && at->ifa_addr->sa_family != AF_INET) {
            continue;
        }

        struct sockaddr_storage address;
        memset(&address, 0, sizeof address);
        size_t size = six ? sizeof(struct sockaddr_in6) : sizeof(struct sockaddr_in);
        memcpy(&address, at->ifa_addr, size);
        socket_address_from(&address, (socklen_t)size, &row->address);
        if (six && row->address.high >> 54 == 0xFE80 >> 6) {
#if defined(__APPLE__)
            // The kernel keeps a link-local address's scope within it.
            row->address.high &= ~UINT64_C(0x0000FFFF00000000);
#endif
            row->address.scope = row->index;
        }

        if (at->ifa_netmask != NULL) {
            const uint8_t *mask = six ? ((struct sockaddr_in6 *)at->ifa_netmask)->sin6_addr.s6_addr
                                      : (const uint8_t *)&((struct sockaddr_in *)at->ifa_netmask)->sin_addr.s_addr;
            for (int i = 0; i < (six ? 16 : 4); i++) {
                row->prefix += __builtin_popcount(mask[i]);
            }
        }
    }

    if (probe >= 0) {
        close(probe);
    }

    freeifaddrs(found);
    *interfaces = all;
    return n;
}

// ---- Arguments, files and the environment ----------------------------------

static char **platform_arguments(int argc, char **argv, int *count) {
    *count = argc;
    return argv;
}

static FILE *open_file(const char *name, const char *mode) {
    // Closed on exec ("e"), as every descriptor the runtime opens is.
    char with[8];
    snprintf(with, sizeof with, "%se", mode);
    return fopen(name, with);
}

static bool append_file(const char *name, const char *bytes, size_t length) {
    int fd = open(name, O_WRONLY | O_APPEND | O_CREAT | O_CLOEXEC, 0644);
    if (fd < 0) {
        return false;
    }

    ssize_t written = write(fd, bytes, length);
    close(fd);
    return written == (ssize_t)length;
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
#if defined(__APPLE__)
    // The system's generator in the process, which is seeded by the kernel's
    // and again after a fork, and which cannot fail: a call of getentropy
    // for each 256 bytes takes twenty times as long.
    arc4random_buf(buffer, length);
#else
    // As much as there is left: it gives less when a signal comes.
    char *next = buffer;
    while (length > 0) {
        ssize_t given = getrandom(next, length, 0);
        if (given < 0) {
            if (errno == EINTR) {
                continue;
            }

            return false;
        }

        next += given;
        length -= (size_t)given;
    }
#endif

    return true;
}

// ---- File handles ---------------------------------------------------------

#include "platform_files_posix.c"
#include "platform_fs_posix.c"
#include "platform_watch_posix.c"

// ---- Starting programs, and signals ---------------------------------------

#include "platform_process_posix.c"
