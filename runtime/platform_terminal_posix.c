// The Sable runtime's terminal for POSIX systems: what platform.h says of
// terminals. Part of platform_posix.c, which includes it.

// ---- What a stream is ------------------------------------------------------

static bool terminal_is(int stream) {
    return isatty(stream) == 1;
}

// The terminal reads the escape sequences of itself.
static bool terminal_styles(int stream) {
    return terminal_is(stream);
}

static bool terminal_size(int stream, int *columns, int *rows) {
    struct winsize size;
    if (ioctl(stream, TIOCGWINSZ, &size) != 0 || size.ws_col == 0 || size.ws_row == 0) {
        return false;
    }

    *columns = size.ws_col;
    *rows = size.ws_row;
    return true;
}

// ---- Raw mode --------------------------------------------------------------

// What is to be undone however the program ends: the mode standard input's
// terminal had before, and what is shown on standard output's. Written where
// no handler is running on the same thread, and read in them.
static struct termios terminal_before;
static int terminal_mode_now;
static int terminal_shown;
static pid_t terminal_owner;

// A change by a program in the background would stop it (SIGTTOU), which is
// no time for that: what it left behind is put back all the same.
static bool terminal_apply(const struct termios *mode) {
    sigset_t output, before;
    sigemptyset(&output);
    sigaddset(&output, SIGTTOU);
    pthread_sigmask(SIG_BLOCK, &output, &before);
    int done;
    // TCSANOW, as waiting for output to drain waits for who reads it.
    while ((done = tcsetattr(STDIN_FILENO, TCSANOW, mode)) != 0 && errno == EINTR) {
    }

    pthread_sigmask(SIG_SETMASK, &before, NULL);
    return done == 0;
}

// Keys as they are typed, shown nowhere; output as it was, a line feed
// still starting the next line.
static struct termios terminal_raw(int mode) {
    struct termios raw = terminal_before;
    raw.c_iflag &= ~(tcflag_t)(BRKINT | PARMRK | ISTRIP | INLCR | IGNCR | ICRNL | IXON);
    raw.c_lflag &= ~(tcflag_t)(ECHO | ECHONL | ICANON | IEXTEN);
    if (mode == TERMINAL_RAW_ALL) {
        raw.c_lflag &= ~(tcflag_t)ISIG;
    }

    raw.c_cc[VMIN] = 1;
    raw.c_cc[VTIME] = 0;
    return raw;
}

static void terminal_write(const char *text) {
    size_t left = strlen(text);
    while (left > 0) {
        ssize_t put = write(STDOUT_FILENO, text, left);
        if (put > 0) {
            text += put;
            left -= (size_t)put;
        } else if (errno != EINTR) {
            return;
        }
    }
}

// What there is to undo, undone, and still noted. Safe in a signal's
// handler; nothing in a child forked from this program.
static void terminal_undo(void) {
    if (terminal_owner != getpid()) {
        return;
    }

    int shown = __atomic_load_n(&terminal_shown, __ATOMIC_ACQUIRE);
    if (shown & TERMINAL_ALTERNATE) {
        terminal_write("\x1b[?1049l");
    }

    if (shown & TERMINAL_NO_CURSOR) {
        terminal_write("\x1b[?25h");
    }

    if (__atomic_load_n(&terminal_mode_now, __ATOMIC_ACQUIRE) != 0) {
        terminal_apply(&terminal_before);
    }
}

static void terminal_restore(void) {
    terminal_undo();
    __atomic_store_n(&terminal_shown, 0, __ATOMIC_RELEASE);
    __atomic_store_n(&terminal_mode_now, 0, __ATOMIC_RELEASE);
}

// The signals that end a program, or stop it, and that a handler can do
// something before: SIGSEGV and SIGBUS are on_fault's.
static const int terminal_signals[] = { SIGHUP, SIGINT, SIGQUIT, SIGILL, SIGTRAP, SIGABRT, SIGFPE, SIGUSR1, SIGUSR2,
                                        SIGALRM, SIGTERM, SIGTSTP, SIGXCPU, SIGXFSZ, SIGVTALRM, SIGPROF, SIGSYS };

// The terminal put back, and then what the signal does without a handler: it
// is sent again, and comes as this returns.
static void on_terminal_signal(int number) {
    int error = errno;
    struct sigaction nothing, own;
    memset(&nothing, 0, sizeof nothing);
    nothing.sa_handler = SIG_DFL;
    if (number != SIGTSTP) {
        terminal_restore();
        sigaction(number, &nothing, NULL);
        raise(number);
        errno = error;
        return;
    }

    // Stopped: it stops here, and goes on from here, with the terminal as
    // the program had it. What was on the alternate screen is the program's
    // to draw again (SIGCONT).
    terminal_undo();
    sigaction(number, &nothing, &own);
    sigset_t stop, before;
    sigemptyset(&stop);
    sigaddset(&stop, number);
    raise(number);
    pthread_sigmask(SIG_UNBLOCK, &stop, &before);
    pthread_sigmask(SIG_SETMASK, &before, NULL);
    sigaction(number, &own, NULL);
    int mode = __atomic_load_n(&terminal_mode_now, __ATOMIC_ACQUIRE);
    if (mode != 0) {
        struct termios raw = terminal_raw(mode);
        terminal_apply(&raw);
    }

    int shown = __atomic_load_n(&terminal_shown, __ATOMIC_ACQUIRE);
    if (shown & TERMINAL_ALTERNATE) {
        terminal_write("\x1b[?1049h");
    }

    if (shown & TERMINAL_NO_CURSOR) {
        terminal_write("\x1b[?25l");
    }

    errno = error;
}

// The handler set for each of the signals that has none; one that is ignored
// stays so, and one the program catches (Signals) is the program's. It stays
// set, and does no more than the signal would where there is nothing to
// undo.
static void terminal_watch(void) {
    if (__atomic_load_n(&terminal_mode_now, __ATOMIC_ACQUIRE) == 0 && __atomic_load_n(&terminal_shown, __ATOMIC_ACQUIRE) == 0) {
        return;
    }

    for (size_t i = 0; i < sizeof terminal_signals / sizeof terminal_signals[0]; i++) {
        struct sigaction now;
        if (sigaction(terminal_signals[i], NULL, &now) == 0 && !(now.sa_flags & SA_SIGINFO) && now.sa_handler == SIG_DFL) {
            memset(&now, 0, sizeof now);
            now.sa_handler = on_terminal_signal;
            now.sa_flags = SA_RESTART | SA_ONSTACK;
            sigemptyset(&now.sa_mask);
            sigaction(terminal_signals[i], &now, NULL);
        }
    }
}

static int terminal_mode(int mode) {
    if (!terminal_is(STDIN_FILENO)) {
        return -1;
    }

    int before = __atomic_load_n(&terminal_mode_now, __ATOMIC_ACQUIRE);
    if (before == mode) {
        return before;
    }

    if (mode == 0) {
        bool done = terminal_apply(&terminal_before);
        __atomic_store_n(&terminal_mode_now, 0, __ATOMIC_RELEASE);
        return done ? before : -1;
    }

    if (before == 0 && tcgetattr(STDIN_FILENO, &terminal_before) != 0) {
        return -1;
    }

    // Noted first: a signal from here on puts back what there was.
    terminal_owner = getpid();
    __atomic_store_n(&terminal_mode_now, mode, __ATOMIC_RELEASE);
    terminal_watch();
    struct termios raw = terminal_raw(mode);
    if (!terminal_apply(&raw)) {
        __atomic_store_n(&terminal_mode_now, before, __ATOMIC_RELEASE);
        return -1;
    }

    return before;
}

static void terminal_show(int shown) {
    terminal_owner = getpid();
    __atomic_store_n(&terminal_shown, shown, __ATOMIC_RELEASE);
    terminal_watch();
}

// ---- Reading keys ----------------------------------------------------------

// A byte of the file within so many milliseconds, or however long for less
// than none.
static int byte_within(int fd, int64_t milliseconds) {
    int64_t until = milliseconds < 0 ? 0 : sable_time_monotonic() + milliseconds * 1000000;
    for (;;) {
        int64_t left = -1;
        if (milliseconds >= 0) {
            left = (until - sable_time_monotonic() + 999999) / 1000000;
            left = left < 0 ? 0 : left > 1000000000 ? 1000000000 : left;
        }

        struct pollfd ready = { .fd = fd, .events = POLLIN };
        int count = poll(&ready, 1, (int)left);
        if (count == 0) {
            if (left == 0) {
                return TERMINAL_NOTHING;
            }

            continue;
        }

        if (count < 0) {
            if (errno == EINTR) {
                continue;
            }

            return TERMINAL_ENDED;
        }

        unsigned char byte;
        ssize_t got = read(fd, &byte, 1);
        if (got == 1) {
            return byte;
        }

        if (got == 0 || (errno != EINTR && errno != EAGAIN)) {
            return TERMINAL_ENDED;
        }
    }
}

static int terminal_read(int64_t milliseconds) {
    return byte_within(STDIN_FILENO, milliseconds);
}

// ---- Pseudoterminals -------------------------------------------------------

// The program's side is kept open too, so that what another program wrote to
// it is there to read once that has ended, and its mode to ask.
struct Pty {
    int own, programs;
    char name[256];
};

static Pty *pty_open(void) {
    Pty *pty = calloc(1, sizeof *pty);
    if (pty == NULL) {
        return NULL;
    }

    pty->programs = -1;
    // Under the spawn lock, so that no child is started with them open, and
    // ptsname's buffer is the one caller's.
    pthread_mutex_lock(&spawn_lock);
    pty->own = posix_openpt(O_RDWR | O_NOCTTY);
    if (pty->own >= 0) {
        fcntl(pty->own, F_SETFD, FD_CLOEXEC);
        const char *name = grantpt(pty->own) == 0 && unlockpt(pty->own) == 0 ? ptsname(pty->own) : NULL;
        if (name != NULL && strlen(name) < sizeof pty->name) {
            strcpy(pty->name, name);
            pty->programs = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
        }
    }

    pthread_mutex_unlock(&spawn_lock);
    if (pty->programs >= 0) {
        return pty;
    }

    if (pty->own >= 0) {
        close(pty->own);
    }

    free(pty);
    return NULL;
}

static const char *pty_name(const Pty *pty) {
    return pty->name;
}

static void pty_close(Pty *pty) {
    close(pty->programs);
    close(pty->own);
    free(pty);
}

static int pty_read(Pty *pty, int64_t milliseconds) {
    return byte_within(pty->own, milliseconds);
}

static bool pty_write(Pty *pty, int byte) {
    unsigned char one = (unsigned char)byte;
    ssize_t put;
    while ((put = write(pty->own, &one, 1)) < 0 && errno == EINTR) {
    }

    return put == 1;
}

static bool pty_resize(Pty *pty, int columns, int rows) {
    struct winsize size = { .ws_row = (unsigned short)rows, .ws_col = (unsigned short)columns };
    return ioctl(pty->own, TIOCSWINSZ, &size) == 0;
}

static int pty_mode(const Pty *pty) {
    struct termios mode;
    if (tcgetattr(pty->programs, &mode) != 0 && tcgetattr(pty->own, &mode) != 0) {
        return -1;
    }

    return (mode.c_lflag & ICANON ? PTY_LINES : 0) | (mode.c_lflag & ECHO ? PTY_ECHO : 0) | (mode.c_lflag & ISIG ? PTY_SIGNALS : 0);
}

// Not the terminal's own, the program is put in a process group of its own,
// as a shell puts a job: a group whose parent is in another of the same
// session is not orphaned, and is stopped by SIGTSTP, as at a terminal.
static bool pty_attach(const char *name, bool controlling) {
    if (controlling ? setsid() < 0 : setpgid(0, 0) != 0) {
        return false;
    }

    int terminal = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (terminal < 0) {
        return false;
    }

    bool done = !controlling || ioctl(terminal, TIOCSCTTY, 0) == 0;
    for (int i = 0; i < 3 && done; i++) {
        done = dup2(terminal, i) == i;
    }

    if (terminal > 2) {
        close(terminal);
    }

    return done;
}
