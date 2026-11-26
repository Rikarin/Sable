// The Sable runtime: the terminal a program's streams are, as Sable.IO's
// Console sees it, and the pseudoterminals its tests run under.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- The terminal ----------------------------------------------------------

bool sable_terminal_is(int64_t stream) {
    return terminal_is((int)stream);
}

// Whether the stream is a terminal that reads escape sequences, asked to if
// it must be.
bool sable_terminal_styles(int64_t stream) {
    return terminal_styles((int)stream);
}

// The stream's terminal's columns, shifted left by 32, and its rows; 0 where
// there is no saying.
int64_t sable_terminal_size(int64_t stream) {
    int columns, rows;
    return terminal_size((int)stream, &columns, &rows) ? ((int64_t)columns << 32) | rows : 0;
}

// Standard input's terminal's mode: 0 as it was, TERMINAL_RAW or
// TERMINAL_RAW_ALL. The one it had; -1 where it has none to change. One
// caller at a time (Console's lock).
int64_t sable_terminal_mode(int64_t mode) {
    return terminal_mode((int)mode);
}

// What standard output's terminal shows, to be undone as the program ends:
// TERMINAL_ALTERNATE, TERMINAL_NO_CURSOR. Said before it is shown.
void sable_terminal_show(int64_t shown) {
    terminal_show((int)shown);
}

// A byte typed, within so many milliseconds, or however long for less than
// none; or TERMINAL_ENDED, TERMINAL_NOTHING. What was written is shown
// first.
int64_t sable_terminal_read(int64_t milliseconds) {
    fflush(stdout);
    return terminal_read(milliseconds);
}

// A program that ends in raw mode, or on the alternate screen, leaves the
// terminal as it found it.
SABLE_AT_EXIT(terminal_at_exit) {
    fflush(stdout);
    terminal_restore();
}

// ---- Pseudoterminals -------------------------------------------------------

// A handle; 0 where the system has none such.
int64_t sable_pty_open(void) {
    return (int64_t)(intptr_t)pty_open();
}

// The file a program's streams are to be, for it to be the program's
// terminal.
SableString *sable_pty_name(int64_t pty) {
    return make_ctext(pty_name((Pty *)(intptr_t)pty));
}

enum { PTY_CALL_CLOSE, PTY_CALL_READ, PTY_CALL_WRITE, PTY_CALL_RESIZE, PTY_CALL_MODE };

int64_t sable_pty_call(int64_t pty, int64_t call, int64_t argument) {
    Pty *one = (Pty *)(intptr_t)pty;
    switch (call) {
    case PTY_CALL_CLOSE:
        pty_close(one);
        return 0;
    case PTY_CALL_READ:
        return pty_read(one, argument);
    case PTY_CALL_WRITE:
        return pty_write(one, (int)argument);
    case PTY_CALL_RESIZE:
        return pty_resize(one, (int)(argument >> 32), (int)(argument & 0x7fffffff));
    default:
        return pty_mode(one);
    }
}

// This program's streams made the terminal's, and it the program's own (its
// Ctrl+C is this program's) if that is asked.
bool sable_pty_attach(const SableString *name, bool controlling) {
    fflush(stdout);
    char *path = c_string(name);
    bool done = pty_attach(path, controlling);
    free(path);
    return done;
}
