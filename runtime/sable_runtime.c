// The Sable runtime: the small amount of native code a compiled program
// links against (docs/06 §6.1).
//
// Writing a line, stopping the way the language says a program stops, and the
// heap: counted objects, strings and copy-on-write arrays. Every counted
// object starts with the header of docs/06 §6.2. Counts are not atomic: one
// task runs at a time (tasks.c).
//
// This file is the runtime: it includes its parts, in this order, as one
// translation unit, compiled to one object (or, for link-time optimisation,
// one module of bitcode). The parts keep their helpers 'static', and every
// one of them stays inlinable into the next in every build, not only an
// optimised one. What the operating system gives is the platform layer's
// (platform.h); the parts after it are the same on every system.

#include "internal.h"
#include "platform.h"

#ifdef _WIN32
#include "platform_win32.c"
#else
#include "platform_posix.c"
#endif

#include "objects.c"
#include "memory.c"
#include "exclusivity.c"
#include "references.c"
#include "traps.c"
#include "strings.c"
#include "arrays.c"
#include "leaks.c"
#include "inline_layout.c"
#include "validation.c"
#include "tasks.c"
#include "io.c"
#include "process.c"
