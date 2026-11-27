// The Sable runtime: the small amount of native code a compiled program
// links against (docs/06 §6.1).
//
// Writing a line, stopping the way the language says a program stops, and the
// heap: counted objects, strings and copy-on-write arrays. Every counted
// object starts with the header of docs/06 §6.2. Tasks run on a pool of
// threads (tasks.c); an object more than one of them can reach is counted
// atomically (objects.c's SABLE_SHARED).
//
// This file is the runtime: it includes its parts, in this order, as one
// translation unit, compiled to one object (or, for link-time optimisation,
// one module of bitcode). The parts keep their helpers 'static', and every
// one of them stays inlinable into the next in every build, not only an
// optimised one. What the operating system gives is the platform layer's
// (platform.h); the parts after it are the same on every system.
//
// Which parts are included is the runtime profile's (docs/06 §6.1),
// SABLE_RUNTIME_PROFILE: 1 minimal, 2 standard, 3 full (the default). Every
// profile has the core: the platform layer, objects, memory, references,
// traps, numbers, strings, hashing, arrays, the leak check, metadata, inline
// layout and the standard streams (io.c). Standard adds the executor and
// threads (frames, tasks, watchdog, locks, foreign threads, parallel loops),
// the system (sockets, files, watching, processes, the terminal, IPC, trust)
// and the processor's kernels (vectors, DLPack, crypto, ciphers, codecs,
// checksums, validation). Full adds the tools: backtraces named by the
// source's lines, coverage and call profiles. What a part left out exports is
// a stub the build makes (NativeBuild.Runtime), which traps by
// sable_runtime_absent; what the core calls of it is below, at the end.

#ifndef SABLE_RUNTIME_PROFILE
#define SABLE_RUNTIME_PROFILE 3
#endif
#define SABLE_RUNTIME_STANDARD (SABLE_RUNTIME_PROFILE >= 2)
#define SABLE_RUNTIME_FULL (SABLE_RUNTIME_PROFILE >= 3)

#include "internal.h"
#include "platform.h"

#if !defined(_WIN32) && !defined(__wasi__)
// A NUL-terminated string in a section, or NULL past its end: what lines.c
// reads DWARF's names with, and the platform layer an ELF program's symbols
// (here, as every profile has the second and only the full one the first).
static const char *line_string_at(const uint8_t *section, size_t size, uint64_t offset) {
    if (section == NULL || offset >= size || memchr(section + offset, 0, size - offset) == NULL) {
        return NULL;
    }

    return (const char *)section + offset;
}
#endif

#if SABLE_RUNTIME_FULL
#include "lines.c"
#else
// No line table is read: a backtrace's frames are named by their symbols.
static bool frame_place(const void *at, const void *image, char *place, size_t size) {
    (void)at, (void)image, (void)place, (void)size;
    return false;
}
#endif

#ifdef _WIN32
#include "platform_win32.c"
#elif defined(__wasi__)
#include "platform_wasi.c"
#else
#include "platform_posix.c"
#endif

#include "threads.c"
#include "objects.c"
#include "memory.c"
#include "exclusivity.c"
#include "references.c"
#if SABLE_RUNTIME_FULL
#include "coverage.c"
#include "profile.c"
#else
static void coverage_write(void) {}
#endif
#include "traps.c"
#include "processor.c"
#include "numbers.c"
#include "strings.c"
#include "hashing.c"
#include "random.c"
#include "arrays.c"
#if SABLE_RUNTIME_STANDARD
#include "topology.c"
#include "dlpack.c"
#include "vectors.c"
#include "crypto.c"
#include "ciphers.c"
#include "trust.c"
#include "codecs.c"
#include "checksums.c"
#endif
#include "leaks.c"
#include "metadata.c"
#include "inline_layout.c"
#if SABLE_RUNTIME_STANDARD
#include "validation.c"
#include "frames.c"
#include "tasks.c"
#include "watchdog.c"
#include "net.c"
#endif
#include "io.c"
#if SABLE_RUNTIME_STANDARD
#include "fs.c"
#include "watch.c"
#include "process.c"
#include "terminal.c"
#include "ipc.c"
#include "locks.c"
#include "foreign.c"
#include "parallel.c"
#endif

#if !SABLE_RUNTIME_STANDARD
// ---- Without the executor --------------------------------------------------

// One thread runs the program, and no task: what the core calls of tasks.c
// and frames.c. Nothing is shared, as no other thread could reach it, and the
// program ends as main returns.
bool sable_threaded;

void sable_share(void *pointer) {
    (void)pointer;
}

static void share_run(const SableMetadata *type, char *elements, int64_t count) {
    (void)type, (void)elements, (void)count;
}

static void share_unowned(void *pointer) {
    (void)pointer;
}

void sable_register_globals(void (*share)(void)) {
    (void)share;
}

void sable_threads_begin(void) {}

void sable_tasks_drain(void) {}

static void tasks_start(void) {}

static void print_task_backtrace(void) {}

static void report_tasks(void) {}

static void report_frames(void) {}

// What the platform layer tells the parts of the system left out: nothing
// watches, catches a signal or is a thread C made.
static void watch_report(void *watch, int kind, const char *path, size_t length) {
    (void)watch, (void)kind, (void)path, (void)length;
}

static void signal_arrived(int signal) {
    (void)signal;
}

static void foreign_thread_end(void *pointer) {
    (void)pointer;
}

// Windows' pipes and change notifications, which only the parts left out
// wait on, tell the reactor; there is none.
static void reactor_ready(void *key, int events) {
    (void)key, (void)events;
}

static void io_event(int64_t handle) {
    (void)handle;
}
#endif

#if !SABLE_RUNTIME_FULL
// What a part the profile leaves out would have done: the stub for each
// function it exports calls this, with the function's name.
_Noreturn void sable_runtime_absent(const char *function) {
    static const char *const profiles[] = { "", "minimal", "standard" };
    char message[256];
    snprintf(message, sizeof message, "'%s' is in a part of the runtime the %s runtime profile leaves out (docs/06 §6.1): build with a larger one (--runtime)",
        function, profiles[SABLE_RUNTIME_PROFILE]);
    sable_trap(message);
}
#endif

// ---- Start-up --------------------------------------------------------------

// What the runtime sets up before any Sable code runs. A program's start-up
// calls it as the process starts, a constructor of the runtime's object; a
// library's (LlvmEmitter's LibraryLifetime) calls it first too, since the
// order in which one image's constructors run is the linker's: a library's
// globals may be made before the runtime's own constructor has run.
void sable_runtime_start(void) {
    static bool started;
    if (started) {
        return;
    }

    started = true;
    platform_start();
    leaks_start();
    sable_counting = getenv("SABLE_STATS") != NULL;
    observing = tracking || sable_counting;
    hashing_start();
    tasks_start();
}

__attribute__((constructor)) static void sable_runtime_constructor(void) {
    sable_runtime_start();
}
