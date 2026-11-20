// The Sable runtime: traps — how a program stops when it cannot go on.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Stopping and writing -------------------------------------------

// Overflow, division by zero, an unreachable point reached: not catchable,
// not unwound. Say why, then stop (docs/06 §6.6).
//
// With SABLE_BACKTRACE set, a trap says where it happened, a frame per line
// (print_backtrace, over the platform's capture_frames and name_frame). A
// stack overflow is a trap too, which the platform catches (platform_start).

// Where the compiled code is: set just before a call into the runtime that
// can trap (an index checked here, an unowned reference read), so that the
// trap can say which line made the call. A trap the compiled code makes
// itself passes its place to sable_trap_at instead. Each thread has its own.
_Thread_local const char *sable_trap_site;

// Whether a trap prints a backtrace: SABLE_BACKTRACE set to anything but
// '0' ('1', or 'full', which also shows the runtime's own frames). Off by
// default, so that a trap's message is the whole of its output.
static int backtrace_wanted(void) {
    const char *setting = getenv("SABLE_BACKTRACE");
    if (setting == NULL || strcmp(setting, "0") == 0) {
        return 0;
    }

    return strcmp(setting, "full") == 0 ? 2 : 1;
}

// The frames of the calls that led to a trap, on standard error: from the
// caller of the runtime function the compiled code called, unless 'full'
// asks for those too. 'own' is how many frames are the runtime's.
__attribute__((noinline)) static void print_backtrace(int own) {
    int wanted = backtrace_wanted();
    if (wanted == 0) {
        return;
    }

    void *frames[BACKTRACE_DEPTH];
    int count = capture_frames(frames, BACKTRACE_DEPTH);
    int first = wanted == 2 ? 0 : own;
    fprintf(stderr, "backtrace:\n");
    for (int i = first; i < count; i++) {
        char text[600];
        name_frame(frames[i], text, sizeof text);
        fprintf(stderr, "  %2d  %s\n", i - first, text);
    }
}

// The trap, said and stopped at; 'own' as for print_backtrace.
__attribute__((noinline)) _Noreturn static void trap_report(const char *message, int message_length, const char *site, int own) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %.*s\n", message_length, message);
    if (site != NULL) {
        fprintf(stderr, "  at %s\n", site);
    }

    print_backtrace(own);
    coverage_write();
    stop_trapped();
}

// A trap with the place in the source it happened at, as 'file:line:column',
// or none: the message on one line, and the place on the next.
__attribute__((noinline)) _Noreturn void sable_trap_at(const char *message, const char *site) {
    // print_backtrace, trap_report and this one.
    trap_report(message, (int)strlen(message), site, 3);
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
