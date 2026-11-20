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
static void print_task_backtrace(void);

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

    print_task_backtrace();
}

// The trap as it is said with no panic hook: the message, and the place
// on the next line.
static void print_trap(const char *message, int message_length, const char *site) {
    fprintf(stderr, "fatal error: %.*s\n", message_length, message);
    if (site != NULL) {
        fprintf(stderr, "  at %s\n", site);
    }
}

// The program's panic hook (Panic.setHook), run with the trap and the
// frames of the calls that led to it ('own' as for print_backtrace); false
// when the program has none, and the default report is said instead. In
// io.c, where strings and arrays are made.
static bool run_trap_hook(const char *message, int message_length, const char *site, int own);

// How a trap ends the program: with 0 as a trap does (stop_trapped, status
// 134); with another status as main's error does (1), standard output and
// error written out first.
_Noreturn static void stop_with(int status) {
    coverage_write();
    if (status == 0) {
        stop_trapped();
    }

    fflush(stdout);
    fflush(stderr);
    _Exit(status);
}

// The first trap ends the program: one on another thread at the same time
// — a trap can happen on any of the pool's threads — waits for it to. The
// thread saying a trap remembers it, so that a trap in the panic hook can
// still say what the hook was given.
static int trap_claimed;
static _Thread_local bool trap_reporting;
static const char *first_message, *first_site;
static int first_length;

// The trap, said and stopped at; 'own' as for print_backtrace, 'status' as
// for stop_with. The program's panic hook says it, if it set one; a trap
// in the hook falls back to the default report, of the trap the hook was
// given and then of its own.
__attribute__((noinline)) _Noreturn static void trap_report(const char *message, int message_length, const char *site, int own, int status) {
    fflush(stdout);
    if (trap_reporting) {
        print_trap(first_message, first_length, first_site);
        fprintf(stderr, "fatal error: the panic hook trapped: %.*s\n", message_length, message);
        if (site != NULL) {
            fprintf(stderr, "  at %s\n", site);
        }

        stop_with(0);
    }

    if (__atomic_exchange_n(&trap_claimed, 1, __ATOMIC_ACQ_REL) != 0) {
        for (;;) {
            sable_time_sleep(1000000000);
        }
    }

    trap_reporting = true;
    first_message = message;
    first_length = message_length;
    first_site = site;
    if (!run_trap_hook(message, message_length, site, own)) {
        print_trap(message, message_length, site);
        print_backtrace(own);
    }

    stop_with(status);
}

// A trap with the place in the source it happened at, as 'file:line:column',
// or none: the message on one line, and the place on the next.
__attribute__((noinline)) _Noreturn void sable_trap_at(const char *message, const char *site) {
    // print_backtrace (or run_trap_hook), trap_report and this one.
    trap_report(message, (int)strlen(message), site, 3, 0);
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
