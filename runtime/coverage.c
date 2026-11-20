// The Sable runtime: coverage — how often each block ran, for 'sable test --coverage'.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Coverage --------------------------------------------------------------

// A program built for coverage starts each block of the measured code with
// a call here, with the block's counter (the compiler's Coverage.cs). The
// counts go to the file SABLE_COVERAGE names when the program ends —
// normally or by a trap, since a test that traps ran code too — one line
// 'counter count' per counter that ran. Several test processes append to
// one file; 'sable test' adds their lines up.

static int64_t *coverage_counts;
static int64_t coverage_size;

static void coverage_write(void) {
    if (coverage_counts == NULL) {
        return;
    }

    const char *path = getenv("SABLE_COVERAGE");
    FILE *file = path != NULL && path[0] != '\0' ? fopen(path, "a") : NULL;
    if (file == NULL) {
        return;
    }

    // One write, so that processes appending at once do not interleave lines.
    char *text = NULL;
    size_t length = 0, capacity = 0;
    for (int64_t id = 0; id < coverage_size; id++) {
        if (coverage_counts[id] == 0) {
            continue;
        }

        char line[48];
        int n = snprintf(line, sizeof line, "%lld %lld\n", (long long)id, (long long)coverage_counts[id]);
        if (length + (size_t)n + 1 > capacity) {
            capacity = (capacity + (size_t)n + 1) * 2;
            char *grown = realloc(text, capacity);
            if (grown == NULL) {
                break;
            }

            text = grown;
        }

        memcpy(text + length, line, (size_t)n);
        length += (size_t)n;
    }

    if (text != NULL) {
        fwrite(text, 1, length, file);
        free(text);
    }

    fclose(file);

    // Written once: a trap during exit must not write the counts again.
    free(coverage_counts);
    coverage_counts = NULL;
}

// Blocks run on every thread: the table grows, and counts, under a lock.
static SpinLock coverage_lock;

void sable_coverage_hit(int64_t id) {
    spin_lock(&coverage_lock);
    if (id >= coverage_size) {
        int64_t size = coverage_size == 0 ? 1024 : coverage_size;
        while (size <= id) {
            size *= 2;
        }

        int64_t *grown = realloc(coverage_counts, (size_t)size * sizeof *grown);
        if (grown == NULL) {
            spin_unlock(&coverage_lock);
            return;
        }

        if (coverage_counts == NULL) {
            atexit(coverage_write);
        }

        memset(grown + coverage_size, 0, (size_t)(size - coverage_size) * sizeof *grown);
        coverage_counts = grown;
        coverage_size = size;
    }

    coverage_counts[id]++;
    spin_unlock(&coverage_lock);
}
