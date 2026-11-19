// The Sable runtime: the operating system as Sable.IO sees it — arguments, standard streams, files, the environment.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- The operating system (Sable.IO's floor) ----------------------------

// A call that can fail gives a value either way — an empty string, an empty
// array — and sets this; the compiler reads it straight after, to make the
// optional. One task runs at a time, so one flag serves them all.
static bool io_last_failed;

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
    trap_report(message->bytes, (int)message->length, site->length == 0 ? NULL : place, 3);
}

// The frames of the calls that led to the caller, a line of text each, the
// caller's first (this function's own is left out): what
// Backtrace.capture() holds.
__attribute__((noinline)) SableArray *sable_backtrace_capture(void) {
    void *frames[BACKTRACE_DEPTH];
    int count = capture_frames(frames, BACKTRACE_DEPTH);
    SableArray *lines = NULL;
    for (int i = 1; i < count; i++) {
        char text[600];
        name_frame(frames[i], text, sizeof text);
        *(SableString **)sable_array_append_slot(&lines, &string_array_metadata) = make_ctext(text);
    }

    return lines;
}
