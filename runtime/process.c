// The Sable runtime: processes as Sable.Process sees them.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Processes (Sable.Process's floor) ----------------------------------

static int64_t process_last_status;

// Plain memory, not a Sable string: the runtime keeps it past the program's
// own objects, and the leak check counts only those.
static char *process_last_errors;
static size_t process_last_errors_length;

int64_t sable_process_status(void) {
    return process_last_status;
}

SableString *sable_process_error_output(void) {
    return make_text(process_last_errors == NULL ? "" : process_last_errors, (int64_t)process_last_errors_length);
}

// Runs the program, found on PATH, to its end (run_program, the
// platform's): the input written to it and its output and errors read as
// they come. Gives what it wrote to standard output; how it ended and its
// errors are kept for the calls above.
SableString *sable_process_run(const SableString *program, const SableArray *arguments, const SableString *input) {
    int64_t count = arguments == NULL ? 0 : arguments->count;
    char **argv = malloc((size_t)(count + 2) * sizeof(char *));
    argv[0] = c_string(program);
    for (int64_t i = 0; i < count; i++) {
        argv[i + 1] = c_string(((SableString **)arguments->elements)[i]);
    }

    argv[count + 1] = NULL;

    Buffer output = { 0 }, errors = { 0 };
    int64_t status = 0;
    bool ran = run_program(argv, input->bytes, input->length, &output, &errors, &status);
    for (int64_t i = 0; i <= count; i++) {
        free(argv[i]);
    }

    free(argv);
    if (!ran) {
        free(output.bytes);
        free(errors.bytes);
        io_last_failed = true;
        return make_string("", 0);
    }

    process_last_status = status;
    free(process_last_errors);
    process_last_errors = errors.bytes;
    process_last_errors_length = errors.length;
    SableString *result = make_text(output.bytes == NULL ? "" : output.bytes, (int64_t)output.length);
    free(output.bytes);
    io_last_failed = false;
    return result;
}

SableString *sable_process_current_directory(void) {
    char *path = current_directory();
    if (path == NULL) {
        return make_string("", 0);
    }

    SableString *result = make_ctext(path);
    free(path);
    return result;
}

bool sable_process_change_directory(const SableString *path) {
    char *name = c_string(path);
    bool changed = change_directory(name);
    free(name);
    return changed;
}

int64_t sable_process_id(void) {
    return process_id();
}

// The path of this program's executable, so that the program can start
// itself again (the test harness does, a process per test): as the
// platform knows it (executable_path).
SableString *sable_process_executable_path(void) {
    char *path = executable_path(saved_argc, saved_argv);
    if (path == NULL) {
        return make_string("", 0);
    }

    SableString *result = make_ctext(path);
    free(path);
    return result;
}
