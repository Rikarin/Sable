// The Sable runtime's terminal for Windows, its console: what platform.h says
// of terminals. Part of platform_win32.c, which includes it.

#ifndef ENABLE_VIRTUAL_TERMINAL_PROCESSING
#define ENABLE_VIRTUAL_TERMINAL_PROCESSING 0x0004
#endif
#ifndef ENABLE_VIRTUAL_TERMINAL_INPUT
#define ENABLE_VIRTUAL_TERMINAL_INPUT 0x0200
#endif

static HANDLE terminal_handle(int stream) {
    return GetStdHandle(stream == 0 ? STD_INPUT_HANDLE : stream == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE);
}

// ---- What a stream is ------------------------------------------------------

static bool terminal_is(int stream) {
    DWORD mode;
    return GetConsoleMode(terminal_handle(stream), &mode) != FALSE;
}

// A console reads escape sequences once it is asked to, from Windows 10 1511,
// and goes on doing so once the program has ended, as a terminal would.
static bool terminal_styles(int stream) {
    HANDLE handle = terminal_handle(stream);
    DWORD mode;
    if (!GetConsoleMode(handle, &mode)) {
        return false;
    }

    DWORD wanted = mode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING;
    return mode == wanted || SetConsoleMode(handle, wanted) != FALSE;
}

static bool terminal_size(int stream, int *columns, int *rows) {
    CONSOLE_SCREEN_BUFFER_INFO info;
    if (!GetConsoleScreenBufferInfo(terminal_handle(stream), &info)) {
        return false;
    }

    *columns = info.srWindow.Right - info.srWindow.Left + 1;
    *rows = info.srWindow.Bottom - info.srWindow.Top + 1;
    return *columns > 0 && *rows > 0;
}

// ---- Raw mode --------------------------------------------------------------

// What is to be undone however the program ends: a console keeps the mode it
// was given once the program that gave it is gone.
static DWORD terminal_before;
static volatile LONG terminal_mode_now;
static volatile LONG terminal_shown;

static void terminal_write(const char *text) {
    DWORD written;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), text, (DWORD)strlen(text), &written, NULL);
}

static void terminal_restore(void) {
    LONG shown = InterlockedExchange(&terminal_shown, 0);
    if (shown & TERMINAL_ALTERNATE) {
        terminal_write("\x1b[?1049l");
    }

    if (shown & TERMINAL_NO_CURSOR) {
        terminal_write("\x1b[?25h");
    }

    if (InterlockedExchange(&terminal_mode_now, 0) != 0) {
        SetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), terminal_before);
    }
}

// The console's events are platform_process_win32.c's handler's, which puts
// the terminal back before one that ends the program.
static void terminal_watch(void) {
    console_events();
}

static int terminal_mode(int mode) {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    LONG before = InterlockedCompareExchange(&terminal_mode_now, 0, 0);
    DWORD now;
    if (!GetConsoleMode(input, &now)) {
        return -1;
    }

    if (before == mode) {
        return (int)before;
    }

    if (mode == 0) {
        bool done = SetConsoleMode(input, terminal_before) != FALSE;
        InterlockedExchange(&terminal_mode_now, 0);
        return done ? (int)before : -1;
    }

    if (before == 0) {
        terminal_before = now;
    }

    DWORD raw = (terminal_before & ~(DWORD)(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT)) | ENABLE_VIRTUAL_TERMINAL_INPUT;
    if (mode == TERMINAL_RAW_ALL) {
        raw &= ~(DWORD)ENABLE_PROCESSED_INPUT;
    }

    terminal_watch();
    InterlockedExchange(&terminal_mode_now, mode);
    if (!SetConsoleMode(input, raw)) {
        InterlockedExchange(&terminal_mode_now, before);
        return -1;
    }

    return (int)before;
}

static void terminal_show(int shown) {
    terminal_watch();
    InterlockedExchange(&terminal_shown, shown);
}

// ---- Reading keys ----------------------------------------------------------

// What was typed, as UTF-8: with the console's virtual terminal input, a key
// that is no character comes as the sequence a terminal sends, an event for
// each of its characters. Read as events, since ReadFile gives nothing but
// ASCII on older consoles. One reader at a time (Console's lock).
static char terminal_typed[16];
static int terminal_typed_count, terminal_typed_at;
static WCHAR terminal_high;

static void terminal_take(const INPUT_RECORD *record) {
    if (record->EventType != KEY_EVENT) {
        return;
    }

    const KEY_EVENT_RECORD *key = &record->Event.KeyEvent;
    WCHAR unit = key->uChar.UnicodeChar;
    // A key that is let go, but for Alt after a character entered by its
    // number, which comes then.
    if (unit == 0 || (!key->bKeyDown && key->wVirtualKeyCode != VK_MENU)) {
        return;
    }

    if (unit >= 0xD800 && unit < 0xDC00) {
        terminal_high = unit;
        return;
    }

    WCHAR units[2] = { terminal_high, unit };
    bool pair = terminal_high != 0 && unit >= 0xDC00 && unit < 0xE000;
    terminal_high = 0;
    terminal_typed_at = 0;
    int one = WideCharToMultiByte(CP_UTF8, 0, pair ? units : &unit, pair ? 2 : 1, terminal_typed, (int)sizeof terminal_typed, NULL, NULL);
    terminal_typed_count = one;
    // A key held down, as many times as fit.
    for (WORD again = 1; again < key->wRepeatCount && terminal_typed_count + one <= (int)sizeof terminal_typed; again++) {
        memcpy(terminal_typed + terminal_typed_count, terminal_typed, (size_t)one);
        terminal_typed_count += one;
    }
}

static int terminal_read(int64_t milliseconds) {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    int64_t until = milliseconds < 0 ? 0 : sable_time_monotonic() + milliseconds * 1000000;
    while (terminal_typed_at >= terminal_typed_count) {
        DWORD left = INFINITE;
        if (milliseconds >= 0) {
            int64_t rest = (until - sable_time_monotonic() + 999999) / 1000000;
            left = rest < 0 ? 0 : rest > 1000000000 ? 1000000000 : (DWORD)rest;
        }

        DWORD waited = WaitForSingleObject(input, left);
        if (waited == WAIT_TIMEOUT) {
            if (left == 0) {
                return TERMINAL_NOTHING;
            }

            continue;
        }

        INPUT_RECORD record;
        DWORD count;
        if (waited != WAIT_OBJECT_0 || !ReadConsoleInputW(input, &record, 1, &count)) {
            return TERMINAL_ENDED;
        }

        if (count == 1) {
            terminal_take(&record);
        }
    }

    return (unsigned char)terminal_typed[terminal_typed_at++];
}

// ---- Pseudoterminals -------------------------------------------------------

// Windows's (ConPTY) has no file that another program's streams can be made.
struct Pty {
    int unused;
};

static Pty *pty_open(void) {
    return NULL;
}

static const char *pty_name(const Pty *pty) {
    (void)pty;
    return "";
}

static void pty_close(Pty *pty) {
    (void)pty;
}

static int pty_read(Pty *pty, int64_t milliseconds) {
    (void)pty;
    (void)milliseconds;
    return TERMINAL_ENDED;
}

static bool pty_write(Pty *pty, int byte) {
    (void)pty;
    (void)byte;
    return false;
}

static bool pty_resize(Pty *pty, int columns, int rows) {
    (void)pty;
    (void)columns;
    (void)rows;
    return false;
}

static int pty_mode(const Pty *pty) {
    (void)pty;
    return -1;
}

static bool pty_attach(const char *name, bool controlling) {
    (void)name;
    (void)controlling;
    return false;
}
