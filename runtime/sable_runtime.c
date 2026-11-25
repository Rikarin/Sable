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

#include "internal.h"
#include "platform.h"
#include "lines.c"

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
#include "coverage.c"
#include "traps.c"
#include "processor.c"
#include "numbers.c"
#include "strings.c"
#include "hashing.c"
#include "random.c"
#include "arrays.c"
#include "vectors.c"
#include "crypto.c"
#include "ciphers.c"
#include "trust.c"
#include "codecs.c"
#include "checksums.c"
#include "leaks.c"
#include "metadata.c"
#include "inline_layout.c"
#include "validation.c"
#include "frames.c"
#include "tasks.c"
#include "watchdog.c"
#include "net.c"
#include "io.c"
#include "fs.c"
#include "watch.c"
#include "process.c"
#include "terminal.c"
#include "locks.c"
#include "foreign.c"
#include "parallel.c"

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
