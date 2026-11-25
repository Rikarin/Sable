// Port of binary-search.sb, with bsearch.
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

static int by_value(const void *a, const void *b) {
    int64_t x = *(const int64_t *)a, y = *(const int64_t *)b;
    return (x > y) - (x < y);
}

static Ints values;

static void setup(void) {
    for (int64_t i = 0; i < 1000000; i++) ints_append(&values, i * 3);
}

static void run(void) {
    int64_t x = 12345, found = 0, total = 0;
    for (int i = 0; i < 2000000; i++) {
        x = next_random(x);
        int64_t key = x % 3000000;
        int64_t *at = bsearch(&key, values.elements, (size_t)values.count, sizeof key, by_value);
        if (at != NULL) {
            found = add_i(found, 1);
            total = add_i(total, at - values.elements);
        }
    }
    bench_printf("%lld %lld\n", (long long)found, (long long)total);
}

BENCH_MAIN_WITH(run, setup)
