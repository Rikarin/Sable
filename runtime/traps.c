// The Sable runtime: traps — how a program stops when it cannot go on.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Stopping and writing -------------------------------------------

// Overflow, division by zero, an unreachable point reached: not catchable,
// not unwound. Say why, then stop (docs/06 §6.6).
//
// With SABLE_BACKTRACE set, a trap says where it happened, a frame per line
// (print_backtrace, the platform's). A stack overflow is a trap too, which
// the platform catches (watch_stack).

// Where the compiled code is: set just before a call into the runtime that
// can trap (an index checked here, an unowned reference read), so that the
// trap can say which line made the call. A trap the compiled code makes
// itself passes its place to sable_trap_at instead. One task runs at a time,
// so one variable serves them all.
const char *sable_trap_site;

// A trap with the place in the source it happened at, as 'file:line:column',
// or none: the message on one line, and the place on the next.
_Noreturn void sable_trap_at(const char *message, const char *site) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    if (site != NULL) {
        fprintf(stderr, "  at %s\n", site);
    }

    print_backtrace();
    stop_trapped();
}

// A trap that has no place in the source: one the whole program reaches —
// memory run out, an object kept alive by its own deinit, every task waiting.
_Noreturn void sable_trap(const char *message) {
    sable_trap_at(message, NULL);
}

// A trap in a runtime function the compiled code called: at the call.
_Noreturn static void trap_here(const char *message) {
    sable_trap_at(message, sable_trap_site);
}
