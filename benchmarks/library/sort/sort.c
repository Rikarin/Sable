// Port of sort.sb, with qsort.
#include <stdbool.h>
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

static int by_value(const void *a, const void *b) {
    int64_t x = *(const int64_t *)a, y = *(const int64_t *)b;
    return (x > y) - (x < y);
}

static int by_text(const void *a, const void *b) {
    return strcmp(*(char *const *)a, *(char *const *)b);
}

enum { WORDS = 200000 };

static Ints numbers;
static char **words;

static void setup(void) {
    int64_t x = 42;
    for (int i = 0; i < 1000000; i++) {
        x = next_random(x);
        ints_append(&numbers, x);
    }
    words = xrealloc(NULL, WORDS * sizeof(char *));
    for (int i = 0; i < WORDS; i++) {
        x = next_random(x);
        char text[32];
        snprintf(text, sizeof text, "w%lld", (long long)(x % 1000000));
        words[i] = strdup(text);
        if (words[i] == NULL) trap("out of memory");
    }
}

static void run(void) {
    int64_t n = numbers.count;
    int64_t *a = xrealloc(NULL, (size_t)n * sizeof(int64_t));
    memcpy(a, numbers.elements, (size_t)n * sizeof(int64_t));
    qsort(a, (size_t)n, sizeof(int64_t), by_value);
    char **b = xrealloc(NULL, WORDS * sizeof(char *));
    memcpy(b, words, WORDS * sizeof(char *));
    qsort(b, WORDS, sizeof(char *), by_text);

    bool ordered = true;
    for (int64_t i = 1; i < n; i++) {
        if (a[i] < a[i - 1]) ordered = false;
    }
    for (int i = 1; i < WORDS; i++) {
        if (strcmp(b[i], b[i - 1]) < 0) ordered = false;
    }
    bench_printf("%s %lld %lld %s %s\n", ordered ? "true" : "false", (long long)a[0], (long long)a[n / 2], b[0], b[WORDS - 1]);

    free(b);
    free(a);
}

BENCH_MAIN_WITH(run, setup)
