// The Sable runtime: the operating system as Sable.IO sees it — arguments, standard streams, files, the environment.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- The operating system (Sable.IO's floor) ----------------------------

// A call that can fail gives a value either way — an empty string, an empty
// array — and sets this; the compiler reads it straight after, to make the
// optional. One task runs at a time, so one flag serves them all.
static _Thread_local bool io_last_failed;

bool sable_io_succeeded(void) {
    return !io_last_failed;
}

static int saved_argc;
static char **saved_argv;

// The program's arguments, as main() is given them, or as the platform
// has them (platform_arguments).
void sable_set_arguments(int32_t argc, char **argv) {
    saved_argv = platform_arguments(argc, argv, &saved_argc);
}

// The arguments after the program's own name.
int64_t sable_io_argument_count(void) {
    return saved_argc > 1 ? saved_argc - 1 : 0;
}

SableString *sable_io_argument(int64_t index) {
    if (index < 0 || index >= sable_io_argument_count()) {
        trap_here("argument index out of range");
    }

    return make_ctext(saved_argv[index + 1]);
}

// A string as a C path: its bytes, NUL-terminated, in memory the caller frees.
static char *c_string(const SableString *s) {
    char *text = malloc((size_t)s->length + 1);
    if (text == NULL) {
        sable_trap("out of memory");
    }

    memcpy(text, s->bytes, (size_t)s->length);
    text[s->length] = '\0';
    return text;
}

void sable_io_write(const SableString *s) {
    fwrite(s->bytes, 1, (size_t)s->length, stdout);
}

void sable_io_write_error(const SableString *s) {
    fflush(stdout);
    fwrite(s->bytes, 1, (size_t)s->length, stderr);
}

// A line of standard input without its line ending; none at the end of input.
SableString *sable_io_read_line(void) {
    fflush(stdout);
    size_t capacity = 128;
    size_t length = 0;
    char *line = malloc(capacity);
    if (line == NULL) {
        sable_trap("out of memory");
    }

    int c;
    bool any = false;
    while ((c = fgetc(stdin)) != EOF) {
        any = true;
        if (c == '\n') {
            break;
        }

        if (length + 1 >= capacity) {
            capacity *= 2;
            char *grown = realloc(line, capacity);
            if (grown == NULL) {
                free(line);
                sable_trap("out of memory");
            }

            line = grown;
        }

        line[length++] = (char)c;
    }

    if (length > 0 && line[length - 1] == '\r') {
        length--;
    }

    io_last_failed = !any;
    SableString *result = make_text(line, (int64_t)length);
    free(line);
    return result;
}

SableString *sable_io_read_file(const SableString *path) {
    char *name = c_string(path);
    FILE *file = open_file(name, "rb");
    free(name);
    if (file == NULL) {
        io_last_failed = true;
        return make_string("", 0);
    }

    size_t capacity = 4096;
    size_t length = 0;
    char *bytes = malloc(capacity);
    if (bytes == NULL) {
        sable_trap("out of memory");
    }

    size_t read;
    while ((read = fread(bytes + length, 1, capacity - length, file)) > 0) {
        length += read;
        if (length == capacity) {
            capacity *= 2;
            char *grown = realloc(bytes, capacity);
            if (grown == NULL) {
                free(bytes);
                sable_trap("out of memory");
            }

            bytes = grown;
        }
    }

    io_last_failed = ferror(file) != 0;
    fclose(file);
    SableString *result = make_text(bytes, (int64_t)length);
    free(bytes);
    return result;
}

bool sable_io_write_file(const SableString *path, const SableString *text, bool append) {
    char *name = c_string(path);
    FILE *file = open_file(name, append ? "ab" : "wb");
    free(name);
    if (file == NULL) {
        return false;
    }

    bool written = fwrite(text->bytes, 1, (size_t)text->length, file) == (size_t)text->length;
    return fclose(file) == 0 && written;
}

bool sable_io_file_exists(const SableString *path) {
    char *name = c_string(path);
    bool exists = file_exists(name);
    free(name);
    return exists;
}

bool sable_io_is_directory(const SableString *path) {
    char *name = c_string(path);
    bool directory = is_directory(name);
    free(name);
    return directory;
}

// A file, or an empty directory.
bool sable_io_remove_file(const SableString *path) {
    char *name = c_string(path);
    bool removed = remove_file(name);
    free(name);
    return removed;
}

bool sable_io_make_directory(const SableString *path) {
    char *name = c_string(path);
    bool made = make_directory(name);
    free(name);
    return made;
}

static int compare_names(const void *a, const void *b) {
    return strcmp(*(char *const *)a, *(char *const *)b);
}

// The entries' names, sorted byte by byte so that every platform agrees;
// '.' and '..' left out.
SableArray *sable_io_list_directory(const SableString *path) {
    char *name = c_string(path);
    size_t count = 0;
    char **names = list_directory(name, &count);
    free(name);
    SableArray *entries = NULL;
    if (names == NULL) {
        io_last_failed = true;
        return entries;
    }

    qsort(names, count, sizeof(char *), compare_names);
    for (size_t i = 0; i < count; i++) {
        *(SableString **)sable_array_append_slot(&entries, &string_array_metadata) = make_ctext(names[i]);
        free(names[i]);
    }

    free(names);
    io_last_failed = false;
    return entries;
}

SableString *sable_io_environment(const SableString *variable) {
    char *name = c_string(variable);
    char *value = environment_variable(name);
    free(name);
    io_last_failed = value == NULL;
    if (value == NULL) {
        return make_string("", 0);
    }

    SableString *result = make_ctext(value);
    free(value);
    return result;
}

// ---- Traps and backtraces ------------------------------------------------

// A trap the program asks for, with a message and place it made as it ran:
// what fatalError and precondition stop with. An empty place is none.
__attribute__((noinline)) _Noreturn void sable_trap_with(const SableString *message, const SableString *site) {
    char place[512];
    snprintf(place, sizeof place, "%.*s", (int)site->length, site->bytes);
    trap_report(message->bytes, (int)message->length, site->length == 0 ? NULL : place, 3, 0);
}

// An error main threw, which nothing caught (Sable.Core's uncaughtError):
// said as a trap is, through the panic hook, but the program ends with
// status 1, as Rust's and Swift's do, not as an abort.
__attribute__((noinline)) _Noreturn void sable_trap_uncaught(const SableString *message, const SableString *site) {
    char place[512];
    snprintf(place, sizeof place, "%.*s", (int)site->length, site->bytes);
    trap_report(message->bytes, (int)message->length, site->length == 0 ? NULL : place, 3, 1);
}

// The frames from 'first' on, a line of text each.
static SableArray *frame_lines(void **frames, int first, int count) {
    SableArray *lines = NULL;
    for (int i = first; i < count; i++) {
        char text[600];
        name_frame(frames[i], text, sizeof text);
        *(SableString **)sable_array_append_slot(&lines, &string_array_metadata) = make_ctext(text);
    }

    return lines;
}

// The frames of the calls that led to the caller, a line of text each, the
// caller's first (this function's own is left out): what
// Backtrace.capture() holds.
__attribute__((noinline)) SableArray *sable_backtrace_capture(void) {
    void *frames[BACKTRACE_DEPTH];
    int count = capture_frames(frames, BACKTRACE_DEPTH);
    return frame_lines(frames, 1, count);
}

// ---- The panic hook ------------------------------------------------------

// What Panic.setHook (Sable.Diagnostics) sets, through Sable.Core's
// Runtime.setTrapHook: a closure given a trap's message, its place
// ('file:line:column', or empty) and the frames of the calls that led to
// it, a line each, all borrowed. Its context is shared — a trap can happen
// on any thread — and held here until the hook is replaced or taken, or
// the program ends.
typedef void (*TrapHookCode)(SableString *message, SableString *site, SableArray *frames, void *context);

static TrapHookCode trap_hook_code;
static void *trap_hook_context;
static SpinLock trap_hook_lock;

// The hook set, in place of the one before; the closure is borrowed.
void sable_trap_hook_set(TrapHookCode code, void *context) {
    sable_share(context);
    sable_retain(context);
    spin_lock(&trap_hook_lock);
    void *replaced = trap_hook_context;
    trap_hook_code = code;
    trap_hook_context = context;
    spin_unlock(&trap_hook_lock);
    sable_release(replaced);
}

// The hook taken out, leaving none: written to 'taken' as a closure is
// laid out (code, context), owned by the caller. With none set, the
// closure 'otherwise' (borrowed) is given instead: the default report.
void sable_trap_hook_take(void **taken, TrapHookCode otherwise, void *context) {
    spin_lock(&trap_hook_lock);
    TrapHookCode code = trap_hook_code;
    void *held = trap_hook_context;
    trap_hook_code = NULL;
    trap_hook_context = NULL;
    spin_unlock(&trap_hook_lock);
    if (code == NULL) {
        code = otherwise;
        held = context;
        sable_retain(held);
    }

    taken[0] = (void *)code;
    taken[1] = held;
}

// The hook run with a trap (traps.c's trap_report), its frames from the
// caller of the runtime's trap; false when there is none.
__attribute__((noinline)) static bool run_trap_hook(const char *message, int message_length, const char *site, int own) {
    spin_lock(&trap_hook_lock);
    TrapHookCode code = trap_hook_code;
    void *context = trap_hook_context;
    sable_retain(context);
    spin_unlock(&trap_hook_lock);
    if (code == NULL) {
        return false;
    }

    void *frames[BACKTRACE_DEPTH];
    int count = capture_frames(frames, BACKTRACE_DEPTH);
    SableArray *lines = frame_lines(frames, own, count);
    SableString *text = make_text(message, message_length);
    SableString *place = make_ctext(site == NULL ? "" : site);
    code(text, place, lines, context);
    fflush(stdout);
    return true;
}

// The hook let go of as the program ends, before what is left is counted.
static void trap_hook_forget(void) {
    void *context = trap_hook_context;
    trap_hook_code = NULL;
    trap_hook_context = NULL;
    sable_release(context);
}

// ---- File handles (Sable.IO's FileHandle) ----------------------------------------
//
// Over the platform's file calls (platform.h, "File handles"). A call that
// fails returns -1, NULL or false, and 'sable_file_error' then says why,
// by IoError's kinds, and 'sable_file_error_code' the system's own code;
// both are the calling thread's, and a call that succeeds clears them.

static _Thread_local FileError file_last_error;

int64_t sable_file_error(void) {
    return file_last_error.kind;
}

int64_t sable_file_error_code(void) {
    return file_last_error.code;
}

static FileError *file_error_cleared(void) {
    file_last_error.kind = FILE_ERROR_NONE;
    file_last_error.code = 0;
    return &file_last_error;
}

int64_t sable_file_open(const SableString *path, int64_t flags, int64_t permissions) {
    char *name = c_string(path);
    PlatformFile file = file_open(name, (int)flags, (int)permissions, file_error_cleared());
    free(name);
    return file;
}

// The standard streams go through C's stdio too (print, Console): what it
// holds back is written before bytes go to the same stream directly, and
// standard input is read through it, so neither way loses the other's.
static bool is_standard(PlatformFile file, int which) {
    return file == file_standard(which);
}

// Up to 'most' bytes, read from the position (offset < 0) or the offset, as
// a new array: NULL at the end, or on failure.
SableArray *sable_file_read(int64_t file, int64_t most, int64_t offset, const SableMetadata *type) {
    FileError *error = file_error_cleared();
    if (most <= 0) {
        return NULL;
    }

    char *buffer = buffer_alloc(most);
    int64_t got;
    if (offset < 0 && is_standard(file, 0)) {
        got = (int64_t)fread(buffer, 1, (size_t)most, stdin);
        if (got == 0 && ferror(stdin)) {
            error->kind = FILE_ERROR_OTHER;
            got = -1;
        }
    } else {
        got = file_read(file, buffer, most, offset, error);
    }

    if (got <= 0) {
        buffer_free(buffer, most);
        return NULL;
    }

    SableArray *bytes = sable_alloc(type, (int64_t)sizeof(SableArray));
    bytes->elements = buffer;
    bytes->capacity = most;
    bytes->count = got;
    return bytes;
}

// Some of the slice's bytes written at the position (offset < 0) or the
// offset: how many, or -1.
int64_t sable_file_write(int64_t file, const SableArray *buffer, int64_t start, int64_t count, int64_t offset) {
    FileError *error = file_error_cleared();
    if (count == 0) {
        return 0;
    }

    if (is_standard(file, 1)) {
        fflush(stdout);
    } else if (is_standard(file, 2)) {
        fflush(stderr);
    }

    return file_write(file, slice_bytes(buffer, start), count, offset, error);
}

int64_t sable_file_seek(int64_t file, int64_t offset, int64_t whence) {
    return file_seek(file, offset, (int)whence, file_error_cleared());
}

bool sable_file_sync(int64_t file) {
    return file_sync(file, file_error_cleared());
}

bool sable_file_set_length(int64_t file, int64_t length) {
    return file_set_length(file, length, file_error_cleared());
}

// One fact of the file: its size (0), when it was last written in
// nanoseconds since 1970 (1), or what it is (2: 0 a file, 1 a directory,
// 2 anything else); -1 on failure.
int64_t sable_file_metadata(int64_t file, int64_t which) {
    FileMetadata metadata;
    if (!file_metadata(file, &metadata, file_error_cleared())) {
        return -1;
    }

    return which == 0 ? metadata.size : which == 1 ? metadata.modified : metadata.kind;
}

bool sable_file_close(int64_t file) {
    return file_close(file, file_error_cleared());
}

int64_t sable_file_standard(int64_t which) {
    return file_standard((int)which);
}

bool sable_file_copy(const SableString *from, const SableString *to) {
    char *source = c_string(from);
    char *target = c_string(to);
    bool copied = file_copy(source, target, file_error_cleared());
    free(source);
    free(target);
    return copied;
}

// The path made absolute with every link resolved; empty when it is not
// there, sable_file_error then saying why.
SableString *sable_file_canonical(const SableString *path) {
    char *name = c_string(path);
    char *resolved = file_canonical(name, file_error_cleared());
    free(name);
    if (resolved == NULL) {
        return make_string("", 0);
    }

    SableString *result = make_ctext(resolved);
    free(resolved);
    return result;
}

// What stdio holds back for the stream (1 output, 2 error), written now.
void sable_file_flush_standard(int64_t which) {
    fflush(which == 2 ? stderr : stdout);
}
